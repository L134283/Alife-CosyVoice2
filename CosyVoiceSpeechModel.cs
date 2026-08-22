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
using Alife.Foundation;
using Alife.Function.AIModelUtility;

namespace Alife.Function.Speech.CosyVoiceTTS
{
    [Module(
        "Cosy语音合成",
        "基于本地 CosyVoice2 (yinmei) 的语音合成引擎",
        defaultCategory: "Doro的妙妙工具",
        EditorUI = typeof(CosyVoiceSpeechModelUI)
    )]
    public class CosyVoiceSpeechModel :
        ChatBehaviour,
        ISpeechModel,
        IConfigurable<CosyVoiceSpeechModelConfig>
    {
        public CosyVoiceSpeechModelConfig? Configuration { get; set; }

        private static readonly ConcurrentDictionary<string, string> FileCache = new();
        private static readonly ConcurrentDictionary<string, InFlightSynth> InFlight = new();

        /// <summary>用户手动停止服务后，看门狗 / 自动恢复暂停（直到再次启动或重开桌宠）。</summary>
        internal static volatile bool ManualStopActive;

        /// <summary>同句飞行合成：带等待方计数，无人等待时停止重试。</summary>
        private sealed class InFlightSynth
        {
            public Task<string?> Task = null!;
            public int Waiters;
        }

        // 超时由每次请求的 CancelAfter 控制
        private readonly HttpClient _http = new()
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private CosyVoiceServiceHost? _host;
        private CosyVoiceSharedGate? _gate;
        private CancellationTokenSource? _watchdogCts;
        private Task? _watchdogTask;
        /// <summary>「进程存活但未就绪」提示节流时间戳，避免每轮循环刷屏。</summary>
        private long _lastLoadWarnUtcTicks;
        /// <summary>上次缓存清理时间，看门狗按小时级节流。</summary>
        private long _lastCacheSweepUtcTicks;
        private readonly SemaphoreSlim _startLock = new(1, 1);
        // 进程内串行；跨进程由 Named Mutex 再串一层
        private readonly SemaphoreSlim _synthLock = new(1, 1);
        private int _activeSynth;
        private long _lastSynthUtcTicks = DateTime.UtcNow.Ticks;

        // === 空闲工作集修剪（借鉴 GptSovits：空闲时 EmptyWorkingSet 压回物理内存）===
        private readonly object _idleGate = new();
        private long _lastTtsDemandUtcTicks = DateTime.UtcNow.Ticks;
        private int _trimmedProcessId;          // 已剪枝的进程 PID（同一进程只剪一次）
        private volatile bool _idleTrimmed;
        private CancellationTokenSource? _idleTrimCts;
        private Task? _idleTrimTask;
        private long _synthHoldStartedUtcTicks;
        private volatile bool _ready;
        private volatile bool _ownsProcess;
        private int _ttsFailStreak;
        private string[] _speakers = Array.Empty<string>();

        public IReadOnlyList<string> Speakers => _speakers;
        public bool IsReady => _ready;

        private bool Shared => Configuration?.SharedService ?? false;
        private bool IsBusySynthesizing => Volatile.Read(ref _activeSynth) > 0;
        private bool RecentlySynthesized(TimeSpan window)
            => (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastSynthUtcTicks), DateTimeKind.Utc)) < window;

        /// <summary>合成持锁过久（GPU 挂死常见），看门狗不应再当「正常忙碌」跳过恢复。</summary>
        private bool IsSynthStuck(TimeSpan limit)
        {
            if (!IsBusySynthesizing) return false;
            long started = Interlocked.Read(ref _synthHoldStartedUtcTicks);
            if (started <= 0) return false;
            return (DateTime.UtcNow - new DateTime(started, DateTimeKind.Utc)) > limit;
        }

        // 用 Console 输出，避免 ILogger 默认的 info: Namespace.Class[0] 前缀
        // QuietLog=true（默认）：仅 Important/Warn/Error；false：全部输出
        private bool Quiet => Configuration?.QuietLog ?? true;

        private void Log(string msg)
        {
            if (Quiet) return;
            Console.WriteLine($"[Cosy语音] {msg}");
        }

        /// <summary>重要状态变更，安静模式下仍输出。</summary>
        private void LogImportant(string msg)
            => Console.WriteLine($"[Cosy语音] {msg}");

        private void LogWarn(string msg)
            => Console.WriteLine($"[Cosy语音][警告] {msg}");

        private void LogError(string msg)
            => Console.WriteLine($"[Cosy语音][错误] {msg}");

        #region 生命周期

        protected override async Task OnAwake()
        {
            Configuration ??= new CosyVoiceSpeechModelConfig();
            NormalizeApiUrl();

            // 重新开桌宠即代表用户想要语音：取消此前「手动停止」的抑制状态
            if (Configuration.AutoStart)
                ManualStopActive = false;

            _host = new CosyVoiceServiceHost(Configuration);
            _gate = new CosyVoiceSharedGate(Configuration.Port, Configuration.ProjectDir);
            _gate.RegisterClient();
            if (Shared)
                Log($"共享模式：客户端 {_gate.CountLiveClients()} 个，端口 {Configuration.Port}");

            // 仅当健康检查通过才复用；端口占用但不健康会在 Start 里强制清理
            if (await ProbeReadyAsync(TimeSpan.FromSeconds(2)))
            {
                Log("检测到已有服务，复用中");
                _ready = true;
                _ownsProcess = false;
                Interlocked.Exchange(ref _ttsFailStreak, 0);
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
                    // 跨进程启动锁：双桌宠同时 AutoStart 时只起一个 Python
                    using var startHold = _gate.TryAcquireStart(TimeSpan.FromSeconds(90));
                    if (startHold == null)
                    {
                        // 未拿到启动锁：禁止 ForceCleanup，避免杀掉对方正在拉起的进程
                        LogWarn("等待其他客户端启动服务超时，改为探测复用");
                        _ownsProcess = false;
                    }
                    else if (!await ProbeReadyAsync(TimeSpan.FromSeconds(1)))
                    {
                        // 端口已有存活进程但未就绪 → 视为正在加载（手动预加载/热重载），不打断
                        bool portProcAlive = CosyVoiceServiceHost.IsPortInUse(Configuration.Port)
                            && CosyVoiceServiceHost.IsPortProcessAlive(Configuration.Port);
                        if (portProcAlive)
                        {
                            Log("检测到服务正在加载（端口进程存活），等待就绪，不打断预加载");
                            _ownsProcess = false;
                        }
                        else
                        {
                            // 健康失败且无存活进程 → 清理僵尸残留后启动
                            _host.ForceCleanup();
                            _host.Start(bindJob: !Shared);
                            _ownsProcess = true;
                            Log(Shared ? "服务启动中…（共享，不绑定退出杀进程）" : "服务启动中…");
                        }
                    }
                    else
                    {
                        Log("其他客户端已拉起服务，复用中");
                        _ownsProcess = false;
                    }
                }
                finally
                {
                    _startLock.Release();
                }

                // 不再同步等待就绪：先返回让桌宠激活立即完成，由看门狗后台轮询。
                // 机械硬盘可能加载 10-20 分钟，同步等待会卡住激活流程。
                bool ok = await ProbeReadyAsync(TimeSpan.FromSeconds(2));
                if (ok)
                {
                    _ready = true;
                    EnsureValidSpeaker();
                    LogImportant($"服务就绪，音色 {_speakers.Length} 个");
                }
                else if (_host.IsProcessAlive)
                {
                    Log(Shared
                        ? "服务启动中…（共享，后台加载，就绪后自动可用）"
                        : "服务启动中…（后台加载，就绪后自动可用）");
                }
            }
            catch (Exception ex)
            {
                LogError($"启动失败：{ex.Message}");
            }

            StartWatchdog();
            StartIdleTrimMonitor();
        }

        protected override async Task OnDestroy()
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

                StopIdleTrimMonitor();

                // 等飞行合成收尾（最多 5s）后再清理共享门，避免合成中的跨进程锁操作撞上已释放的 Mutex
                for (int i = 0; i < 50 && !InFlight.IsEmpty; i++)
                    await Task.Delay(100).ConfigureAwait(false);

                int remaining = _gate?.UnregisterClient() ?? 0;
                // 手动启动的独立服务（不随桌宠/Alife 退出）→ 桌宠关闭时保留，避免带走用户预加载的服务
                bool externalService = CosyVoiceServiceHost.ExternalMarkerExists(Configuration.ProjectDir);

                if (Shared)
                {
                    // 共享模式：仅最后一位存活客户端停服，避免关掉一个桌宠带走 TTS
                    if (remaining <= 0 && !externalService)
                    {
                        LogImportant("共享模式：已无其他客户端，停止服务");
                        _host?.Stop();
                    }
                    else
                    {
                        Log($"共享模式：仍有 {remaining} 个客户端{(externalService ? "，存在手动启动的独立服务" : "")}，保留 TTS 进程");
                        _host?.Detach();
                    }
                }
                else
                {
                    // 独占模式：停掉本插件拉起的服务；手动启动/复用的独立服务不带走
                    if (_ownsProcess && !externalService)
                    {
                        LogImportant("独占模式：停止本端拉起的服务");
                        _host?.Stop();
                    }
                    else
                    {
                        Log(externalService
                            ? "检测到手动启动的独立服务，桌宠退出不带走 TTS"
                            : "服务非本端拉起，保留 TTS 进程");
                        _host?.Detach();
                    }
                }

                _gate?.Dispose();
                _gate = null;

                // 故意不 Dispose _http/_startLock/_synthLock：插件热重载后，框架构造注入的消费者
                // （如 SpeechService）可能仍持有本实例引用，迟到的合成调用撞上已释放对象会直接抛
                // ObjectDisposedException（实测 2026-08-22 日志可复现）。保留可用状态让迟到调用
                // 照常完成合成；未释放的句柄量级极小（仅开发期热重载产生），交给 GC / 进程退出回收。
                _watchdogCts?.Dispose();
            }
            catch (Exception ex)
            {
                LogWarn($"释放资源时异常：{ex.Message}");
            }
        }

        #endregion

        #region 手动服务控制（UI 开关；桌宠未启动时走静态路径）

        /// <summary>
        /// 手动启动服务（UI「启动服务」按钮，桌宠运行时调用）。
        /// 按共享/独占语义绑定进程，与自动启动一致。
        /// </summary>
        public string ManualStart()
        {
            Configuration ??= new CosyVoiceSpeechModelConfig();
            NormalizeApiUrl();
            ManualStopActive = false;

            if (_ready)
                return "服务已在运行且就绪，无需重复启动。";

            if (CosyVoiceServiceHost.IsPortInUse(Configuration.Port))
                return $"端口 {Configuration.Port} 已被占用（可能正在加载中），请稍候或先用「停止服务」清理后重试。";

            if (!ValidatePaths(out var pathError))
                return "路径无效：" + pathError;

            try
            {
                // 有界等待：看门狗重启服务期间持锁可达数十秒，不设上限会长期占住线程池线程
                if (!_startLock.Wait(TimeSpan.FromSeconds(30)))
                    return "服务正在启动/重启中，请稍后再试。";
                try
                {
                    _host ??= new CosyVoiceServiceHost(Configuration);
                    _host.ForceCleanup();
                    _host.Start(bindJob: !Shared);
                    _ownsProcess = true;
                    _ready = false;
                    // 共享模式进程不绑 Job：标记为独立服务，桌宠关闭时不带走
                    if (Shared)
                        CosyVoiceServiceHost.MarkExternalService(Configuration.ProjectDir, _host.ProcessId);
                }
                finally
                {
                    _startLock.Release();
                }
                LogImportant($"手动启动（共享={Shared}），模型后台加载中…");
                return "已启动 Cosy 服务，模型后台加载中（机械硬盘可能需较长时间，稍后可刷新状态查看）。";
            }
            catch (Exception ex)
            {
                LogError($"手动启动失败：{ex.Message}");
                return "启动失败：" + ex.Message;
            }
        }

        /// <summary>手动停止服务（UI「停止服务」按钮；会结束端口上的 TTS 进程）。</summary>
        public string ManualStop()
        {
            ManualStopActive = true;
            _ready = false;
            try
            {
                CosyVoiceServiceHost.ClearExternalMarker(Configuration.ProjectDir);
                _host?.Stop();
                _ownsProcess = false;
                LogImportant("已手动停止服务");
                return "已停止 Cosy 服务。";
            }
            catch (Exception ex)
            {
                LogWarn($"手动停止失败：{ex.Message}");
                return "停止失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 静态启动：桌宠未开启时也可用（配置页按钮）。
        /// 不绑定 Alife 退出，适合机械硬盘用户先后台预加载模型再开桌宠。
        /// </summary>
        public static string StartService(CosyVoiceSpeechModelConfig config)
        {
            config ??= new CosyVoiceSpeechModelConfig();
            NormalizeApiUrl(config);
            ManualStopActive = false;

            if (IsServiceHealthy(config, TimeSpan.FromSeconds(2)))
                return $"服务已在运行且就绪（端口 {config.Port}）。";

            if (CosyVoiceServiceHost.IsPortInUse(config.Port))
                return $"端口 {config.Port} 已被占用（可能正在加载中），请稍候或先点「停止服务」清理后重试。";

            if (!ValidatePaths(config, out var pathError))
                return "路径无效：" + pathError;

            try
            {
                var host = new CosyVoiceServiceHost(config);
                host.ForceCleanup();
                // 手动启动不绑 Job：关掉 Alife 也继续后台加载，适合机械硬盘预加载
                host.Start(bindJob: false);
                // 标记为独立服务：之后打开桌宠/关桌宠都不会带走它
                CosyVoiceServiceHost.MarkExternalService(config.ProjectDir, host.ProcessId);
                return "已启动 Cosy 服务，模型后台加载中（机械硬盘可能需较长时间，稍后可点「刷新状态」查看进度）。";
            }
            catch (Exception ex)
            {
                return "启动失败：" + ex.Message;
            }
        }

        /// <summary>静态停止：桌宠未开启时也可用（配置页按钮）。</summary>
        public static string StopService(CosyVoiceSpeechModelConfig config)
        {
            config ??= new CosyVoiceSpeechModelConfig();
            ManualStopActive = true;
            CosyVoiceServiceHost.ClearExternalMarker(config.ProjectDir);
            return CosyVoiceServiceHost.CleanupOrphans(config);
        }

        /// <summary>服务是否就绪（/get_spk 可达）。静态探测，不依赖模块实例。</summary>
        public static bool IsServiceHealthy(CosyVoiceSpeechModelConfig config, TimeSpan timeout)
        {
            config ??= new CosyVoiceSpeechModelConfig();
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                using var http = new HttpClient { Timeout = timeout };
                string baseUrl = string.IsNullOrWhiteSpace(config.ApiUrl)
                    ? $"http://127.0.0.1:{config.Port}"
                    : config.ApiUrl.TrimEnd('/');
                using var resp = http.GetAsync(baseUrl + "/get_spk", cts.Token).GetAwaiter().GetResult();
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
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

            // 空闲剪枝：有合成需求即视为「非空闲」，记录需求时间（剪枝标志不复位：
            // 同一进程只剪一次，避免反复换页导致 pagefile 膨胀）
            lock (_idleGate)
            {
                _lastTtsDemandUtcTicks = DateTime.UtcNow.Ticks;
            }

            text = text.Trim();
            // Cosy 对纯标点/空白会稳定 500，合成前直接跳过
            if (!HasSpeakableContent(text))
            {
                LogWarn($"跳过不可朗读文本：「{Truncate(text, 40)}」");
                return null;
            }

            string spkid = string.IsNullOrWhiteSpace(Configuration.ReferenceAudio)
                ? "女主持"
                : Configuration.ReferenceAudio.Trim();
            string instruct = Configuration.Instruct?.Trim() ?? "";
            float speed = Configuration.Speed <= 0 ? 1.0f : Configuration.Speed;

            string hash = ComputeHash(text, spkid, speed, instruct);
            string outputPath = Path.Combine(AlifePath.TempFolderPath, $"cosy_{hash}.wav");

            if (File.Exists(outputPath))
                return outputPath;

            if (FileCache.TryGetValue(hash, out var cached) && File.Exists(cached))
                return cached;

            // 同句飞行去重：并发相同文本只打一次 API
            var entry = InFlight.GetOrAdd(hash, _ =>
            {
                var e = new InFlightSynth();
                e.Task = SynthesizeAndUntrackAsync(hash, text, spkid, speed, instruct, outputPath, e);
                return e;
            });

            Interlocked.Increment(ref entry.Waiters);
            try
            {
                if (!cancellationToken.CanBeCanceled)
                    return await entry.Task.ConfigureAwait(false);

                // 调用方可取消等待；无人等待时后台停止重试
                var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var reg = cancellationToken.Register(() => cancelTcs.TrySetResult());
                var finished = await Task.WhenAny(entry.Task, cancelTcs.Task).ConfigureAwait(false);
                if (finished != entry.Task)
                    throw new OperationCanceledException(cancellationToken);
                return await entry.Task.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref entry.Waiters);
            }
        }

        private async Task<string?> SynthesizeAndUntrackAsync(
            string hash,
            string text,
            string spkid,
            float speed,
            string instruct,
            string outputPath,
            InFlightSynth entry)
        {
            try
            {
                return await SynthesizeCoreAsync(
                    text, spkid, speed, instruct, outputPath, hash, entry, CancellationToken.None)
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
            InFlightSynth entry,
            CancellationToken cancellationToken)
        {
            var totalSw = Stopwatch.StartNew();
            Log($"开始合成：「{Truncate(text, 40)}」");

            if (!_ready)
            {
                Log("服务未就绪，等待健康检查…");
                bool ok = await WaitUntilReadyAsync(
                    TimeSpan.FromSeconds(Math.Min(60, Configuration!.ReadyTimeoutSeconds)),
                    cancellationToken);
                if (ok) _ready = true;
                else
                {
                    // 健康检查不过时不要再占满合成锁长时间空转
                    LogWarn($"服务未就绪，放弃合成：「{Truncate(text, 40)}」");
                    NoteTtsFailure("服务未就绪");
                    return null;
                }
            }

            object payload = string.IsNullOrEmpty(instruct)
                ? new { text, spkid, speed, stream = false }
                : new { text, spkid, speed, instruct, stream = false };

            string url = Configuration!.ApiUrl.TrimEnd('/') + "/tts";
            // 真忙重试次数收紧；不可朗读文本不会进到这里
            int maxRetry = 3;
            int timeoutSec = Math.Clamp(Configuration.RequestTimeoutSeconds, 10, 90);
            int lockTimeoutSec = Math.Clamp(Configuration.SynthLockTimeoutSeconds, 10, 180);
            HttpResponseMessage? resp = null;
            bool lockHeld = false;
            int timeoutHits = 0;

            Interlocked.Increment(ref _activeSynth);
            Interlocked.Exchange(ref _lastSynthUtcTicks, DateTime.UtcNow.Ticks);
            // 注意：持锁计时只在真正拿到锁后写入，避免排队等待被误判为 GPU 挂死

            // 进程内串行：带超时，避免前一请求 GPU 挂死后后面无限转圈
            Log($"等待进程内合成锁（最多 {lockTimeoutSec}s）…");
            try
            {
                if (!await _synthLock.WaitAsync(TimeSpan.FromSeconds(lockTimeoutSec), cancellationToken)
                        .ConfigureAwait(false))
                {
                    LogWarn($"等待进程内合成锁超时（{lockTimeoutSec}s），放弃：「{Truncate(text, 40)}」");
                    Interlocked.Decrement(ref _activeSynth);
                    NoteTtsFailure("进程内合成锁超时");
                    await TryRecoverAsync("进程内合成锁超时", forceRestart: true)
                        .ConfigureAwait(false);
                    return null;
                }
            }
            catch (OperationCanceledException)
            {
                // 排队等锁期间被取消：必须回滚计数，否则 IsBusySynthesizing 永久为真，看门狗失明
                Interlocked.Decrement(ref _activeSynth);
                throw;
            }
            lockHeld = true;
            Interlocked.Exchange(ref _synthHoldStartedUtcTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _lastSynthUtcTicks, DateTime.UtcNow.Ticks);

            IDisposable? crossProcHold = null;
            try
            {
                if (Shared && _gate != null)
                {
                    // 跨进程锁超时不宜太长：刚启动或上次崩溃后可能残留死锁，10s 足够判断
                    int crossLockSec = 10;
                    Log($"等待跨进程合成锁（最多 {crossLockSec}s）…");
                    crossProcHold = await _gate
                        .AcquireSynthAsync(TimeSpan.FromSeconds(crossLockSec), cancellationToken)
                        .ConfigureAwait(false);
                    if (crossProcHold == null)
                    {
                        LogWarn($"等待跨进程合成锁超时（{crossLockSec}s），放弃：「{Truncate(text, 40)}」");
                        NoteTtsFailure("跨进程合成锁超时");
                        // 共享模式锁超时通常是对端桌宠正常占用 GPU，不强制重启以免打断对方；
                        // 仅当本端是最后存活客户端时由 TryRecoverAsync 决定是否恢复
                        await TryRecoverAsync("跨进程合成锁超时", forceRestart: false)
                            .ConfigureAwait(false);
                        return null;
                    }
                    // 跨进程锁到手后刷新持锁起点（真正开始占用全局合成权）
                    Interlocked.Exchange(ref _synthHoldStartedUtcTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Exchange(ref _lastSynthUtcTicks, DateTime.UtcNow.Ticks);
                }

                for (int retry = 0; retry < maxRetry; retry++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 首次请求照常；之后若已无人等待则停止重试
                    if (retry > 0 && Volatile.Read(ref entry.Waiters) <= 0)
                    {
                        Log("合成等待已取消，停止重试");
                        resp?.Dispose();
                        return null;
                    }

                    Interlocked.Exchange(ref _lastSynthUtcTicks, DateTime.UtcNow.Ticks);

                    using var content = new StringContent(
                        JsonSerializer.Serialize(payload),
                        Encoding.UTF8,
                        "application/json");

                    try
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

                        var postSw = Stopwatch.StartNew();
                        Log(retry == 0
                            ? $"POST /tts（超时 {timeoutSec}s）「{Truncate(text, 40)}」"
                            : $"POST /tts 重试 {retry + 1}/{maxRetry}（超时 {timeoutSec}s）");

                        resp = await _http.PostAsync(url, content, timeoutCts.Token)
                            .ConfigureAwait(false);

                        postSw.Stop();
                        if (resp.IsSuccessStatusCode)
                        {
                            Log($"POST /tts 成功 {postSw.ElapsedMilliseconds}ms，状态 {(int)resp.StatusCode}");
                            break;
                        }

                        int code = (int)resp.StatusCode;
                        string body = await resp.Content.ReadAsStringAsync(cancellationToken)
                            .ConfigureAwait(false);
                        LogWarn($"POST /tts 失败 {postSw.ElapsedMilliseconds}ms，状态 {code}");

                        if (code >= 500)
                        {
                            // 空 body + 几乎无有效字符：服务端拒读，重试无意义
                            if (IsNonRetryableServerError(code, body, text))
                            {
                                LogWarn(
                                    $"合成拒绝（{code}），文本不可朗读：「{Truncate(text, 40)}」" +
                                    (string.IsNullOrWhiteSpace(body) ? "" : $" {Truncate(body, 80)}"));
                                resp.Dispose();
                                return null;
                            }

                            if (retry == 0 || retry % 2 == 0)
                                LogWarn($"合成服务忙（{code}），文本「{Truncate(text, 40)}」，稍后重试…");
                        }
                        else
                        {
                            LogWarn($"合成请求失败（{code}），文本「{Truncate(text, 40)}」，{Truncate(body, 120)}");
                        }

                        // 4xx 参数问题不重试（408/429 除外）
                        if (code is >= 400 and < 500 and not 408 and not 429)
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
                        // TaskCanceledException 多数是单次 HTTP 超时
                        bool isTimeout = ex is TaskCanceledException && !cancellationToken.IsCancellationRequested;
                        if (isTimeout)
                        {
                            timeoutHits++;
                            NoteTtsFailure($"HTTP 超时 {timeoutSec}s");
                        }

                        if (retry == 0)
                            Log(isTimeout
                                ? $"合成超时（{timeoutSec}s），重试中…"
                                : "服务未就绪，等待中…");
                        else
                            Log(isTimeout
                                ? $"合成仍超时（{retry + 1}/{maxRetry}）"
                                : $"仍在等待服务（{retry + 1}/{maxRetry}）");
                    }

                    resp?.Dispose();
                    resp = null;

                    // 5xx 短退避；无人等待则不再等
                    if (Volatile.Read(ref entry.Waiters) <= 0)
                    {
                        Log("合成等待已取消，停止重试");
                        return null;
                    }

                    int waitMs = Math.Min(8_000, 800 * (1 << Math.Min(retry, 3)));
                    await Task.Delay(waitMs, cancellationToken).ConfigureAwait(false);
                }

                if (resp == null || !resp.IsSuccessStatusCode)
                {
                    NoteTtsFailure($"重试 {maxRetry} 次仍失败");
                    LogError($"合成失败，已重试 {maxRetry} 次，文本「{Truncate(text, 40)}」");
                    resp?.Dispose();

                    // 多次超时：主动重启 Cosy
                    if (timeoutHits > 0)
                    {
                        await TryRecoverAsync(
                                $"合成超时 {timeoutHits} 次",
                                forceRestart: timeoutHits >= 2)
                            .ConfigureAwait(false);
                    }
                    return null;
                }

                try
                {
                    byte[] audio = await resp.Content.ReadAsByteArrayAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (audio.Length < 44)
                    {
                        LogError($"返回音频过短（{audio.Length} 字节），文本「{Truncate(text, 40)}」");
                        NoteTtsFailure("音频过短");
                        return null;
                    }

                    string tmp = outputPath + ".tmp";
                    await File.WriteAllBytesAsync(tmp, audio, cancellationToken).ConfigureAwait(false);
                    File.Move(tmp, outputPath, overwrite: true);

                    FileCache[hash] = outputPath;
                    Interlocked.Exchange(ref _lastSynthUtcTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Exchange(ref _ttsFailStreak, 0);
                    totalSw.Stop();
                    Log($"合成完成 {totalSw.ElapsedMilliseconds}ms，{audio.Length} 字节：「{Truncate(text, 40)}」");
                    return outputPath;
                }
                finally
                {
                    resp.Dispose();
                }
            }
            finally
            {
                try { crossProcHold?.Dispose(); } catch { /* ignore */ }
                if (lockHeld)
                {
                    try { _synthLock.Release(); } catch { /* ignore */ }
                }
                Interlocked.Decrement(ref _activeSynth);
                Interlocked.Exchange(ref _synthHoldStartedUtcTicks, 0);
                Interlocked.Exchange(ref _lastSynthUtcTicks, DateTime.UtcNow.Ticks);
            }
        }

        /// <summary>累计 TTS 失败；超时/挂死也计入，供看门狗触发重启。</summary>
        private void NoteTtsFailure(string reason)
        {
            int n = Interlocked.Increment(ref _ttsFailStreak);
            LogWarn($"TTS 连续失败 {n} 次（{reason}），可能 CUDA/GPU 被占用");
        }

        /// <summary>
        /// TTS 异常恢复：必要时重启 Cosy 服务。
        /// </summary>
        private async Task TryRecoverAsync(string reason, bool forceRestart)
        {
            int fails = Volatile.Read(ref _ttsFailStreak);
            if (!forceRestart && fails < 3)
                return;

            if (ManualStopActive)
            {
                LogWarn("已手动停止服务，跳过自动恢复");
                return;
            }

            if (!(Configuration?.AutoStart ?? true) || _host == null)
                return;

            // 共享模式：其它桌宠还在时不重启 TTS，避免打断对方
            if (Shared && (_gate?.CountLiveClients() ?? 1) > 1 && !forceRestart)
            {
                LogWarn("共享模式：其它桌宠仍在，暂不重启 TTS");
                return;
            }

            LogWarn($"准备重启 Cosy 服务（{reason}，连败 {fails}）…");
            _ready = false;

            try
            {
                await _startLock.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            try
            {
                using var startHold = _gate?.TryAcquireStart(TimeSpan.FromSeconds(15));
                if (Shared && startHold == null)
                {
                    LogWarn("共享模式：其它客户端正在启动/重启，本端跳过");
                    return;
                }

                _host.ForceCleanup();
                await Task.Delay(1500).ConfigureAwait(false);
                _host.Start(bindJob: !Shared);
                _ownsProcess = true;

                bool ok = await WaitUntilReadyAsync(
                    TimeSpan.FromSeconds(Math.Max(30, Configuration?.ReadyTimeoutSeconds ?? 180)))
                    .ConfigureAwait(false);
                if (ok)
                {
                    _ready = true;
                    Interlocked.Exchange(ref _ttsFailStreak, 0);
                    LogImportant("Cosy 服务已重启并就绪");
                }
                else
                {
                    LogWarn("重启后仍未就绪，看门狗将继续尝试");
                }
            }
            finally
            {
                try { _startLock.Release(); } catch { /* ignore */ }
            }
        }

        #endregion

        #region 就绪 / 健康 / 预热

        private void StartWatchdog()
        {
            var oldCts = _watchdogCts;
            var oldTask = _watchdogTask;
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch { /* ignore */ }
                // 后台等旧循环退出后再释放其 CTS：避免重入时的双看门狗窗口与 CTS 泄漏
                _ = Task.Run(() =>
                {
                    try { oldTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
                    try { oldCts.Dispose(); } catch { /* ignore */ }
                });
            }

            _watchdogCts = new CancellationTokenSource();
            var ct = _watchdogCts.Token;
            Interlocked.Exchange(ref _lastLoadWarnUtcTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _lastCacheSweepUtcTicks, DateTime.UtcNow.Ticks);

            _watchdogTask = Task.Run(async () =>
            {
                int failStreak = 0;
                var started = DateTime.UtcNow;

                while (!ct.IsCancellationRequested)
                {
                    int delaySec = (DateTime.UtcNow - started).TotalMinutes < 2 ? 10 : 25;
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
                        // 共享模式心跳：避免其它桌宠误判本端已退出
                        if (Shared)
                            _gate?.RegisterClient();

                        // 周期清理过期同句缓存 wav（节流 1 小时）
                        if ((DateTime.UtcNow - new DateTime(
                                Interlocked.Read(ref _lastCacheSweepUtcTicks), DateTimeKind.Utc))
                            > TimeSpan.FromHours(1))
                        {
                            Interlocked.Exchange(ref _lastCacheSweepUtcTicks, DateTime.UtcNow.Ticks);
                            SweepCache();
                        }

                        int lockTimeoutSec = Math.Clamp(Configuration?.SynthLockTimeoutSeconds ?? 60, 10, 180);
                        var stuckLimit = TimeSpan.FromSeconds(lockTimeoutSec + 30);
                        bool synthStuck = IsSynthStuck(stuckLimit);

                        // 合成持锁过久：视为 GPU 挂死，不再当「正常忙碌」跳过
                        if (synthStuck)
                        {
                            LogWarn($"合成持锁超过 {stuckLimit.TotalSeconds:0}s，疑似 GPU 挂死，触发恢复");
                            await TryRecoverAsync("合成持锁过久", forceRestart: true)
                                .ConfigureAwait(false);
                            failStreak = 0;
                            continue;
                        }

                        // 本进程或其它桌宠正在合成 / 刚合成完：禁止误杀
                        if (IsBusySynthesizing ||
                            RecentlySynthesized(TimeSpan.FromSeconds(45)) ||
                            (_gate?.IsSynthBusyElsewhere() ?? false))
                        {
                            failStreak = 0;
                            continue;
                        }

                        bool ok = await HealthCheckAsync(ct).ConfigureAwait(false);
                        if (ok)
                        {
                            if (!_ready)
                            {
                                LogImportant($"服务就绪，音色 {_speakers.Length} 个");
                                try { EnsureValidSpeaker(); } catch { /* ignore */ }
                                await WarmupAsync().ConfigureAwait(false);
                            }
                            _ready = true;
                            failStreak = 0;

                            // 即使 /get_spk 通过，TTS 连续失败也可能标识 CUDA 已损坏
                            int ttsFails = Volatile.Read(ref _ttsFailStreak);
                            if (ttsFails >= 3)
                            {
                                LogWarn($"TTS 连续失败 {ttsFails} 次，可能 CUDA 上下文已损坏，触发恢复");
                                _ready = false;
                                await TryRecoverAsync(
                                        $"TTS 连败 {ttsFails}",
                                        forceRestart: true)
                                    .ConfigureAwait(false);
                                continue;
                            }

                            continue;
                        }

                        // 合成中途失败不累计（双重保险）；持锁过久已在上面处理
                        if (IsBusySynthesizing || (_gate?.IsSynthBusyElsewhere() ?? false))
                        {
                            failStreak = 0;
                            continue;
                        }

                        failStreak++;
                        // 忙时偶发失败不立刻标未就绪，避免上层连环重试
                        if (failStreak >= 2)
                            _ready = false;

                        if (failStreak == 1 || failStreak % 3 == 0)
                            LogWarn($"健康检查失败（连续 {failStreak} 次）");

                        // 连续失败达到阈值才重启，避免 500 抖动时杀进程
                        int threshold = Configuration?.RestartAfterFailures ?? 8;
                        if (failStreak >= threshold && (Configuration?.AutoStart ?? true) && _host != null && !ManualStopActive)
                        {
                            // 进程还活着（本端持有句柄或端口有存活进程，兼容复用/外部手动启动的服务）
                            // → 视为仍在加载/恢复，不杀进程（机械硬盘首次加载可能很久）
                            if (_host.IsProcessAlive || CosyVoiceServiceHost.IsPortProcessAlive(Configuration.Port))
                            {
                                if ((DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastLoadWarnUtcTicks), DateTimeKind.Utc)) > TimeSpan.FromMinutes(10))
                                {
                                    Interlocked.Exchange(ref _lastLoadWarnUtcTicks, DateTime.UtcNow.Ticks);
                                    LogWarn("服务进程存活但未就绪，视为仍在加载（机械硬盘首次加载可能很慢），暂不重启");
                                }
                                failStreak = 0;
                                continue;
                            }

                            // 重启前再确认：若又开始合成 / 其它客户端仍在用则取消重启
                            if (IsBusySynthesizing ||
                                RecentlySynthesized(TimeSpan.FromSeconds(30)) ||
                                (_gate?.IsSynthBusyElsewhere() ?? false))
                            {
                                failStreak = 0;
                                continue;
                            }

                            // 共享模式：仅当本机是最后存活客户端时才允许杀进程重启
                            if (Shared && (_gate?.CountLiveClients() ?? 1) > 1)
                            {
                                if (failStreak == threshold || failStreak % 6 == 0)
                                    LogWarn("共享模式：其它桌宠仍在使用，跳过重启以免打断对方");
                                continue;
                            }

                            LogWarn("连续失败，正在重启服务…");

                            await _startLock.WaitAsync(ct).ConfigureAwait(false);
                            try
                            {
                                if (IsBusySynthesizing || (_gate?.IsSynthBusyElsewhere() ?? false))
                                {
                                    failStreak = 0;
                                    continue;
                                }

                                using var startHold = _gate?.TryAcquireStart(TimeSpan.FromSeconds(15));
                                if (Shared && startHold == null)
                                {
                                    LogWarn("共享模式：其它客户端正在启动/重启，本端跳过");
                                    failStreak = 0;
                                    continue;
                                }

                                if (await HealthCheckAsync(ct).ConfigureAwait(false))
                                {
                                    _ready = true;
                                    failStreak = 0;
                                    continue;
                                }

                                _host.ForceCleanup();
                                await Task.Delay(2000, ct).ConfigureAwait(false);
                                _host.Start(bindJob: !Shared);
                                _ownsProcess = true;
                                Interlocked.Exchange(ref _ttsFailStreak, 0);
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
                        if (!IsBusySynthesizing)
                        {
                            failStreak++;
                            LogWarn($"健康检查异常：{ex.Message}");
                        }
                    }
                }
            }, ct);
        }

        // ===== 空闲工作集修剪（借鉴 GptSovits：EmptyWorkingSet 只压物理 RSS，不碰显存）=====

        /// <summary>启动空闲剪枝监控：周期性检查 TTS 进程，空闲超阈值则把工作集压回分页文件。</summary>
        private void StartIdleTrimMonitor()
        {
            if (_idleTrimTask != null)
                return;
            _idleTrimCts = CancellationTokenSource.CreateLinkedTokenSource(
                _watchdogCts?.Token ?? CancellationToken.None);
            _idleTrimTask = Task.Run(() => IdleTrimLoopAsync(_idleTrimCts.Token), CancellationToken.None);
        }

        /// <summary>停止空闲剪枝监控。</summary>
        private void StopIdleTrimMonitor()
        {
            try { _idleTrimCts?.Cancel(); } catch { }
            if (_idleTrimTask != null)
            {
                try { _idleTrimTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
            }
            try { _idleTrimCts?.Dispose(); } catch { }
            _idleTrimCts = null;
            _idleTrimTask = null;
        }

        private async Task IdleTrimLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                int poll = Math.Clamp(Configuration?.IdleTrimPollSeconds ?? 20, 5, 300);
                try { await Task.Delay(TimeSpan.FromSeconds(poll), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                TryTrimWorkingSet();
            }
        }

        private void TryTrimWorkingSet()
        {
            var cfg = Configuration;
            if (cfg == null || !cfg.EnableIdleWorkingSetTrim)
                return;

            // 只修剪本端实际持有句柄的进程；复用/外部手动启动的进程不碰
            if (!_ownsProcess || _host == null || !_host.IsProcessAlive)
                return;

            // 正在合成 / 其它桌宠正在合成 → 不动
            if (IsBusySynthesizing || (_gate?.IsSynthBusyElsewhere() ?? false))
                return;

            long now = DateTime.UtcNow.Ticks;

            // 同一进程只剪一次：剪枝后即使继续合成也不再剪（工作集不会再涨回高位）
            int pid = _host.ProcessId;
            lock (_idleGate)
            {
                if (_idleTrimmed && pid == _trimmedProcessId)
                    return;
                // 进程重启（PID 变化）→ 允许对新的进程再剪一次
                if (_idleTrimmed && pid != _trimmedProcessId)
                    _idleTrimmed = false;
            }

            // 距最近一次合成或需求超过阈值分钟才剪
            long synthIdle = now - Interlocked.Read(ref _lastSynthUtcTicks);
            long demandIdle = now - Interlocked.Read(ref _lastTtsDemandUtcTicks);
            long minIdle = (long)Math.Clamp(cfg.IdleWorkingSetTrimMinutes, 1, 720) * TimeSpan.TicksPerMinute;
            if (synthIdle < minIdle || demandIdle < minIdle)
                return;

            // 工作集已很低则不剪（避免无意义修剪）
            long minWs = (long)Math.Max(0, cfg.IdleTrimMinWorkingSetMiB) * 1048576;
            long currentWs = _host.GetWorkingSetSize();
            if (currentWs <= 0 || currentWs < minWs)
                return;

            lock (_idleGate)
            {
                _idleTrimmed = true;
                _trimmedProcessId = pid;
            }

            try
            {
                long before = _host.TrimWorkingSet();
                if (before > 0)
                {
                    LogImportant($"空闲剪枝：TTS 进程工作集已压回分页文件（before {before / 1048576d:F1} MiB）；同一进程仅剪一次，后续合成不再重复修剪");
                }
                else
                {
                    // 剪枝失败/无进程 → 复位标志，允许下次重试
                    lock (_idleGate) { _idleTrimmed = false; }
                }
            }
            catch (Exception ex)
            {
                lock (_idleGate) { _idleTrimmed = false; }
                LogWarn($"空闲工作集修剪失败：{ex.Message}");
            }
        }

        private async Task<bool> HealthCheckAsync(CancellationToken ct = default)
        {
            // 合成占用时健康检查易超时/失败，直接视为暂态 OK
            if (IsBusySynthesizing)
                return true;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

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
            // 10 分钟内已预热过（跨进程标记文件，多桌宠/多次开启共享）→ 跳过，避免重复占用 GPU
            DateTime? last = CosyVoiceSharedGate.GetLastWarmupUtc(
                Configuration?.Port ?? 0, Configuration?.ProjectDir);
            if (last.HasValue && (DateTime.UtcNow - last.Value) < TimeSpan.FromMinutes(10))
            {
                Log("近期已预热过，跳过");
                return;
            }

            try
            {
                // 预热必须真打一次推理：不能走 GenerateSpeechFileAsync（文件缓存命中不会触发 GPU 推理）
                string text = "你好";
                string spkid = string.IsNullOrWhiteSpace(Configuration?.ReferenceAudio)
                    ? "女主持"
                    : Configuration!.ReferenceAudio!.Trim();
                float speed = Configuration!.Speed <= 0 ? 1.0f : Configuration.Speed;
                string instruct = Configuration.Instruct?.Trim() ?? "";
                // 独立输出文件（不参与同句缓存查找），每次预热覆盖重合成
                string outputPath = Path.Combine(AlifePath.TempFolderPath, "cosy_warmup.wav");
                string hash = ComputeHash(text, spkid, speed, instruct) + "_warmup";
                string? path = await SynthesizeCoreAsync(
                    text, spkid, speed, instruct, outputPath, hash,
                    new InFlightSynth { Task = Task.FromResult<string?>(null), Waiters = 1 },
                    CancellationToken.None).ConfigureAwait(false);
                if (path != null)
                {
                    CosyVoiceSharedGate.MarkWarmup(Configuration.Port, Configuration.ProjectDir);
                    Log("预热完成");
                }
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

        private void NormalizeApiUrl() => NormalizeApiUrl(Configuration);

        private static void NormalizeApiUrl(CosyVoiceSpeechModelConfig? config)
        {
            if (config == null) return;
            string expected = $"http://127.0.0.1:{config.Port}";

            if (string.IsNullOrWhiteSpace(config.ApiUrl))
            {
                config.ApiUrl = expected;
                return;
            }

            string url = config.ApiUrl.Trim();
            if (url.Contains("127.0.0.1") || url.Contains("localhost"))
            {
                // 仅同步「纯本地根地址」（与端口保持一致）；带路径/查询的自定义地址（如反代子路径）不碰
                bool hasPath;
                try
                {
                    var u = new Uri(url);
                    hasPath = u.AbsolutePath.TrimEnd('/').Length > 0 || u.Query.Length > 0;
                }
                catch
                {
                    hasPath = true; // 解析失败：宁可不动用户配置
                }
                if (!hasPath)
                    config.ApiUrl = expected;
            }
        }

        private bool ValidatePaths(out string error) => ValidatePaths(Configuration, out error);

        private static bool ValidatePaths(CosyVoiceSpeechModelConfig? cfg, out string error)
        {
            error = "";
            if (cfg == null)
            {
                error = "配置为空";
                return false;
            }
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

        /// <summary>
        /// 清理过期的同句缓存 wav：先按年龄删（CacheMaxAgeHours），再按条数上限删最旧（CacheMaxFiles），
        /// 并同步修剪内存缓存表中指向已删文件的项。由看门狗每小时调用一次。
        /// </summary>
        private void SweepCache()
        {
            try
            {
                int maxAgeHours = Math.Max(0, Configuration?.CacheMaxAgeHours ?? 72);
                int maxFiles = Math.Max(0, Configuration?.CacheMaxFiles ?? 300);
                if (maxAgeHours == 0 && maxFiles == 0)
                    return;

                string dir = AlifePath.TempFolderPath;
                if (!Directory.Exists(dir))
                    return;

                var files = new List<FileInfo>();
                foreach (string raw in Directory.EnumerateFiles(dir, "cosy_*.wav"))
                {
                    try { files.Add(new FileInfo(raw)); } catch { /* ignore */ }
                }

                int removed = 0;
                if (maxAgeHours > 0)
                {
                    var cutoff = DateTime.UtcNow - TimeSpan.FromHours(maxAgeHours);
                    foreach (FileInfo f in files.ToList())
                    {
                        if (f.LastWriteTimeUtc >= cutoff) continue;
                        try { f.Delete(); files.Remove(f); removed++; } catch { /* ignore */ }
                    }
                }

                if (maxFiles > 0 && files.Count > maxFiles)
                {
                    foreach (FileInfo f in files
                        .OrderBy(f => f.LastWriteTimeUtc)
                        .Take(files.Count - maxFiles))
                    {
                        try { f.Delete(); removed++; } catch { /* ignore */ }
                    }
                }

                if (removed > 0)
                {
                    // 修剪指向已删文件的内存缓存项
                    foreach (var kv in FileCache)
                    {
                        if (!File.Exists(kv.Value))
                            FileCache.TryRemove(kv.Key, out _);
                    }
                    LogImportant($"缓存清理：删除 {removed} 个过期 wav");
                }
            }
            catch (Exception ex)
            {
                LogWarn($"缓存清理失败：{ex.Message}");
            }
        }

        private static string ComputeHash(
            string text, string spkid, float speed, string instruct)
        {
            using var md5 = MD5.Create();
            var raw = $"{text}|{spkid}|{speed:F2}|{instruct}";
            return Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(raw)));
        }

        /// <summary>
        /// 是否含可朗读内容（字母/数字/CJK 等）。纯标点、省略号、空白会让 Cosy 返回 500。
        /// </summary>
        private static bool HasSpeakableContent(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            foreach (var c in text)
            {
                if (char.IsLetterOrDigit(c))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 5xx 且像「文本拒读」而非「服务忙」：空 body + 文本几乎无可读字符 → 不重试。
        /// </summary>
        private static bool IsNonRetryableServerError(int code, string body, string text)
        {
            if (code < 500)
                return false;
            if (!HasSpeakableContent(text))
                return true;
            // 极短且 body 为空时，多为服务端对异常输入直接炸了
            if (string.IsNullOrWhiteSpace(body) && text.Length <= 4)
            {
                int speakable = 0;
                foreach (var c in text)
                {
                    if (char.IsLetterOrDigit(c))
                        speakable++;
                }
                if (speakable == 0)
                    return true;
            }
            return false;
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

        #endregion
    }

    #region Service Host

    /// <summary>
    /// 管理本地 CosyVoice Python 进程。
    /// - 独占模式：Stop 杀进程树；Windows Job（KILL_ON_JOB_CLOSE）随主进程带走子进程
    /// - 共享模式：不绑 Job；Detach 仅放弃句柄；Stop 仅在最后客户端调用
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

        // QuietLog=true（默认）：仅 Important/Warn/Error；false：全部输出
        private bool Quiet => _config.QuietLog;

        private void Log(string msg)
        {
            if (Quiet) return;
            Console.WriteLine($"[Cosy语音] {msg}");
        }

        private void LogImportant(string msg)
            => Console.WriteLine($"[Cosy语音] {msg}");

        private void LogWarn(string msg)
            => Console.WriteLine($"[Cosy语音][警告] {msg}");

        private void LogError(string msg)
            => Console.WriteLine($"[Cosy语音][错误] {msg}");

        /// <summary>静态清理路径用：始终输出（用户主动操作）。</summary>
        private static void LogAlways(string msg)
            => Console.WriteLine($"[Cosy语音] {msg}");

        public CosyVoiceServiceHost(CosyVoiceSpeechModelConfig config)
        {
            _config = config;
        }

        /// <summary>本实例是否仍持有存活的 Python 进程句柄。</summary>
        public bool IsProcessAlive
        {
            get
            {
                try { return _process != null && !_process.HasExited; }
                catch { return false; }
            }
        }

        /// <summary>端口是否处于 LISTENING（供就绪诊断）。</summary>
        public static bool IsPortInUse(int port) => IsPortListening(port);

        /// <summary>端口上是否还有存活进程（用于区分「加载中」与「僵尸占端口」）。</summary>
        public static bool IsPortProcessAlive(int port)
        {
            if (port <= 0) return false;
            try
            {
                foreach (int pid in GetPidsListeningOnPort(port))
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        if (!p.HasExited) return true;
                    }
                    catch (ArgumentException) { }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
            return false;
        }

        /// <summary>手动启动的独立服务标记文件（存在=服务不随桌宠/Alife 退出）。</summary>
        public static string ExternalMarkerPath(string? projectDir)
            => !string.IsNullOrWhiteSpace(projectDir) && Directory.Exists(projectDir)
                ? Path.Combine(projectDir, ".cosyvoice_alife_external")
                : Path.Combine(Path.GetTempPath(), ".cosyvoice_alife_external");

        public static bool ExternalMarkerExists(string? projectDir)
        {
            try { return File.Exists(ExternalMarkerPath(projectDir)); }
            catch { return false; }
        }

        public static void MarkExternalService(string? projectDir, int pid)
        {
            try { File.WriteAllText(ExternalMarkerPath(projectDir), pid.ToString()); }
            catch { /* ignore */ }
        }

        public static void ClearExternalMarker(string? projectDir)
        {
            try { if (File.Exists(ExternalMarkerPath(projectDir))) File.Delete(ExternalMarkerPath(projectDir)); }
            catch { /* ignore */ }
        }

        /// <summary>本实例持有的 Python 进程 PID（未持有返回 0）。</summary>
        public int ProcessId
        {
            get
            {
                lock (_lock)
                {
                    try { return _process?.Id ?? 0; }
                    catch { return 0; }
                }
            }
        }

        // ===== 空闲工作集修剪（借鉴 GptSovits：EmptyWorkingSet 只压物理 RSS，不碰显存）=====

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        /// <summary>
        /// 把本实例持有的 TTS 进程物理内存（Working Set）压回分页文件。
        /// 只降 RAM，不释放显存；下次合成会自动重新调入。返回修剪前工作集字节数，失败返回 0。
        /// </summary>
        public long TrimWorkingSet()
        {
            lock (_lock)
            {
                if (_process == null)
                    return 0;
                try
                {
                    if (_process.HasExited)
                        return 0;
                    _process.Refresh();
                    long before = _process.WorkingSet64;
                    if (!EmptyWorkingSet(_process.Handle))
                        return 0;
                    return before;
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>获取当前进程工作集字节数；无进程或失败返回 0。</summary>
        public long GetWorkingSetSize()
        {
            lock (_lock)
            {
                if (_process == null)
                    return 0;
                try
                {
                    if (_process.HasExited)
                        return 0;
                    _process.Refresh();
                    return _process.WorkingSet64;
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <param name="bindJob">
        /// true=绑定 KillOnClose Job（独占）；false=共享模式不绑，避免一个桌宠退出带走 TTS。
        /// </param>
        public void Start(bool bindJob = true)
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
                    $"--mode {mode}";

                _stderr.Clear();
                if (bindJob)
                    EnsureJobObject();
                else
                    CloseJobObject();

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
                // 稳延迟：限制 CPU 线程争抢，不伤 GPU 推理主路径
                _process.StartInfo.Environment["OMP_NUM_THREADS"] = "4";
                _process.StartInfo.Environment["MKL_NUM_THREADS"] = "4";
                // 内存优化：CUDA 模块按需加载（PyTorch 官方推荐），减少库常驻内存；MKL 动态分配线程缓冲
                _process.StartInfo.Environment["CUDA_MODULE_LOADING"] = "LAZY";
                _process.StartInfo.Environment["MKL_DYNAMIC"] = "TRUE";
                // 与 start.bat 一致：把项目自带 ffmpeg 放进 PATH，避免部分音频处理失败
                try
                {
                    string ffmpegDir = Path.Combine(_config.ProjectDir, "ffmpeg");
                    if (Directory.Exists(ffmpegDir))
                    {
                        string pathEnv = _process.StartInfo.Environment.TryGetValue("PATH", out var existing)
                            ? existing ?? ""
                            : (Environment.GetEnvironmentVariable("PATH") ?? "");
                        if (pathEnv.IndexOf(ffmpegDir, StringComparison.OrdinalIgnoreCase) < 0)
                            _process.StartInfo.Environment["PATH"] = ffmpegDir + Path.PathSeparator + pathEnv;
                    }
                }
                catch { /* ignore */ }

                // 不刷 stdout/stderr 到日志；仅缓存 stderr 供异常退出时摘要
                _process.OutputDataReceived += (_, _) => { };
                _process.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    lock (_stderr)
                    {
                        // 只保留末尾：异常退出时 Traceback 的结尾（根因所在）最关键
                        _stderr.AppendLine(e.Data);
                        if (_stderr.Length > 8000)
                            _stderr.Remove(0, _stderr.Length - 4000);
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

                    // 独占模式才绑 Job：Alife 异常退出时一并杀掉 Python
                    if (bindJob && _jobHandle != IntPtr.Zero)
                    {
                        if (!WindowsJob.Assign(_jobHandle, _process))
                            LogWarn("无法绑定进程组，异常退出时可能残留服务");
                    }

                    File.WriteAllText(PidFilePath, _process.Id.ToString());
                    LogImportant(bindJob
                        ? $"已启动（模式 {mode}，独占）"
                        : $"已启动（模式 {mode}，共享）");
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
        /// 共享模式：放弃本端进程句柄，不杀 Python（其它桌宠继续用）。
        /// </summary>
        public void Detach()
        {
            lock (_lock)
            {
                try
                {
                    if (_process != null)
                    {
                        try { _process.Dispose(); } catch { }
                        _process = null;
                    }
                    CloseJobObject();
                }
                catch (Exception ex)
                {
                    LogWarn($"Detach 异常：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 强制清理残留：PID 文件 + 监听端口的进程（含进程树）。
        /// 用于异常退出后的僵尸服务、半死不活占端口的情况。
        /// 返回结束的进程数。
        /// </summary>
        public int ForceCleanup()
        {
            lock (_lock)
            {
                return ForceCleanupUnlocked();
            }
        }

        /// <summary>
        /// 不依赖模块实例：按配置清理僵尸 TTS（PID 文件 + 端口 + 共享心跳文件）。
        /// 供配置 UI「清理僵尸进程」按钮使用；Alife 被强杀后也能在设置页点一次恢复。
        /// </summary>
        public static string CleanupOrphans(CosyVoiceSpeechModelConfig? config)
        {
            config ??= new CosyVoiceSpeechModelConfig();
            int port = config.Port > 0 ? config.Port : 9981;
            int webPort = config.WebPort > 0 ? config.WebPort : 9980;

            var host = new CosyVoiceServiceHost(config);
            int killed = host.ForceCleanup();
            ClearExternalMarker(config.ProjectDir);
            int cleared = CosyVoiceSharedGate.ClearStaleFiles(port, config.ProjectDir);

            bool portStill = IsPortListening(port);
            bool webStill = webPort != port && IsPortListening(webPort);

            // 用户主动点清理：始终输出结果
            if (killed > 0 || cleared > 0)
            {
                LogAlways($"手动清理完成：结束 {killed} 个进程，清理 {cleared} 个共享状态文件" +
                    (portStill || webStill ? "（端口仍占用，请稍候再试或检查任务管理器）" : "，端口已释放"));
            }
            else
            {
                LogAlways(portStill || webStill
                    ? $"手动清理：未识别到可杀进程，但端口仍占用（API={port}{(webStill ? $", Web={webPort}" : "")}）"
                    : "手动清理：未发现残留 TTS 进程");
            }

            if (killed == 0 && cleared == 0 && !portStill && !webStill)
                return "未发现残留 TTS 进程，端口空闲。";
            if (portStill || webStill)
                return $"已结束 {killed} 个进程、清理 {cleared} 个状态文件；端口仍占用，请稍等几秒或在任务管理器结束 python。";
            return $"已结束 {killed} 个进程、清理 {cleared} 个状态文件，端口已释放。可重新打开桌宠。";
        }

        private int ForceCleanupUnlocked()
        {
            var killed = new HashSet<int>();
            int killCount = 0;

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
                            if (KillProcessTree(pid))
                            {
                                killed.Add(pid);
                                killCount++;
                            }
                            else
                            {
                                killed.Add(pid);
                            }
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
                            killCount++;
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

            // 3) 按端口查杀（解决 PID 文件丢失 / 进程树残留）；KillProcessTree 内部已含 taskkill 兜底
            foreach (int port in new[] { _config.Port, _config.WebPort }.Distinct())
            {
                if (port <= 0) continue;
                foreach (int pid in GetPidsListeningOnPort(port))
                {
                    if (!killed.Add(pid)) continue;
                    if (KillProcessTree(pid))
                        killCount++;
                }
            }

            // 4) 兜底：端口仍被占用才再扫一遍（减少 netstat 次数）
            if (IsPortListening(_config.Port) || IsPortListening(_config.WebPort))
            {
                foreach (int port in new[] { _config.Port, _config.WebPort }.Distinct())
                {
                    if (port <= 0) continue;
                    foreach (int pid in GetPidsListeningOnPort(port))
                    {
                        if (killed.Contains(pid)) continue;
                        if (TaskKill(pid))
                        {
                            killed.Add(pid);
                            killCount++;
                        }
                    }
                }
            }

            if (killCount > 0)
                LogImportant($"已清理残留服务（{killCount} 个进程）");

            // 等端口释放
            for (int i = 0; i < 15 && IsPortListening(_config.Port); i++)
                Thread.Sleep(200);

            return killCount;
        }

        public void Stop()
        {
            lock (_lock)
            {
                try
                {
                    ForceCleanupUnlocked();
                    CloseJobObject();
                    LogImportant("服务已停止");
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

    /// <summary>
    /// 多桌宠共享同一 TTS 进程的跨进程协调：
    /// - 客户端注册表（心跳文件）
    /// - 启动互斥（只起一个 Python）
    /// - 合成互斥（全局串行，避免并发 500）
    /// </summary>
    internal sealed class CosyVoiceSharedGate : IDisposable
    {
        private readonly int _port;
        private readonly string _clientDir;
        private readonly string _clientFile;
        private readonly string _synthBusyFile;
        private readonly Mutex _startMutex;
        private readonly Mutex _synthMutex;
        private readonly string _clientId = $"{Environment.ProcessId}_{Guid.NewGuid():N}";
        private bool _registered;
        private bool _disposed;

        public CosyVoiceSharedGate(int port, string projectDir)
        {
            _port = port > 0 ? port : 9981;
            string root = !string.IsNullOrWhiteSpace(projectDir) && Directory.Exists(projectDir)
                ? projectDir
                : Path.GetTempPath();
            _clientDir = Path.Combine(root, ".cosyvoice_alife_clients");
            Directory.CreateDirectory(_clientDir);
            _clientFile = Path.Combine(_clientDir, $"p{_port}_{_clientId}.client");
            _synthBusyFile = Path.Combine(_clientDir, $"p{_port}.synthbusy");

            // 优先 Global\；无权限时回退 Local\
            _startMutex = CreateMutexSafe($@"Global\Alife.CosyVoice.Start.{_port}",
                $@"Local\Alife.CosyVoice.Start.{_port}");
            _synthMutex = CreateMutexSafe($@"Global\Alife.CosyVoice.Synth.{_port}",
                $@"Local\Alife.CosyVoice.Synth.{_port}");
        }

        private static Mutex CreateMutexSafe(string preferred, string fallback)
        {
            try { return new Mutex(false, preferred); }
            catch { return new Mutex(false, fallback); }
        }

        public void RegisterClient()
        {
            try
            {
                TouchClientFile();
                _registered = true;
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// 清理共享心跳 / 合成忙标记（Alife 被强杀后的僵尸状态）。
        /// 返回删除的文件数。不杀进程。
        /// </summary>
        public static int ClearStaleFiles(int port, string? projectDir)
        {
            int cleared = 0;
            port = port > 0 ? port : 9981;
            try
            {
                string root = !string.IsNullOrWhiteSpace(projectDir) && Directory.Exists(projectDir)
                    ? projectDir
                    : Path.GetTempPath();
                string clientDir = Path.Combine(root, ".cosyvoice_alife_clients");
                if (!Directory.Exists(clientDir))
                    return 0;

                foreach (var f in Directory.EnumerateFiles(clientDir, $"p{port}_*.client"))
                {
                    try
                    {
                        File.Delete(f);
                        cleared++;
                    }
                    catch { /* ignore */ }
                }

                string busy = Path.Combine(clientDir, $"p{port}.synthbusy");
                if (File.Exists(busy))
                {
                    try
                    {
                        File.Delete(busy);
                        cleared++;
                    }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
            return cleared;
        }

        /// <summary>注销并返回剩余存活客户端数（不含自己）。</summary>
        public int UnregisterClient()
        {
            try
            {
                if (File.Exists(_clientFile))
                    File.Delete(_clientFile);
            }
            catch { /* ignore */ }
            _registered = false;
            return CountLiveClients();
        }

        public int CountLiveClients()
        {
            try
            {
                if (!Directory.Exists(_clientDir))
                    return 0;

                var now = DateTime.UtcNow;
                int count = 0;
                foreach (var f in Directory.EnumerateFiles(_clientDir, $"p{_port}_*.client"))
                {
                    try
                    {
                        // 超过 90 秒未心跳视为僵尸
                        var age = now - File.GetLastWriteTimeUtc(f);
                        if (age > TimeSpan.FromSeconds(90))
                        {
                            try { File.Delete(f); } catch { }
                            continue;
                        }

                        // 文件内容为 "pid|timestamp"：进程已死则立即清理
                        if (!IsClientProcessAlive(f))
                        {
                            try { File.Delete(f); } catch { }
                            continue;
                        }

                        count++;
                    }
                    catch { /* ignore */ }
                }
                return count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 客户端文件内容为 "pid|..."；进程不存在则视为僵尸。
        /// </summary>
        private static bool IsClientProcessAlive(string clientFile)
        {
            try
            {
                string text = File.ReadAllText(clientFile).Trim();
                if (string.IsNullOrEmpty(text))
                    return true; // 旧格式无 pid，仅靠心跳时间判断

                string pidPart = text;
                int sep = text.IndexOf('|');
                if (sep >= 0)
                    pidPart = text[..sep];

                if (!int.TryParse(pidPart, out int pid) || pid <= 0)
                    return true;

                try
                {
                    var p = Process.GetProcessById(pid);
                    return !p.HasExited;
                }
                catch (ArgumentException)
                {
                    return false; // 进程不存在
                }
            }
            catch
            {
                return true;
            }
        }

        public IDisposable? TryAcquireStart(TimeSpan timeout)
        {
            try
            {
                if (!_startMutex.WaitOne(timeout))
                    return null;
                return new MutexHold(_startMutex);
            }
            catch (AbandonedMutexException)
            {
                // 前持有者崩溃，视为已获得
                return new MutexHold(_startMutex);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 获取跨进程合成锁。timeout 内拿不到返回 null（不再死等）。
        /// </summary>
        public async Task<IDisposable?> AcquireSynthAsync(TimeSpan timeout, CancellationToken ct)
        {
            // 心跳：长时间排队时仍标记本客户端存活
            TouchClientFile();
            var deadline = DateTime.UtcNow + timeout;
            bool triedForceClear = false;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // 若 busy 标记超过 60s（远超正常合成时间），视为僵尸，强制清掉再试一次
                if (!triedForceClear && IsSynthBusyFileStale(TimeSpan.FromSeconds(60)))
                {
                    try { File.Delete(_synthBusyFile); } catch { }
                    triedForceClear = true;
                }

                try
                {
                    if (_synthMutex.WaitOne(0))
                    {
                        MarkSynthBusy(true);
                        return new SynthHold(_synthMutex, () => MarkSynthBusy(false));
                    }
                }
                catch (AbandonedMutexException)
                {
                    MarkSynthBusy(true);
                    return new SynthHold(_synthMutex, () => MarkSynthBusy(false));
                }

                if (DateTime.UtcNow >= deadline)
                    return null;

                await Task.Delay(80, ct).ConfigureAwait(false);
                if ((Environment.TickCount & 0x3F) == 0)
                    TouchClientFile();
            }
        }

        private bool IsSynthBusyFileStale(TimeSpan maxAge)
        {
            try
            {
                if (!File.Exists(_synthBusyFile))
                    return false;
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_synthBusyFile);
                return age > maxAge;
            }
            catch { return false; }
        }

        /// <summary>兼容旧调用：无超时上限。</summary>
        public Task<IDisposable?> AcquireSynthAsync(CancellationToken ct)
            => AcquireSynthAsync(TimeSpan.FromHours(1), ct);

        /// <summary>其它进程是否正持有合成锁（本进程未持有时探测）。</summary>
        public bool IsSynthBusyElsewhere()
        {
            try
            {
                if (File.Exists(_synthBusyFile))
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_synthBusyFile);
                    // 忙标记超过 90 秒视为残留
                    if (age < TimeSpan.FromSeconds(90))
                    {
                        // 若 busy 文件里的 pid 已死，立即清掉
                        if (!IsClientProcessAlive(_synthBusyFile))
                        {
                            try { File.Delete(_synthBusyFile); } catch { }
                        }
                        else
                        {
                            return true;
                        }
                    }
                    else
                    {
                        try { File.Delete(_synthBusyFile); } catch { }
                    }
                }
            }
            catch { /* ignore */ }

            try
            {
                if (_synthMutex.WaitOne(0))
                {
                    _synthMutex.ReleaseMutex();
                    return false;
                }
                return true;
            }
            catch (AbandonedMutexException)
            {
                try { _synthMutex.ReleaseMutex(); } catch { }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void TouchClientFile()
        {
            try
            {
                File.WriteAllText(_clientFile, $"{Environment.ProcessId}|{DateTime.UtcNow:O}");
            }
            catch { /* ignore */ }
        }

        private void MarkSynthBusy(bool busy)
        {
            try
            {
                if (busy)
                    File.WriteAllText(_synthBusyFile, $"{Environment.ProcessId}|{DateTime.UtcNow:O}");
                else if (File.Exists(_synthBusyFile))
                    File.Delete(_synthBusyFile);
            }
            catch { /* ignore */ }
        }

        // ===== 预热标记（跨进程共享：多桌宠/多次开启不重复预热）=====

        private static string WarmupStampPath(int port, string? projectDir)
        {
            port = port > 0 ? port : 9981;
            string root = !string.IsNullOrWhiteSpace(projectDir) && Directory.Exists(projectDir)
                ? projectDir
                : Path.GetTempPath();
            return Path.Combine(root, ".cosyvoice_alife_clients", $"p{port}.warmup");
        }

        /// <summary>最近一次成功预热的 UTC 时间（无标记或读取失败返回 null）。</summary>
        public static DateTime? GetLastWarmupUtc(int port, string? projectDir)
        {
            try
            {
                string path = WarmupStampPath(port, projectDir);
                if (!File.Exists(path)) return null;
                return long.TryParse(File.ReadAllText(path).Trim(), out long ticks) && ticks > 0
                    ? new DateTime(ticks, DateTimeKind.Utc)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>记录本次预热成功时间（写入共享文件，跨进程可见）。</summary>
        public static void MarkWarmup(int port, string? projectDir)
        {
            try
            {
                string path = WarmupStampPath(port, projectDir);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DateTime.UtcNow.Ticks.ToString());
            }
            catch { /* ignore */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_registered)
                    UnregisterClient();
            }
            catch { /* ignore */ }
            try { _startMutex.Dispose(); } catch { }
            try { _synthMutex.Dispose(); } catch { }
        }

        private sealed class MutexHold : IDisposable
        {
            private Mutex? _m;
            public MutexHold(Mutex m) => _m = m;
            public void Dispose()
            {
                var m = Interlocked.Exchange(ref _m, null);
                if (m == null) return;
                try { m.ReleaseMutex(); } catch { }
            }
        }

        private sealed class SynthHold : IDisposable
        {
            private Mutex? _m;
            private Action? _onRelease;
            public SynthHold(Mutex m, Action onRelease)
            {
                _m = m;
                _onRelease = onRelease;
            }
            public void Dispose()
            {
                var on = Interlocked.Exchange(ref _onRelease, null);
                try { on?.Invoke(); } catch { }
                var m = Interlocked.Exchange(ref _m, null);
                if (m == null) return;
                try { m.ReleaseMutex(); } catch { }
            }
        }
    }

    #endregion
}
