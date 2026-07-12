using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Platform;

namespace Alife.Function.Speech.CosyVoiceTTS
{
    [Module(
        "Cosy语音合成",
        "基于本地 CosyVoice2 (yinmei) 的语音合成引擎",
        defaultCategory: "Doro的妙妙工具",
        EditorUI = typeof(CosyVoiceSpeechModelUI)
    )]
    public class CosyVoiceSpeechModel :
        ISpeechModel,
        IConfigurable<CosyVoiceSpeechModelConfig>,
        ISystemEvent,
        IAsyncDisposable
    {
        public CosyVoiceSpeechModelConfig? Configuration { get; set; }

        private static readonly ConcurrentDictionary<string, string> FileCache = new();
        private static readonly ConcurrentDictionary<string, Task<string?>> InFlight = new();

        // 超时由每次请求的 CancelAfter 控制
        private readonly HttpClient _http = new()
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private CosyVoiceServiceHost? _host;
        private CancellationTokenSource? _watchdogCts;
        private Task? _watchdogTask;
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private volatile bool _ready;
        private string[] _speakers = Array.Empty<string>();

        public IReadOnlyList<string> Speakers => _speakers;
        public bool IsReady => _ready;

        // 用 Console 输出，避免 ILogger 默认的 info: Namespace.Class[0] 前缀
        private static void Log(string msg) => Console.WriteLine($"[Cosy语音] {msg}");
        private static void LogWarn(string msg) => Console.WriteLine($"[Cosy语音][警告] {msg}");
        private static void LogError(string msg) => Console.WriteLine($"[Cosy语音][错误] {msg}");

        #region 生命周期

        public async Task AwakeAsync(AwakeContext context)
        {
            Configuration ??= new CosyVoiceSpeechModelConfig();
            NormalizeApiUrl();

            _host = new CosyVoiceServiceHost(Configuration);

            // 仅当健康检查通过才复用；端口占用但不健康会在 Start 里强制清理
            if (await ProbeReadyAsync(TimeSpan.FromSeconds(2)))
            {
                Log("检测到已有服务，复用中");
                _ready = true;
                EnsureValidSpeaker();
                await WarmupAsync();
                StartWatchdog();
                return;
            }

            if (!Configuration.AutoStart)
            {
                LogWarn("未开启自动启动，且服务未就绪");
                StartWatchdog();
                return;
            }

            if (!ValidatePaths(out var pathError))
            {
                LogError($"路径无效：{pathError}");
                StartWatchdog();
                return;
            }

            try
            {
                await _startLock.WaitAsync();
                try
                {
                    if (!await ProbeReadyAsync(TimeSpan.FromSeconds(1)))
                    {
                        // 健康失败时强制清理残留（含异常退出留下的 Python），再启动
                        _host.ForceCleanup();
                        _host.Start();
                        Log("服务启动中…");
                    }
                }
                finally
                {
                    _startLock.Release();
                }

                bool ok = await WaitUntilReadyAsync(
                    TimeSpan.FromSeconds(Math.Max(30, Configuration.ReadyTimeoutSeconds)));

                if (ok)
                {
                    _ready = true;
                    EnsureValidSpeaker();
                    await WarmupAsync();
                    Log($"服务就绪，音色 {_speakers.Length} 个");
                }
                else
                {
                    LogWarn($"{Configuration.ReadyTimeoutSeconds} 秒内未就绪，后续请求将继续重试");
                }
            }
            catch (Exception ex)
            {
                LogError($"启动失败：{ex.Message}");
            }

            StartWatchdog();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _watchdogCts?.Cancel();
                if (_watchdogTask != null)
                {
                    try { await _watchdogTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch { /* ignore */ }
                }

                // 正常退出：停掉本插件拉起的服务，释放显存/内存
                _host?.Stop();
                _http.Dispose();
                _startLock.Dispose();
                _watchdogCts?.Dispose();
            }
            catch (Exception ex)
            {
                LogWarn($"释放资源时异常：{ex.Message}");
            }
        }

        #endregion

        #region TTS

        public async Task<string?> GenerateSpeechFileAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            Configuration ??= new CosyVoiceSpeechModelConfig();
            NormalizeApiUrl();

            text = text.Trim();
            string spkid = string.IsNullOrWhiteSpace(Configuration.ReferenceAudio)
                ? "女主持"
                : Configuration.ReferenceAudio.Trim();
            string instruct = Configuration.Instruct?.Trim() ?? "";
            float speed = Configuration.Speed <= 0 ? 1.0f : Configuration.Speed;

            string hash = ComputeHash(text, spkid, speed, instruct, Configuration.ApiUrl);
            string outputPath = Path.Combine(AlifePath.TempFolderPath, $"cosy_{hash}.wav");

            if (File.Exists(outputPath))
                return outputPath;

            if (FileCache.TryGetValue(hash, out var cached) && File.Exists(cached))
                return cached;

            // 同句飞行去重：并发相同文本只打一次 API
            var task = InFlight.GetOrAdd(hash, _ =>
                SynthesizeAndUntrackAsync(hash, text, spkid, speed, instruct, outputPath));

            if (!cancellationToken.CanBeCanceled)
                return await task.ConfigureAwait(false);

            // 调用方可取消等待，但不取消已在飞的合成
            var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var reg = cancellationToken.Register(() => cancelTcs.TrySetResult());
            var finished = await Task.WhenAny(task, cancelTcs.Task).ConfigureAwait(false);
            if (finished != task)
                throw new OperationCanceledException(cancellationToken);
            return await task.ConfigureAwait(false);
        }

        private async Task<string?> SynthesizeAndUntrackAsync(
            string hash,
            string text,
            string spkid,
            float speed,
            string instruct,
            string outputPath)
        {
            try
            {
                return await SynthesizeCoreAsync(
                    text, spkid, speed, instruct, outputPath, hash, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                InFlight.TryRemove(hash, out _);
            }
        }

        private async Task<string?> SynthesizeCoreAsync(
            string text,
            string spkid,
            float speed,
            string instruct,
            string outputPath,
            string hash,
            CancellationToken cancellationToken)
        {
            if (!_ready)
            {
                bool ok = await WaitUntilReadyAsync(
                    TimeSpan.FromSeconds(Math.Min(60, Configuration!.ReadyTimeoutSeconds)),
                    cancellationToken);
                if (ok) _ready = true;
            }

            object payload = string.IsNullOrEmpty(instruct)
                ? new { text, spkid, speed, stream = false }
                : new { text, spkid, speed, instruct, stream = false };

            string url = Configuration!.ApiUrl.TrimEnd('/') + "/tts";
            int maxRetry = 8;
            int timeoutSec = Math.Max(10, Configuration.RequestTimeoutSeconds);
            HttpResponseMessage? resp = null;

            for (int retry = 0; retry < maxRetry; retry++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

                    resp = await _http.PostAsync(url, content, timeoutCts.Token)
                        .ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode)
                        break;

                    string body = await resp.Content.ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);
                    LogWarn($"合成请求失败（{(int)resp.StatusCode}），{Truncate(body, 120)}");

                    // 4xx 参数问题不重试
                    if ((int)resp.StatusCode is >= 400 and < 500 and not 408 and not 429)
                    {
                        resp.Dispose();
                        return null;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (
                    ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
                {
                    if (retry == 0)
                        Log("服务未就绪，等待中…");
                    else if (retry % 3 == 0)
                        Log($"仍在等待服务（{retry + 1}/{maxRetry}）");
                }

                resp?.Dispose();
                resp = null;

                int waitMs = Math.Min(10_000, 1000 * (1 << Math.Min(retry, 3)));
                await Task.Delay(waitMs, cancellationToken).ConfigureAwait(false);
            }

            if (resp == null || !resp.IsSuccessStatusCode)
            {
                LogError($"合成失败，已重试 {maxRetry} 次");
                resp?.Dispose();
                return null;
            }

            try
            {
                byte[] audio = await resp.Content.ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (audio.Length < 44)
                {
                    LogError($"返回音频过短（{audio.Length} 字节）");
                    return null;
                }

                string tmp = outputPath + ".tmp";
                await File.WriteAllBytesAsync(tmp, audio, cancellationToken).ConfigureAwait(false);
                File.Move(tmp, outputPath, overwrite: true);

                FileCache[hash] = outputPath;
                return outputPath;
            }
            finally
            {
                resp.Dispose();
            }
        }

        #endregion

        #region 就绪 / 健康 / 预热

        private void StartWatchdog()
        {
            _watchdogCts?.Cancel();
            _watchdogCts = new CancellationTokenSource();
            var ct = _watchdogCts.Token;

            _watchdogTask = Task.Run(async () =>
            {
                int failStreak = 0;
                var started = DateTime.UtcNow;

                while (!ct.IsCancellationRequested)
                {
                    int delaySec = (DateTime.UtcNow - started).TotalMinutes < 2 ? 8 : 20;
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySec), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    try
                    {
                        bool ok = await HealthCheckAsync(ct).ConfigureAwait(false);
                        if (ok)
                        {
                            if (!_ready)
                                Log("服务已恢复");
                            _ready = true;
                            failStreak = 0;
                            continue;
                        }

                        failStreak++;
                        _ready = false;

                        // 连续失败才打日志，避免刷屏
                        if (failStreak == 1 || failStreak % 3 == 0)
                            LogWarn($"健康检查失败（连续 {failStreak} 次）");

                        int threshold = Math.Max(3, Configuration?.RestartAfterFailures ?? 6);
                        if (failStreak >= threshold && (Configuration?.AutoStart ?? true) && _host != null)
                        {
                            LogWarn("连续失败，正在重启服务…");
                            await _startLock.WaitAsync(ct).ConfigureAwait(false);
                            try
                            {
                                _host.ForceCleanup();
                                await Task.Delay(1500, ct).ConfigureAwait(false);
                                _host.Start();
                            }
                            finally
                            {
                                _startLock.Release();
                            }
                            failStreak = 0;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        failStreak++;
                        LogWarn($"健康检查异常：{ex.Message}");
                    }
                }
            }, ct);
        }

        private async Task<bool> HealthCheckAsync(CancellationToken ct = default)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                string url = Configuration!.ApiUrl.TrimEnd('/') + "/get_spk";
                using var resp = await _http.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return false;

                await RefreshSpeakersFromResponseAsync(resp, ct).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> ProbeReadyAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return await HealthCheckAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> WaitUntilReadyAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow + timeout;
            int attempt = 0;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await HealthCheckAsync(cancellationToken).ConfigureAwait(false))
                    return true;

                attempt++;
                if (attempt == 1 || attempt % 5 == 0)
                    Log($"等待模型加载…（{attempt}）");

                int delay = attempt < 5 ? 2000 : 4000;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        private void EnsureValidSpeaker()
        {
            if (_speakers.Length == 0 || Configuration == null)
                return;

            string current = Configuration.ReferenceAudio?.Trim() ?? "";
            if (string.IsNullOrEmpty(current) ||
                !_speakers.Any(s => string.Equals(s, current, StringComparison.OrdinalIgnoreCase)))
            {
                string fallback = _speakers.FirstOrDefault(s => s.Contains("女主持"))
                    ?? _speakers[0];
                LogWarn($"音色「{current}」无效，改用「{fallback}」");
                Configuration.ReferenceAudio = fallback;
            }
        }

        private async Task WarmupAsync()
        {
            try
            {
                string? path = await GenerateSpeechFileAsync("你好", CancellationToken.None)
                    .ConfigureAwait(false);
                if (path != null)
                    Log("预热完成");
                else
                    LogWarn("预热未返回音频");
            }
            catch (Exception ex)
            {
                LogWarn($"预热失败（可忽略）：{ex.Message}");
            }
        }

        private async Task RefreshSpeakersFromResponseAsync(
            HttpResponseMessage resp,
            CancellationToken ct)
        {
            try
            {
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var list = JsonSerializer.Deserialize<string[]>(json);
                if (list is { Length: > 0 })
                    _speakers = list;
            }
            catch
            {
                // ignore parse errors
            }
        }

        #endregion

        #region 工具

        private void NormalizeApiUrl()
        {
            if (Configuration == null) return;
            string expected = $"http://127.0.0.1:{Configuration.Port}";
            if (string.IsNullOrWhiteSpace(Configuration.ApiUrl) ||
                Configuration.ApiUrl.Contains("127.0.0.1") ||
                Configuration.ApiUrl.Contains("localhost"))
            {
                Configuration.ApiUrl = expected;
            }
        }

        private bool ValidatePaths(out string error)
        {
            error = "";
            var cfg = Configuration!;
            if (!File.Exists(cfg.PythonPath))
            {
                error = $"Python 不存在：{cfg.PythonPath}";
                return false;
            }
            if (!Directory.Exists(cfg.ProjectDir))
            {
                error = $"项目目录不存在：{cfg.ProjectDir}";
                return false;
            }
            string startPy = Path.Combine(cfg.ProjectDir, "start.py");
            if (!File.Exists(startPy))
            {
                error = $"找不到 start.py：{startPy}";
                return false;
            }
            if (!Directory.Exists(cfg.ModelDir))
            {
                error = $"模型目录不存在：{cfg.ModelDir}";
                return false;
            }
            return true;
        }

        private static string ComputeHash(
            string text, string spkid, float speed, string instruct, string api)
        {
            using var md5 = MD5.Create();
            var raw = $"{text}|{spkid}|{speed:F2}|{instruct}|{api}";
            return Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(raw)));
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

        #endregion
    }

    #region Service Host

    /// <summary>
    /// 管理本地 CosyVoice Python 进程。
    /// - 正常退出：Stop 杀进程树
    /// - 异常退出：Windows Job（KILL_ON_JOB_CLOSE）随主进程一起带走子进程
    /// - 历史残留：ForceCleanup 按 PID 文件 + 端口双通道清理
    /// </summary>
    public class CosyVoiceServiceHost
    {
        private Process? _process;
        private IntPtr _jobHandle = IntPtr.Zero;
        private readonly object _lock = new();
        private readonly CosyVoiceSpeechModelConfig _config;
        private readonly StringBuilder _stderr = new();

        private string PidFilePath => Path.Combine(_config.ProjectDir, ".cosyvoice_alife.pid");

        private static void Log(string msg) => Console.WriteLine($"[Cosy语音] {msg}");
        private static void LogWarn(string msg) => Console.WriteLine($"[Cosy语音][警告] {msg}");
        private static void LogError(string msg) => Console.WriteLine($"[Cosy语音][错误] {msg}");

        public CosyVoiceServiceHost(CosyVoiceSpeechModelConfig config)
        {
            _config = config;
        }

        public void Start()
        {
            if (_process != null && !_process.HasExited)
                return;

            lock (_lock)
            {
                if (_process != null && !_process.HasExited)
                    return;

                // 启动前再清一次，避免「端口占用却不可用」导致空转
                if (IsPortListening(_config.Port))
                {
                    LogWarn("端口仍被占用，强制清理后重试");
                    ForceCleanupUnlocked();
                    Thread.Sleep(800);
                }

                if (IsPortListening(_config.Port))
                {
                    LogError($"端口 {_config.Port} 清理后仍被占用，无法启动");
                    return;
                }

                int mode = _config.Mode is >= 1 and <= 3 ? _config.Mode : 1;

                var args =
                    $"start.py " +
                    $"--model_dir \"{_config.ModelDir}\" " +
                    $"--port {_config.Port} " +
                    $"--webport {_config.WebPort} " +
                    $"--limit_count {_config.LimitCount} " +
                    $"--mode {mode}";

                _stderr.Clear();
                EnsureJobObject();

                _process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _config.PythonPath,
                        Arguments = args,
                        WorkingDirectory = _config.ProjectDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    },
                    EnableRaisingEvents = true
                };

                _process.StartInfo.Environment["PYTORCH_CUDA_ALLOC_CONF"] = "expandable_segments:True";
                _process.StartInfo.Environment["PYTHONUNBUFFERED"] = "1";
                _process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";

                // 不刷 stdout/stderr 到日志；仅缓存 stderr 供异常退出时摘要
                _process.OutputDataReceived += (_, _) => { };
                _process.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    lock (_stderr)
                    {
                        if (_stderr.Length < 4000)
                            _stderr.AppendLine(e.Data);
                    }
                };
                _process.Exited += (_, _) =>
                {
                    string err;
                    lock (_stderr) err = _stderr.ToString();
                    if (!string.IsNullOrWhiteSpace(err))
                        LogWarn($"进程异常退出：{Truncate(err, 300)}");
                    else
                        Log("进程已退出");
                    try
                    {
                        if (File.Exists(PidFilePath))
                            File.Delete(PidFilePath);
                    }
                    catch { /* ignore */ }
                };

                try
                {
                    if (!_process.Start())
                        throw new InvalidOperationException("进程启动失败");

                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();

                    // 纳入 Job：Alife 异常退出时一并杀掉 Python，释放显存/内存
                    if (_jobHandle != IntPtr.Zero)
                    {
                        if (!WindowsJob.Assign(_jobHandle, _process))
                            LogWarn("无法绑定进程组，异常退出时可能残留服务");
                    }

                    File.WriteAllText(PidFilePath, _process.Id.ToString());
                    Log($"已启动（模式 {mode}）");
                }
                catch (Exception ex)
                {
                    LogError($"启动 Python 失败：{ex.Message}");
                    try { if (File.Exists(PidFilePath)) File.Delete(PidFilePath); } catch { }
                    _process?.Dispose();
                    _process = null;
                    throw;
                }
            }
        }

        /// <summary>
        /// 强制清理残留：PID 文件 + 监听端口的进程（含进程树）。
        /// 用于异常退出后的僵尸服务、半死不活占端口的情况。
        /// </summary>
        public void ForceCleanup()
        {
            lock (_lock)
            {
                ForceCleanupUnlocked();
            }
        }

        private void ForceCleanupUnlocked()
        {
            var killed = new HashSet<int>();
            bool any = false;

            // 1) 本实例持有的进程
            try
            {
                if (_process != null)
                {
                    try
                    {
                        if (!_process.HasExited)
                        {
                            int pid = _process.Id;
                            KillProcessTree(pid);
                            killed.Add(pid);
                            any = true;
                        }
                    }
                    catch { /* ignore */ }

                    try { _process.Dispose(); } catch { }
                    _process = null;
                }
            }
            catch { /* ignore */ }

            // 2) PID 文件
            try
            {
                if (File.Exists(PidFilePath))
                {
                    string pidStr = File.ReadAllText(PidFilePath).Trim();
                    if (int.TryParse(pidStr, out int oldPid) && oldPid > 0 && killed.Add(oldPid))
                    {
                        if (KillProcessTree(oldPid))
                            any = true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarn($"按 PID 文件清理失败：{ex.Message}");
            }
            finally
            {
                try { if (File.Exists(PidFilePath)) File.Delete(PidFilePath); } catch { }
            }

            // 3) 按端口查杀（解决 PID 文件丢失 / 进程树残留）
            foreach (int port in new[] { _config.Port, _config.WebPort }.Distinct())
            {
                if (port <= 0) continue;
                foreach (int pid in GetPidsListeningOnPort(port))
                {
                    if (!killed.Add(pid)) continue;
                    if (KillProcessTree(pid))
                        any = true;
                }
            }

            // 4) 兜底：taskkill 再扫一遍端口占用
            foreach (int port in new[] { _config.Port, _config.WebPort }.Distinct())
            {
                if (port <= 0) continue;
                foreach (int pid in GetPidsListeningOnPort(port))
                {
                    if (killed.Contains(pid)) continue;
                    if (TaskKill(pid))
                    {
                        killed.Add(pid);
                        any = true;
                    }
                }
            }

            if (any)
                Log("已清理残留服务");

            // 等端口释放
            for (int i = 0; i < 10 && IsPortListening(_config.Port); i++)
                Thread.Sleep(200);
        }

        public void Stop()
        {
            lock (_lock)
            {
                try
                {
                    ForceCleanupUnlocked();
                    CloseJobObject();
                    Log("服务已停止");
                }
                catch (Exception ex)
                {
                    LogWarn($"停止服务时异常：{ex.Message}");
                }
            }
        }

        private void EnsureJobObject()
        {
            if (_jobHandle != IntPtr.Zero) return;
            if (!OperatingSystem.IsWindows()) return;

            _jobHandle = WindowsJob.CreateKillOnCloseJob();
            if (_jobHandle == IntPtr.Zero)
                LogWarn("创建进程组失败，异常退出时可能残留服务");
        }

        private void CloseJobObject()
        {
            if (_jobHandle == IntPtr.Zero) return;
            try { WindowsJob.CloseHandle(_jobHandle); } catch { }
            _jobHandle = IntPtr.Zero;
        }

        private bool KillProcessTree(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                var proc = Process.GetProcessById(pid);
                if (proc.HasExited) return false;

                // 优先整棵进程树（python 常再拉子进程）
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    try { proc.Kill(); } catch { return TaskKill(pid); }
                }

                proc.WaitForExit(8000);
                return true;
            }
            catch (ArgumentException)
            {
                return false; // 已不存在
            }
            catch
            {
                return TaskKill(pid);
            }
        }

        private static bool TaskKill(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /T /PID {pid}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (p == null) return false;
                p.WaitForExit(8000);
                return p.ExitCode == 0 || p.ExitCode == 128; // 128: 进程不存在
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<int> GetPidsListeningOnPort(int port)
        {
            var pids = new HashSet<int>();
            if (port <= 0) return pids;

            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano -p tcp",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (p == null) return pids;

                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);

                foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    // TCP  0.0.0.0:9981  0.0.0.0:0  LISTENING  12345
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5) continue;

                    string local = parts[1];
                    int colon = local.LastIndexOf(':');
                    if (colon < 0) continue;
                    if (!int.TryParse(local.AsSpan(colon + 1), out int localPort) || localPort != port)
                        continue;
                    if (!int.TryParse(parts[^1], out int pid) || pid <= 0)
                        continue;

                    pids.Add(pid);
                }
            }
            catch
            {
                // ignore
            }

            return pids;
        }

        private static bool IsPortListening(int port)
        {
            if (port <= 0) return false;
            try
            {
                // 用监听表判断，避免 TcpClient.ConnectAsync 超时后未观察异常
                var listeners = System.Net.NetworkInformation.IPGlobalProperties
                    .GetIPGlobalProperties()
                    .GetActiveTcpListeners();
                foreach (var ep in listeners)
                {
                    if (ep.Port == port)
                        return true;
                }
                return false;
            }
            catch
            {
                // 回退：同步连接并完整等待，确保异常被观察
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                    client.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                    return client.Connected;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>
    /// Windows Job Object：主进程异常退出时自动结束组内子进程。
    /// </summary>
    internal static class WindowsJob
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        public static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)length))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            return job;
        }

        public static bool Assign(IntPtr job, Process process)
        {
            if (job == IntPtr.Zero || process == null)
                return false;
            try
            {
                return AssignProcessToJobObject(job, process.Handle);
            }
            catch
            {
                return false;
            }
        }
    }

    #endregion
}
