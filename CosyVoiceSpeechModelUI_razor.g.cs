using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using AntDesign;

namespace Alife.Function.Speech.CosyVoiceTTS
{
    public partial class CosyVoiceSpeechModelUI
        : ModuleUIBase<CosyVoiceSpeechModel, CosyVoiceSpeechModelConfig>
    {
        private static readonly string[] BuiltinSpeakers =
        {
            "女主持", "男主持", "曼波", "马保国", "周星驰", "蔡徐坤",
            "纳西妲", "日本萝莉音", "elysia_100", "Kyuui", "胡桃", "泠鸢",
            "白雪眠", "牛肉AI", "牛肉AI-2", "疯狂米塔", "香奈美", "日本女优",
            "S1", "S2", "67"
        };

        private List<string> _speakerOptions = new(BuiltinSpeakers);
        private string _speakerHint = "内置列表（服务未连接时）";
        private bool _refreshing;

        // 星语粉紫 · Cosy 语音台：粉点缀 + 紫雾流光 + 声波装饰
        private const string Css = @"
/* ===== 容器 ===== */
.cv-container {
    position: relative;
    overflow: hidden;
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'PingFang SC', sans-serif;
    color: #3d2a4a;
    background:
        radial-gradient(ellipse 80% 50% at 10% 0%, rgba(255,182,213,0.45) 0%, transparent 55%),
        radial-gradient(ellipse 70% 45% at 95% 10%, rgba(196,160,255,0.35) 0%, transparent 50%),
        radial-gradient(ellipse 60% 40% at 50% 100%, rgba(255,143,191,0.2) 0%, transparent 55%),
        linear-gradient(145deg, #fff8fc 0%, #f8f0ff 42%, #fff5f9 100%);
    padding: 26px 24px 28px;
    border-radius: 18px;
    border: 1px solid rgba(255,160,200,0.55);
    max-width: 720px;
    box-shadow:
        0 12px 40px rgba(200,100,160,0.14),
        0 4px 14px rgba(140,90,200,0.08),
        inset 0 1px 0 rgba(255,255,255,0.85);
    animation: cv-fade-in 0.45s cubic-bezier(0.22,1,0.36,1);
}
.cv-container::before {
    content: '';
    position: absolute;
    inset: 0;
    pointer-events: none;
    background:
        linear-gradient(115deg, transparent 40%, rgba(255,255,255,0.35) 50%, transparent 60%);
    background-size: 220% 100%;
    animation: cv-sheen 7s ease-in-out infinite;
    opacity: 0.55;
}
.cv-container::after {
    content: '';
    position: absolute;
    left: 18px; right: 18px; top: 0;
    height: 3px;
    border-radius: 0 0 8px 8px;
    background: linear-gradient(90deg, #ff8fbf, #c9a0ff, #ff6b9d, #b388ff, #ff8fbf);
    background-size: 200% 100%;
    animation: cv-bar 4s linear infinite;
    box-shadow: 0 0 12px rgba(255,107,157,0.45);
}
@keyframes cv-fade-in {
    from { opacity: 0; transform: translateY(14px) scale(0.985); }
    to { opacity: 1; transform: translateY(0) scale(1); }
}
@keyframes cv-sheen {
    0%,100% { background-position: 120% 0; }
    50% { background-position: -20% 0; }
}
@keyframes cv-bar {
    0% { background-position: 0% 50%; }
    100% { background-position: 200% 50%; }
}

/* ===== AntDesign 覆盖 ===== */
.cv-container .ant-input {
    border-color: #f0c4d8 !important;
    border-radius: 10px !important;
    background: rgba(255,255,255,0.88) !important;
    transition: all 0.25s ease !important;
}
.cv-container .ant-input:hover {
    border-color: #ff8fbf !important;
    box-shadow: 0 0 0 3px rgba(255,143,191,0.12) !important;
}
.cv-container .ant-input:focus,
.cv-container .ant-input-focused {
    border-color: #c9a0ff !important;
    box-shadow: 0 0 0 3px rgba(201,160,255,0.22) !important;
}
.cv-container .ant-input-affix-wrapper {
    border-color: #f0c4d8 !important;
    border-radius: 10px !important;
    background: rgba(255,255,255,0.88) !important;
}
.cv-container .ant-input-affix-wrapper:hover { border-color: #ff8fbf !important; }
.cv-container .ant-input-affix-wrapper-focused {
    border-color: #c9a0ff !important;
    box-shadow: 0 0 0 3px rgba(201,160,255,0.22) !important;
}

/* ===== 标题区 ===== */
.cv-header {
    position: relative;
    z-index: 1;
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 10px 14px;
    margin-bottom: 14px;
}
.cv-title {
    font-size: 22px;
    font-weight: 800;
    letter-spacing: 0.5px;
    line-height: 1.25;
    background: linear-gradient(100deg, #ff6b9d 0%, #c9a0ff 35%, #ff8fbf 55%, #9b6dff 75%, #ff6b9d 100%);
    background-size: 220% auto;
    -webkit-background-clip: text;
    background-clip: text;
    -webkit-text-fill-color: transparent;
    animation: cv-shimmer 3.2s linear infinite;
    filter: drop-shadow(0 2px 8px rgba(255,107,157,0.18));
}
.cv-title-sub {
    font-size: 11px;
    font-weight: 600;
    color: #b07a9a;
    letter-spacing: 1.5px;
    text-transform: uppercase;
    opacity: 0.9;
}
@keyframes cv-shimmer {
    0% { background-position: 0% center; }
    100% { background-position: 220% center; }
}

/* 声波装饰条 */
.cv-wave {
    display: flex;
    align-items: flex-end;
    gap: 3px;
    height: 18px;
    margin-left: 2px;
}
.cv-wave span {
    display: block;
    width: 3px;
    border-radius: 2px;
    background: linear-gradient(180deg, #ff8fbf, #c9a0ff);
    animation: cv-wave 1.1s ease-in-out infinite;
}
.cv-wave span:nth-child(1) { height: 6px; animation-delay: 0s; }
.cv-wave span:nth-child(2) { height: 12px; animation-delay: 0.12s; }
.cv-wave span:nth-child(3) { height: 18px; animation-delay: 0.24s; }
.cv-wave span:nth-child(4) { height: 10px; animation-delay: 0.36s; }
.cv-wave span:nth-child(5) { height: 15px; animation-delay: 0.48s; }
.cv-wave span:nth-child(6) { height: 7px; animation-delay: 0.6s; }
@keyframes cv-wave {
    0%,100% { transform: scaleY(0.55); opacity: 0.65; }
    50% { transform: scaleY(1); opacity: 1; }
}

/* ===== 状态徽标 ===== */
.cv-badge-on, .cv-badge-off {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 4px 12px;
    border-radius: 999px;
    font-size: 11px;
    font-weight: 700;
    letter-spacing: 0.3px;
    vertical-align: middle;
}
.cv-badge-on {
    background: linear-gradient(135deg, #ff6b9d, #d46bff);
    color: #fff;
    box-shadow: 0 3px 12px rgba(255,20,147,0.35), 0 0 0 1px rgba(255,255,255,0.25) inset;
}
.cv-badge-on::before {
    content: '\25CF';
    font-size: 9px;
    animation: cv-pulse 1.6s ease-in-out infinite;
}
.cv-badge-off {
    background: linear-gradient(135deg, #f3eef6, #ebe4f0);
    color: #9a8aa8;
    border: 1px solid #e2d8ea;
}
.cv-badge-off::before { content: '\25CB'; font-size: 9px; }
@keyframes cv-pulse {
    0%,100% { opacity: 1; transform: scale(1); text-shadow: 0 0 4px #fff; }
    50% { opacity: 0.45; transform: scale(0.75); }
}

/* ===== 信息框 ===== */
.cv-alert {
    position: relative;
    z-index: 1;
    display: flex;
    gap: 12px;
    align-items: flex-start;
    background: linear-gradient(135deg, rgba(255,240,248,0.95), rgba(245,236,255,0.92));
    border: 1px solid rgba(255,160,200,0.45);
    border-left: 4px solid #ff6b9d;
    border-radius: 12px;
    padding: 12px 14px;
    margin-bottom: 16px;
    box-shadow: 0 4px 16px rgba(255,107,157,0.08);
}
.cv-alert-icon {
    flex-shrink: 0;
    width: 28px; height: 28px;
    border-radius: 50%;
    display: flex; align-items: center; justify-content: center;
    font-size: 14px;
    background: linear-gradient(135deg, #ff8fbf, #c9a0ff);
    color: #fff;
    box-shadow: 0 3px 10px rgba(255,107,157,0.35);
}
.cv-alert-body { flex: 1; min-width: 0; }
.cv-alert-title {
    font-weight: 800;
    font-size: 13px;
    color: #c2185b;
    margin-bottom: 4px;
}
.cv-alert-desc {
    font-size: 12px;
    color: #8a5a78;
    line-height: 1.65;
}

/* ===== 分区标题 ===== */
.cv-section {
    position: relative;
    z-index: 1;
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 22px 0 12px;
    padding-bottom: 8px;
    font-size: 14px;
    font-weight: 800;
    color: #9b4d7a;
    border-bottom: 2px solid transparent;
    border-image: linear-gradient(90deg, #ff6b9d, #c9a0ff, transparent) 1;
}
.cv-section-icon {
    width: 22px; height: 22px;
    border-radius: 7px;
    display: inline-flex; align-items: center; justify-content: center;
    font-size: 12px;
    background: linear-gradient(135deg, #ffe0ef, #efe0ff);
    border: 1px solid rgba(255,143,191,0.45);
    box-shadow: 0 2px 6px rgba(201,160,255,0.2);
}

/* ===== 标签 / 提示 ===== */
.cv-label {
    position: relative;
    z-index: 1;
    margin-top: 12px;
    margin-bottom: 5px;
    font-weight: 700;
    font-size: 13px;
    color: #8e3d6b;
    display: flex;
    align-items: center;
    gap: 5px;
}
.cv-label::before {
    content: '\25B8';
    color: #ff6b9d;
    font-size: 11px;
    text-shadow: 0 0 6px rgba(255,107,157,0.45);
}
.cv-hint {
    position: relative;
    z-index: 1;
    font-size: 11px;
    color: #b07a9a;
    margin: 0 0 8px 2px;
    line-height: 1.55;
    padding: 6px 10px;
    border-radius: 8px;
    background: rgba(255,255,255,0.45);
    border-left: 3px solid #ffb6d0;
}

/* ===== 按钮 ===== */
.cv-row {
    position: relative;
    z-index: 1;
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
    margin-top: 8px;
    margin-bottom: 6px;
}
.cv-btn {
    position: relative;
    overflow: hidden;
    border: 1.5px solid #f0c4d8;
    background: linear-gradient(145deg, #fff, #fff5fa);
    color: #9b4d7a;
    border-radius: 999px;
    padding: 6px 14px;
    font-size: 12px;
    font-weight: 700;
    font-family: inherit;
    cursor: pointer;
    transition: all 0.28s cubic-bezier(0.4,0,0.2,1);
    box-shadow: 0 2px 6px rgba(255,143,191,0.1);
}
.cv-btn::after {
    content: '';
    position: absolute;
    inset: 0;
    background: linear-gradient(120deg, transparent 30%, rgba(255,255,255,0.55) 50%, transparent 70%);
    transform: translateX(-120%);
    transition: transform 0.5s ease;
}
.cv-btn:hover {
    border-color: transparent;
    color: #fff;
    background: linear-gradient(135deg, #ff6b9d, #c9a0ff);
    box-shadow: 0 8px 22px rgba(255,107,157,0.35);
    transform: translateY(-3px);
}
.cv-btn:hover::after { transform: translateX(120%); }
.cv-btn:active { transform: translateY(-1px); }
.cv-btn-primary {
    border-color: transparent;
    color: #fff;
    background: linear-gradient(135deg, #ff6b9d 0%, #d46bff 50%, #9b6dff 100%);
    background-size: 180% 180%;
    animation: cv-btn-glow 3s ease infinite;
    box-shadow: 0 4px 16px rgba(212,107,255,0.4);
}
.cv-btn-primary:hover {
    background: linear-gradient(135deg, #ff8fbf, #e0a0ff, #b388ff);
    box-shadow: 0 10px 28px rgba(155,109,255,0.45);
}
@keyframes cv-btn-glow {
    0%,100% { background-position: 0% 50%; }
    50% { background-position: 100% 50%; }
}
.cv-btn-refresh {
    border-color: #d4b8ff;
    color: #7b4db8;
    background: linear-gradient(145deg, #faf5ff, #fff);
}
.cv-btn-refresh:hover {
    background: linear-gradient(135deg, #c9a0ff, #9b6dff);
    color: #fff;
}
.cv-btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
    transform: none !important;
    box-shadow: none !important;
}

/* Mode 快捷芯片 */
.cv-chip-row {
    position: relative;
    z-index: 1;
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin: 6px 0 4px;
}
.cv-chip {
    border: 1.5px solid #e8d0f0;
    background: rgba(255,255,255,0.75);
    color: #8a5a9a;
    border-radius: 10px;
    padding: 7px 12px;
    font-size: 12px;
    font-weight: 700;
    font-family: inherit;
    cursor: pointer;
    transition: all 0.25s ease;
}
.cv-chip:hover {
    border-color: #ff8fbf;
    color: #c2185b;
    box-shadow: 0 4px 14px rgba(255,143,191,0.2);
    transform: translateY(-2px);
}
.cv-chip-on {
    border-color: transparent;
    color: #fff;
    background: linear-gradient(135deg, #ff6b9d, #b388ff);
    box-shadow: 0 4px 14px rgba(179,136,255,0.4);
}

/* ===== 折叠组 ===== */
.cv-details {
    position: relative;
    z-index: 1;
    margin-top: 16px;
    border: 1.5px solid rgba(255,160,200,0.5);
    border-radius: 14px;
    padding: 10px 14px;
    background: linear-gradient(160deg, rgba(255,255,255,0.92), rgba(255,248,252,0.88));
    transition: all 0.3s cubic-bezier(0.4,0,0.2,1);
    box-shadow: 0 2px 10px rgba(200,120,180,0.06);
}
.cv-details:hover {
    border-color: #ff8fbf;
    box-shadow: 0 8px 28px rgba(255,107,157,0.16);
    transform: translateY(-2px);
}
.cv-details > summary {
    cursor: pointer;
    font-weight: 800;
    font-size: 13px;
    color: #9b4d7a;
    padding: 4px 0;
    user-select: none;
    display: flex;
    align-items: center;
    gap: 8px;
    list-style: none;
}
.cv-details > summary::-webkit-details-marker { display: none; }
.cv-arrow {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 18px; height: 18px;
    border-radius: 6px;
    font-size: 10px;
    color: #fff;
    background: linear-gradient(135deg, #ff6b9d, #c9a0ff);
    transition: transform 0.25s ease;
    box-shadow: 0 2px 6px rgba(255,107,157,0.3);
}
.cv-details[open] .cv-arrow { transform: rotate(90deg); }
.cv-details[open] {
    background: linear-gradient(160deg, #fff, #fff6fb 60%, #f8f0ff);
    box-shadow: 0 6px 22px rgba(201,160,255,0.14);
}
.cv-details-content { margin-top: 10px; padding-top: 4px; }

/* 底部小彩条 */
.cv-footer {
    position: relative;
    z-index: 1;
    margin-top: 18px;
    padding-top: 12px;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    flex-wrap: wrap;
    border-top: 1px dashed rgba(255,160,200,0.45);
}
.cv-footer-text {
    font-size: 11px;
    color: #b07a9a;
    font-weight: 600;
}
.cv-sparkle {
    font-size: 12px;
    background: linear-gradient(90deg, #ff6b9d, #c9a0ff, #ff8fbf);
    background-size: 200% auto;
    -webkit-background-clip: text;
    background-clip: text;
    -webkit-text-fill-color: transparent;
    animation: cv-shimmer 2.5s linear infinite;
    font-weight: 800;
}
";

        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();
            await TryLoadSpeakersAsync();
        }

        private async Task TryLoadSpeakersAsync()
        {
            if (Configuration == null) return;

            try
            {
                string baseUrl = Configuration.ApiUrl?.TrimEnd('/') ?? "http://127.0.0.1:9981";
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                string json = await http.GetStringAsync(baseUrl + "/get_spk");
                var list = JsonSerializer.Deserialize<string[]>(json);
                if (list is { Length: > 0 })
                {
                    _speakerOptions = list.ToList();
                    _speakerHint = $"已从服务加载 {_speakerOptions.Count} 个音色";

                    if (string.IsNullOrWhiteSpace(Configuration.ReferenceAudio) ||
                        !_speakerOptions.Any(s =>
                            string.Equals(s, Configuration.ReferenceAudio, StringComparison.OrdinalIgnoreCase)))
                    {
                        Configuration.ReferenceAudio =
                            _speakerOptions.FirstOrDefault(s => s.Contains("女主持"))
                            ?? _speakerOptions[0];
                    }
                }
            }
            catch
            {
                _speakerHint = "服务未就绪，显示内置音色列表";
            }
        }

        private async Task OnRefreshSpeakers()
        {
            _refreshing = true;
            StateHasChanged();
            try
            {
                await TryLoadSpeakersAsync();
            }
            finally
            {
                _refreshing = false;
            }
        }

        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenElement(0, "style");
            b.AddContent(1, Css);
            b.CloseElement();

            if (Configuration == null)
            {
                b.AddContent(2, "Configuration NULL");
                return;
            }

            int i = 2;
            bool connected = _speakerHint.StartsWith("已从服务");

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-container");

            // ---- 标题 ----
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-header");

            b.OpenElement(i++, "div");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-title");
            b.AddContent(i++, "Cosy 星语 · 语音合成");
            b.CloseElement();
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-title-sub");
            b.AddContent(i++, "CosyVoice2  ·  yinmei");
            b.CloseElement();
            b.CloseElement();

            // 声波
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-wave");
            for (int w = 0; w < 6; w++)
            {
                b.OpenElement(i++, "span");
                b.CloseElement();
            }
            b.CloseElement();

            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", connected ? "cv-badge-on" : "cv-badge-off");
            b.AddContent(i++, connected ? "服务已连接" : "服务未连接");
            b.CloseElement();

            b.CloseElement(); // header

            // ---- 说明 ----
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-alert");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-alert-icon");
            b.AddContent(i++, "\u266A");
            b.CloseElement();
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-alert-body");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-alert-title");
            b.AddContent(i++, "本地 CosyVoice2 引擎");
            b.CloseElement();
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-alert-desc");
            b.AddContent(i++,
                "基于 yinmei-cosyvoice。推荐 Mode=1（仅 API）更省资源；风格指令留空则用音色默认语气。修改路径/端口后请重新加载模块。");
            b.CloseElement();
            b.CloseElement();
            b.CloseElement();

            // ---- 音色与效果 ----
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-section");
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "cv-section-icon");
            b.AddContent(i++, "\u266B");
            b.CloseElement();
            b.AddContent(i++, "音色与效果");
            b.CloseElement();

            AddLabel(b, ref i, "音色 spkid");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-hint");
            b.AddContent(i++, _speakerHint + " · " + string.Join(" / ", _speakerOptions.Take(12))
                + (_speakerOptions.Count > 12 ? " …" : ""));
            b.CloseElement();

            AddInput(b, ref i, "当前音色（可手输，或点下方快捷选择）",
                Configuration.ReferenceAudio ?? "",
                v => Configuration.ReferenceAudio = v);

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-row");
            foreach (var spk in _speakerOptions.Take(12))
            {
                var capture = spk;
                bool selected = string.Equals(Configuration.ReferenceAudio, capture, StringComparison.Ordinal);
                b.OpenElement(i++, "button");
                b.AddAttribute(i++, "type", "button");
                b.AddAttribute(i++, "class", selected ? "cv-btn cv-btn-primary" : "cv-btn");
                b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, () =>
                {
                    Configuration.ReferenceAudio = capture;
                    StateHasChanged();
                }));
                b.AddContent(i++, capture);
                b.CloseElement();
            }

            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", "cv-btn cv-btn-refresh");
            b.AddAttribute(i++, "disabled", _refreshing);
            b.AddAttribute(i++, "onclick",
                EventCallback.Factory.Create(this, async () => await OnRefreshSpeakers()));
            b.AddContent(i++, _refreshing ? "刷新中…" : "刷新音色");
            b.CloseElement();
            b.CloseElement();

            AddInput(b, ref i, "风格指令 Instruct（可选，如：用开心的语气说）",
                Configuration.Instruct ?? "",
                v => Configuration.Instruct = v);

            AddInput(b, ref i, "语速 Speed（默认 1.0）",
                Configuration.Speed.ToString("0.##"),
                v =>
                {
                    if (float.TryParse(v, out var f) && f > 0)
                        Configuration.Speed = f;
                });

            // ---- 服务与路径 ----
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-section");
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "cv-section-icon");
            b.AddContent(i++, "\u2699");
            b.CloseElement();
            b.AddContent(i++, "服务与路径");
            b.CloseElement();

            AddInput(b, ref i, "API 地址",
                Configuration.ApiUrl,
                v => Configuration.ApiUrl = v);

            AddInput(b, ref i, "API 端口",
                Configuration.Port.ToString(),
                v =>
                {
                    if (int.TryParse(v, out var n) && n > 0)
                    {
                        Configuration.Port = n;
                        Configuration.ApiUrl = $"http://127.0.0.1:{n}";
                    }
                });

            AddLabel(b, ref i, "启动模式 Mode");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-hint");
            b.AddContent(i++, "1 = 仅 API（推荐） · 2 = 仅 WebUI · 3 = API + WebUI");
            b.CloseElement();

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-chip-row");
            AddModeChip(b, ref i, 1, "Mode 1 · 仅 API");
            AddModeChip(b, ref i, 2, "Mode 2 · WebUI");
            AddModeChip(b, ref i, 3, "Mode 3 · 双开");
            b.CloseElement();

            AddLabel(b, ref i, "自动启动 AutoStart");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-chip-row");
            AddBoolChip(b, ref i, true, "开启自动启动");
            AddBoolChip(b, ref i, false, "仅连接已有服务");
            b.CloseElement();

            // ---- 高级折叠 ----
            b.OpenElement(i++, "details");
            b.AddAttribute(i++, "class", "cv-details");

            b.OpenElement(i++, "summary");
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "cv-arrow");
            b.AddContent(i++, "\u25B6");
            b.CloseElement();
            b.AddContent(i++, "高级路径与超时");
            b.CloseElement();

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-details-content");

            AddInput(b, ref i, "Python 路径",
                Configuration.PythonPath,
                v => Configuration.PythonPath = v);

            AddInput(b, ref i, "项目目录 ProjectDir",
                Configuration.ProjectDir,
                v => Configuration.ProjectDir = v);

            AddInput(b, ref i, "模型目录 ModelDir",
                Configuration.ModelDir,
                v => Configuration.ModelDir = v);

            AddInput(b, ref i, "WebUI 端口（Mode=2/3 时）",
                Configuration.WebPort.ToString(),
                v =>
                {
                    if (int.TryParse(v, out var n) && n > 0)
                        Configuration.WebPort = n;
                });

            AddInput(b, ref i, "流式切片 LimitCount（越大越稳；非流式影响小）",
                Configuration.LimitCount.ToString(),
                v =>
                {
                    if (int.TryParse(v, out var n) && n > 0)
                        Configuration.LimitCount = n;
                });

            AddInput(b, ref i, "请求超时秒数",
                Configuration.RequestTimeoutSeconds.ToString(),
                v =>
                {
                    if (int.TryParse(v, out var n) && n >= 10)
                        Configuration.RequestTimeoutSeconds = n;
                });

            AddInput(b, ref i, "就绪等待秒数",
                Configuration.ReadyTimeoutSeconds.ToString(),
                v =>
                {
                    if (int.TryParse(v, out var n) && n >= 30)
                        Configuration.ReadyTimeoutSeconds = n;
                });

            AddInput(b, ref i, "失败重启阈值 RestartAfterFailures",
                Configuration.RestartAfterFailures.ToString(),
                v =>
                {
                    if (int.TryParse(v, out var n) && n >= 1)
                        Configuration.RestartAfterFailures = n;
                });

            b.CloseElement(); // details-content
            b.CloseElement(); // details

            // footer
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cv-footer");
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "cv-footer-text");
            b.AddContent(i++, "粉紫星语台 · 本地语音引擎");
            b.CloseElement();
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "cv-sparkle");
            b.AddContent(i++, "\u2726 Speak with Cosy \u2726");
            b.CloseElement();
            b.CloseElement();

            b.CloseElement(); // container
        }

        private void AddModeChip(RenderTreeBuilder b, ref int seq, int mode, string label)
        {
            bool on = Configuration!.Mode == mode;
            b.OpenElement(seq++, "button");
            b.AddAttribute(seq++, "type", "button");
            b.AddAttribute(seq++, "class", on ? "cv-chip cv-chip-on" : "cv-chip");
            b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () =>
            {
                Configuration.Mode = mode;
                StateHasChanged();
            }));
            b.AddContent(seq++, label);
            b.CloseElement();
        }

        private void AddBoolChip(RenderTreeBuilder b, ref int seq, bool value, string label)
        {
            bool on = Configuration!.AutoStart == value;
            b.OpenElement(seq++, "button");
            b.AddAttribute(seq++, "type", "button");
            b.AddAttribute(seq++, "class", on ? "cv-chip cv-chip-on" : "cv-chip");
            b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () =>
            {
                Configuration.AutoStart = value;
                StateHasChanged();
            }));
            b.AddContent(seq++, label);
            b.CloseElement();
        }

        private static void AddLabel(RenderTreeBuilder b, ref int seq, string label)
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "cv-label");
            b.AddContent(seq++, label);
            b.CloseElement();
        }

        private void AddInput(
            RenderTreeBuilder b,
            ref int seq,
            string label,
            string value,
            Action<string> setter)
        {
            AddLabel(b, ref seq, label);

            b.OpenComponent<Input<string>>(seq++);
            b.AddAttribute(seq++, "Value", value);
            b.AddAttribute(
                seq++,
                "ValueChanged",
                EventCallback.Factory.Create<string>(this, setter));
            b.CloseComponent();
        }
    }
}
