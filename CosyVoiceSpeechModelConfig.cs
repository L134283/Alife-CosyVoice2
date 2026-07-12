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
    /// 流式切片字符数（limit_count）。
    /// 越小首包越快但后续更易卡顿；非流式时影响较小。
    /// </summary>
    public int LimitCount { get; set; } = 10;

    /// <summary>
    /// 启动模式：1=仅 API（推荐，省资源），2=仅 WebUI，3=API+WebUI。
    /// </summary>
    public int Mode { get; set; } = 1;

    /// <summary>是否由插件自动拉起本地服务；若你已手动启动可设为 false。</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>单次 TTS HTTP 超时（秒）。</summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>等待服务就绪的最长时间（秒）。</summary>
    public int ReadyTimeoutSeconds { get; set; } = 180;

    /// <summary>健康检查失败多少次后重启服务。</summary>
    public int RestartAfterFailures { get; set; } = 6;
}
