# MeetingRecorder for macOS 1.2.0 beta

原生 SwiftUI / ScreenCaptureKit 实现，最低 macOS 15。无需 .NET、FFmpeg、虚拟声卡或额外依赖。Apple Silicon 版在本机调试；Intel 构建的验证范围以发布说明为准。

## 安装与权限

1. 解压 ZIP，将 `MeetingRecorder.app` 拖入“应用程序”。
2. 打开应用，点击“授权并刷新来源”。在“系统设置 → 隐私与安全性 → 屏幕与系统音频录制”中允许 MeetingRecorder。系统要求时退出并重新打开应用。
3. 启用麦克风时另行授权；Mac mini 没有内置麦克风，请接入外置设备。
4. 选择来源、保存目录和声音选项，开始录制。录制前请取得必要授权。

当前测试包采用 ad-hoc 签名，没有 Apple Developer ID 签名或公证。下载后 macOS Gatekeeper 可能阻止首次打开；确认来源后，可在“系统设置 → 隐私与安全性”中使用“仍要打开”。不要关闭系统安全功能。更新应用后，系统可能要求重新授权屏幕访问。

开发中反复覆盖同一 `.app` 会改变 ad-hoc 签名，系统可能继续按旧签名拒绝访问，即使权限开关已打开。请先固定最终应用，再完成授权，不要在已授权的副本上继续编译覆盖；必要时只重置本应用的旧记录：`tccutil reset ScreenCapture com.mscutey.meetingrecorder`，然后重新打开最终应用并授权。该操作不绕过授权。具体已验证与待验证范围见 [验收记录](../docs/ACCEPTANCE-macOS.md)。

权限列表中，上方“录屏与系统录音”和下方“仅系统录音”是独立入口，录屏需要开启上方入口。使用台前调度录窗口时，请让目标窗口保持在当前台前组；被缩成缩略图或最小化时，系统可能挂起画面，且初始缩略尺寸可能降低录制分辨率。无法保持目标窗口活动时，建议使用整屏模式。

## 功能与范围

- 显示器或窗口录制，H.264 视频、AAC 声音，导出 MP4；1080p / 720p / 源分辨率，上限 30 fps，保留比例。
- 仅声音输出 M4A；不添加视频采样回调，不读取或保存视频帧。ScreenCaptureKit 的系统声音路径仍需要屏幕与系统音频权限。
- 系统声音、可选麦克风、输入设备选择和录制中静音。整屏模式采集系统应用声音；窗口模式限于所属应用。双路声音各以 0.5 增益混合为一条音轨，防止直接叠加削波。
- 暂停 / 继续，暂停时间从所有音视频时间戳中统一扣除。菜单栏后台控制、应用内快捷键、倒计时和本地历史。
- 默认排除本应用窗口；不会自动最小化。保存前使用 `.partial.mov` 碎片化临时文件，导出成功后清理；异常文件可尝试恢复。无法保证恢复最后一个未完成片段。
- 低于 512 MB 自动停止。录制时阻止系统自动睡眠；退出时先保存。导出混音需要额外磁盘空间和时间，临时文件在保存成功前一直保留。

这一版没有移植 Windows Worker 隔离、实时预览、音量表、自动增益、MP3 冗余恢复或可自定义全局热键。应用内快捷键：⇧⌘R 开始/停止、⇧⌘P 暂停/继续、⇧⌘M 麦克风。后台请使用菜单栏控制。系统切换音频输出、拔出设备、锁屏或移除显示器后应检查录制状态。

设置保存在 macOS UserDefaults，默认录制目录为 `~/Movies/MeetingRecorder`，文件内容不会上传。没有网络请求功能。

## 本机构建

需要 Xcode 16 或更新版本的 Command Line Tools（Swift 6、macOS 15 SDK 或更新）。

```bash
bash scripts/build-macos.sh
open artifacts/macos-release-arm64/MeetingRecorder.app
```

构建脚本先运行单元测试，再生成 `.app`、ZIP 和 SHA-256。`ARCH=x86_64 bash scripts/build-macos.sh` 可交叉构建 Intel 包。可通过 `SIGN_IDENTITY` 指定 Developer ID；脚本不自动公证。

## 开发试录

明确传入 `--smoke-test` 才会自动录制。普通启动不会自动录制。

```bash
artifacts/macos-release-arm64/MeetingRecorder.app/Contents/MacOS/MeetingRecorder \
  --smoke-test /tmp/meetingrecorder-test --pause
```

试录约 6 秒有效内容，中间暂停 2 秒。输出媒体、`smoke-result.json` 和视频帧 PNG。可附加 `--audio-only`、`--window main --window-title '测试窗口标题'`、`--with-microphone`、`--expect-sound` 或 `--no-system-audio`。`--expect-sound` 会解码检查非静音音频；报告的 `success` 才是测试结果，不能只看进程退出码。请先完成系统权限授权，播放测试声音后检查音轨内容，完成后自行删除试录文件。测试程序不会绕过权限。独立试音程序应正常启动为单独应用，避免与命令行录制进程共用音频归属而被排除。窗口试录可以用 `open -g -n -W MeetingRecorder.app --args ...` 在后台启动录制器，保持测试窗口活动。
