📘 CosyVoice2 语音合成插件 1.0.1

基于 CosyVoice2 API 的语音合成插件，支持 UI 配置、自定义服务路径、自动服务管理与稳定性增强。

✨ 功能特性
🎤 高质量语音合成（CosyVoice2）
🧩 可视化 UI 配置面板
📁 自定义服务路径（Python / 模型 / 项目目录）
🚀 自动启动后端服务
🛡️ 健康检查与异常自动重启
🔁 请求重试机制
⚡ 本地缓存优化响应速度
📦 插件版本
🟢 v1.0.1（当前版本）
新增 UI 配置面板
支持自定义服务路径
增加自动启动守护机制
健康检查 + 异常重启
请求重试与缓存优化
🟡 v1.0.0
初始版本发布
基础 CosyVoice2 语音合成功能
⚙️ 安装方式
1️⃣ 下载插件

在alife插件市场中下载 或 前往 Release 下载

手动下载：

CosyVoice2 v1.0.1 - Alife.Function.Speech.CosyVoiceTTS.zip


2️⃣ 放入插件目录

解压到：Plugins文件夹下，文件夹名为Alife.Function.Speech.CosyVoiceTTS
3️⃣ 配置服务（可选）

在 UI 或配置文件中设置：

Python 路径
模型路径
服务启动目录

🚀 快速开始
启动 Alife 桌宠系统
打开语音插件配置界面
设置 CosyVoice2 服务路径
点击启动桌宠
自动启动服务，开始语音合成

🧠 架构说明
Alife → 插件层 → CosyVoice2 API服务 → 语音输出

🔧 配置说明
配置项	说明
python_path	Python执行路径
model_path	模型目录
service_path	服务启动目录
auto_restart	是否自动重启
retry_count	请求失败重试次数

⚠️ 注意事项
首次使用需确保 CosyVoice2 服务可正常启动
建议使用 GPU 环境（提升语音生成速度）

📌 依赖
Alife 桌宠框架
CosyVoice2 API 服务

💡 常见问题
❓ 无法启动服务？

检查：

Python 路径是否正确
端口是否被占用

❓ 没有声音输出？

检查：

模型是否加载成功
API 服务是否运行
