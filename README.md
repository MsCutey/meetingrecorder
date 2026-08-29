# MeetingRecorder

MeetingRecorder 是面向 Windows 10/11 x64 的本地会议录屏软件。它不接入腾讯会议或其他会议平台接口，也不会上传录制内容。

## 功能

- **视频 + 声音**：选择显示器或窗口，录制 30 fps H.264/AAC MP4。
- **仅声音**：收录系统声音、麦克风或二者混音，实时生成只有 AAC 音轨的 M4A。
- 系统声音与麦克风独立选择、独立音量表。
- 1080p、720p、源分辨率三种画质；保持宽高比并自动补黑边。
- 3 秒默认倒计时、托盘控制、可配置全局热键。
- 暂停时间不进入成品；窗口最小化或关闭后继续录音并明确警告。
- 低于 5 GB 警告，低于 512 MB 自动安全停止。
- `.partial` 写入、原子 JSON、异常会话扫描与恢复。
- 安装模式把设置放在 `%LOCALAPPDATA%\MeetingRecorder`；存在 `portable.marker` 时改用程序旁的 `UserData`。

> 录制会议前，请取得所有必要授权并遵守适用法律、单位制度和会议平台规则。本软件不绕过 DRM、安全桌面或平台限制。

## 开发构建

需要 .NET 10 SDK，目标平台必须为 x64：

```powershell
dotnet restore MeetingRecorder.sln -p:Platform=x64 --configfile NuGet.config
dotnet build MeetingRecorder.sln -p:Platform=x64 --no-restore
dotnet test tests/MeetingRecorder.Tests/MeetingRecorder.Tests.csproj -p:Platform=x64 --no-restore
```

启动开发版：

```powershell
dotnet run --project src/MeetingRecorder.App/MeetingRecorder.App.csproj -p:Platform=x64
```

## 发布

`scripts/build-release.ps1` 会运行测试、生成自包含 `win-x64` 发布目录、便携 ZIP、NSIS 安装包（已安装 NSIS 时）以及 `SHA256SUMS.txt`：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1
```

若需要完整离线交付，请先把 Microsoft 官方 `vc_redist.x64.exe` 放到 `installer/prerequisites`。发布脚本会把它复制进便携版并嵌入安装包。

## 关键实现

- 捕获与音频混合：ScreenRecorderLib 7.0.0（Windows Graphics Capture、WASAPI、Media Foundation）。
- 仅音频编码：NAudio.Wasapi 3.0.1，通过有界 PCM 队列实时写入 AAC/M4A。
- 音频模式同时生成 64×64、1 fps 的临时恢复 MP4；AAC 编码失败时保留该文件。
- 视频优先尝试硬件 H.264，初始化失败自动回退软件编码。

详细验收步骤见 [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md)。

## 首版边界

不包含区域录制、摄像头叠加、标注、降噪、定时录制、云同步、自动更新和代码签名。未签名安装包可能触发 Windows SmartScreen“未知发布者”提示。
