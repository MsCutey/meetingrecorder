# MeetingRecorder

现包含 Windows 版（V1.1.3）与新增的原生 macOS 测试版（1.2.0 beta）。

## macOS 版

适用于 macOS 15 及以上，使用 SwiftUI、ScreenCaptureKit 和 AVFoundation，无需安装 .NET 或虚拟声卡。支持屏幕/窗口 MP4、仅声音 M4A、系统声音与麦克风混音、暂停续录、菜单栏控制和临时文件恢复。

安装、权限、构建和功能差异见 [macOS 使用与开发说明](macos/README.md)。预编译包见 [Releases](https://github.com/MsCutey/meetingrecorder/releases)。

当前功能分支增加 macOS **会后纪要实验版**：从录制历史或本地音视频生成转写，再整理概要、决策和待办。转写与总结分别配置云端 API，包含 DeepSeek 总结预设；只有主动确认处理才发送音轨/文本。支持转写断点继续、纪要编辑、Markdown 导出，以及复制转写到 ChatGPT 手动使用订阅。调研、方案比较及正式 ChatGPT 订阅登录的可行性见 [AI 会议纪要调研](docs/AI-MEETING-NOTES.md)。此分支尚待验收合并，不代表已发布版本。

```bash
bash scripts/build-macos.sh
open artifacts/macos-release-arm64/MeetingRecorder.app
```

## Windows V1.1.3

MeetingRecorder 是面向 Windows 10/11 x64 的本地会议录屏软件。它不接入会议平台接口，也不会上传录制内容。

## 功能

- **视频 + 声音**：选择显示器或窗口，录制 30 fps H.264/AAC MP4。
- **仅声音**：直接采集系统声音、麦克风或二者混音，生成只有 AAC 音轨的 M4A。
- 仅录系统声音时自动提升过小的信号（最高 +24 dB，带噪声门与削波保护）；视频模式的仅系统声路径使用保守的 +6 dB 增益，不修改 Windows 音量设置。
- 双路混音不再无条件把系统声和麦克风各衰减 6 dB；只有两路相加将要削波时才按音频块等比限幅。
- 仅音频主路径同步写入低码率 MP3 恢复文件；成功后自动删除，异常退出时保留。若驱动拒绝 NAudio 初始化，则在同一隔离 Worker 内使用 6.6.0 兼容路径，并保留可恢复的分段 M4A。
- 系统声音与麦克风独立选择、独立音量表，设备设置保存稳定的 Windows endpoint ID。
- 1080p、720p、源分辨率三种画质；3 秒默认倒计时、暂停、托盘和全局热键。
- 点击“开始录制”不会最小化、隐藏、改变窗口大小或抢夺焦点；只有用户主动最小化或关闭时才进入托盘。
- 低分辨率实时预览默认开启，可在“设置”中关闭；整屏录制且未启用防捕获时，预览画面可能被录进视频。“隐藏 MeetingRecorder”防捕获功能作为实验性设置保留，默认关闭且只在录制期间启用。
- 低于 5 GB 警告，低于 512 MB 自动安全停止；暂停时间不进入成品。
- `.partial` 写入、原子 JSON、异常会话扫描和恢复入口。

> 录制会议前，请取得所有必要授权并遵守适用法律、单位制度和会议平台规则。本软件不绕过 DRM、安全桌面或平台限制。

## V1.1.3 窗口录制修复与麦克风开关

- 修复窗口录制（尤其浏览器窗口）点击开始即报“录制异常”的问题：引擎启动时验证视频帧产出，窗口捕获失败后自动降级为“窗口所在显示器区域裁剪”，再不行才录制整个显示器，并在界面给出黄色警告；启动前窗口已最小化会直接提示先恢复窗口。
- 修正错误分类：窗口捕获崩溃不再被误报为“硬件编码器初始化失败”，也不再触发整 Worker 的软件编码重试。
- 录制中可随时开关麦克风：主界面底部新增麦克风按钮，全局热键默认 `Ctrl+Shift+F11`（可在“保存与控制”中修改），托盘菜单同样支持；视频与仅声音两种模式都生效，暂停中也可切换；静音期间麦克风电平表归零。
- 新增设置“录制开始后默认静音麦克风（可随时开启）”：适合会议场景，轮到你讲话时再一键开启。
- 设置文件升级到 schema 4，旧版本设置自动迁移，热键与麦克风偏好全部保留。

## V1.1.2 体验优化

- 开屏授权须知窗口的“我已了解并同意”按钮可直接点击（不再强制勾选确认框），窗口高度自适应，高分辨率缩放下底部按钮不再被裁切。
- 录制历史每行新增“删除”：弹窗可选“同时删除本地文件”与“记住我的选择”；记住后再次删除不再询问，可在“设置”中取消记住并恢复每次询问。
- 异常录制恢复横幅新增关闭按钮，点击后本次会话内不再提示该批候选；新产生的异常录制仍会重新提醒，下次启动重新扫描。
- 新增“设置”窗口（标题栏“⚙ 设置”），收纳低分辨率预览开关与删除记忆选项，即改即存。
- 预览开关默认开启：整屏录制不再强制关闭预览；录制中关闭开关立即停止显示预览，下一次录制起 Worker 不再发送预览帧。

## V1.1.1 软件图标

- 使用 MeetingRecorder 麦克风录制图标替代系统默认图标，并提供 16–256 像素的九种 Windows 图标尺寸。
- 主窗口、授权窗口、页面品牌区、任务栏和系统托盘使用同一图标。
- 主程序、安装器、卸载器、开始菜单、桌面快捷方式及“已安装的应用”列表均显示产品图标。
- 图标读取失败时托盘安全回退到系统图标，不影响主界面启动或录制功能。

## V1.1.0 稳定性架构

主程序只包含界面、设置、历史与恢复逻辑，不引用或加载 ScreenRecorderLib。设备发现和每次录制都在全新的 `MeetingRecorder.Worker.exe` 中运行，并通过仅限当前用户的版本化命名管道通信：

- 设备发现 Worker 最长运行 15 秒，按显示器、窗口、输出端点、输入端点的顺序枚举。
- 录制 Worker 启动上限 20 秒，安全收尾上限 2 分钟；原生访问冲突或堆损坏只会结束 Worker，主界面仍可继续使用。
- 预览开启时，预览只生成 320×180 的输出画面并限制为每秒最多 2 帧，再进入容量为 1 的有界队列；预览开关关闭时不创建预览回调与队列。音频采集和两路编码队列也都有固定上限。
- 视频使用 ScreenRecorderLib 6.6.0 的成熟双设备接口；关闭其高开销的逐样本强制单声道复制，双路录制启用低延迟编码，音量表直接读取 Windows 音频端点峰值。
- 仅音频优先使用 NAudio 2.2.1，统一转换为 48 kHz、16-bit、双声道后混音，并同时编码 M4A 与恢复 MP3；端点初始化失败时切换到隔离的 6.6.0 音频路径，停止后重新封装为仅含 AAC 的 M4A。
- 硬件 H.264 初始化失败时，主程序只在第二个全新 Worker 中重试一次软件编码。

日志位于 `%LOCALAPPDATA%\MeetingRecorder\Logs`；便携模式位于程序旁 `UserData\Logs`。日志记录版本、系统、设备类型、配置、状态与退出码，不记录音视频内容。

## 开发构建

需要 .NET 10.0.400 SDK，目标平台必须为 x64：

```powershell
dotnet restore MeetingRecorder.sln -p:Platform=x64 --configfile NuGet.config
dotnet build MeetingRecorder.sln -p:Platform=x64 --no-restore
dotnet test tests/MeetingRecorder.Tests/MeetingRecorder.Tests.csproj -p:Platform=x64 --no-restore
```

启动开发版前请先构建整个解决方案，使 Worker 可供主程序启动：

```powershell
dotnet run --project src/MeetingRecorder.App/MeetingRecorder.App.csproj -p:Platform=x64
```

## 发布

正式发布必须把微软官方且签名有效的 `vc_redist.x64.exe` 放在 `installer/prerequisites`，并提供 NSIS 3 的 `makensis`。VC++ 文件缺失、签名无效或未找到 NSIS 时，正式构建都会直接失败；只生成便携包时可显式传入 `-SkipInstaller`：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1
```

脚本运行测试，并生成自包含主程序、自包含 Worker、便携 ZIP、离线安装包及 `SHA256SUMS.txt`。安装程序会先安装 VC++ x64 运行库；失败时终止安装，不会静默继续。

便携版已内置 .NET 运行时。首次在未安装 VC++ 运行库的电脑上使用时，请先运行压缩包内
`prerequisites\vc_redist.x64.exe`，安装成功后再启动 `MeetingRecorder.exe`。

详细验证步骤见 [V1.1.3 验收清单](docs/ACCEPTANCE.md)。

## 当前边界

不包含区域录制、摄像头叠加、标注、降噪、定时录制、云同步、自动更新和代码签名。未签名安装包可能触发 Windows SmartScreen“未知发布者”提示。
