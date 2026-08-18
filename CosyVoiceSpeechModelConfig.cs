namespace Alife.Function.Speech.CosyVoiceTTS;

public class CosyVoiceSpeechModelConfig
{
    /// <summary>API 根地址，需与 Port 一致。</summary>
    public string ApiUrl { get; set; } = "http://127.0.0.1:9981";

    /// <summary>音色 spkid，对应 /get_spk 返回的名称。</summary>
    public string ReferenceAudio { get; set; } = "女主持";

    /// <summary>语速，1.0 为正常。</summary>
    public float Speed { get; set; } = 1.0f;

    /// <summary>风格指令，如「用开心的语气说」；留空则不传。</summary>
    public string Instruct { get; set; } = "";

    /// <summary>yinmei-cosyvoice 自带 Python 解释器路径。</summary>
    public string PythonPath { get; set; }
        = @"D:\1TTS\yinmei-cosyvoice\python311\python.exe";

    /// <summary>yinmei-cosyvoice 项目根目录。</summary>
    public string ProjectDir { get; set; }
        = @"D:\1TTS\yinmei-cosyvoice";

    /// <summary>CosyVoice2 模型目录。</summary>
    public string ModelDir { get; set; }
        = @"D:\1TTS\yinmei-cosyvoice\pretrained_models\CosyVoice2-0.5B";

    /// <summary>API 端口。</summary>
    public int Port { get; set; } = 9981;

    /// <summary>WebUI 端口（仅 mode=2/3 时使用）。</summary>
    public int WebPort { get; set; } = 9980;

    /// <summary>
    /// 启动模式：1=仅 API（推荐，省资源），2=仅 WebUI，3=API+WebUI。
    /// </summary>
    public int Mode { get; set; } = 1;

    /// <summary>是否由插件自动拉起本地服务；若你已手动启动可设为 false。</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>
    /// 多桌宠共享同一 TTS 进程。单桌宠建议关闭：更快、不卡启动锁、叉掉 Alife 自动停服。
    /// 开启后：同端口只保留一个 Python；跨进程串行合成；退出时仅最后一位客户端停服。
    /// </summary>
    public bool SharedService { get; set; } = false;

    /// <summary>单次 TTS HTTP 超时（秒）。</summary>
    public int RequestTimeoutSeconds { get; set; } = 90;

    /// <summary>等待服务就绪的最长时间（秒）。</summary>
    public int ReadyTimeoutSeconds { get; set; } = 180;

    /// <summary>健康检查连续失败多少次后重启服务。</summary>
    public int RestartAfterFailures { get; set; } = 8;

    /// <summary>等待进程内/跨进程合成锁的最长时间（秒），超时放弃本次合成并触发恢复。</summary>
    public int SynthLockTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 安静日志（默认开）：开启后仅输出错误与重要状态变更（就绪/重启/停服等），
    /// 日常合成过程日志全部静默；关闭则输出完整调试日志。
    /// </summary>
    public bool QuietLog { get; set; } = true;

    // === 空闲工作集修剪（借鉴 GptSovits：EmptyWorkingSet 只压物理 RSS，不碰显存）===
    /// <summary>空闲超阈值后把 TTS 进程物理内存（Working Set）压回分页文件。只降 RAM，不释放显存。</summary>
    public bool EnableIdleWorkingSetTrim { get; set; } = true;

    /// <summary>连续空闲（无合成/无播放）多少分钟后触发剪枝。</summary>
    public int IdleWorkingSetTrimMinutes { get; set; } = 10;

    /// <summary>空闲监控轮询间隔（秒，5-300；不暴露 UI，可在配置 JSON 调整）。</summary>
    public int IdleTrimPollSeconds { get; set; } = 20;
}
