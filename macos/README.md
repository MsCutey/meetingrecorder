# MeetingRecorder Pure for macOS 1.2.0 beta

纯净版只提供本地录屏、录音与历史文件管理，不包含会议纪要、语音转写、AI 总结、账号登录、API Key 或上传模块。最低 macOS 15，原生 SwiftUI / ScreenCaptureKit / AVFoundation，无需 .NET、FFmpeg、语音模型或额外第三方依赖。

## 独立安装

- 打开 DMG，将 `MeetingRecorder Pure.app` 拖入镜像中的 `Applications` 文件夹。安装完成后推出镜像，从“应用程序”启动。应用标识为 `com.mscutey.meetingrecorder.pure`，可与完整版同时安装，不覆盖完整版。
- 纯净版使用自己的设置，默认录制目录是 `~/Movies/MeetingRecorder Pure`，不迁移或清除完整版的录制、设置、纪要或凭据。
- 系统屏幕与音频权限按应用分别授予。录窗口可点击“选择窗口并开始录制”，在系统选择器中确认目标；整屏及系统声音按需要完成系统授权。麦克风另行授权。
- 录制前请取得必要授权。文件保存在本机，软件不提供发送到云端的功能。

## 保留的功能

- 显示器/窗口 MP4（H.264 / AAC）及纯音频 M4A；1080p、720p或源分辨率，保留比例，上限30 fps。
- 系统声音、可选麦克风、输入设备选择、录制中静音；双路声音混成一条音轨。
- 暂停/继续，暂停时间从成品扣除；菜单栏控制、倒计时、快捷键和磁盘空间检查。
- 录制历史播放、定位、删除；未完成录制尝试恢复。关闭恢复提示后保持隐藏，没有替代链接或计数，原文件保留。
- 流启动且首个有效媒体写入后才显示录制中；10秒无画面则退出准备。准备可取消，已写入媒体保留。
- 录屏时默认自动临时关闭台前调度，停止、取消、失败或正常退出后恢复原状态；原先关闭则保持关闭。仅声音模式不切换。
- 强制退出或崩溃后，下次启动恢复台前调度；恢复失败保留记录并提供重试。

仅关闭主窗口时，菜单栏应用仍可继续运行；退出时会先停止并保存。应用内快捷键：⇧⌘R开始/停止、⇧⌘P暂停/继续、⇧⌘M切换麦克风静音。

## 已知范围

- 窗口被最小化时系统仍可能暂停画面。本机macOS27的台前调度缩略窗口不能持续提供完整画面，因此使用自动临时关闭的兼容方式。
- 台前调度开关使用未公开的系统设置键，其他macOS版本需要实测，可关闭自动选项改为手动操作。两种版本共用这一个系统开关的恢复锁，避免互相恢复正在录制时的状态。
- 测试包采用ad-hoc签名，尚无Developer ID签名和公证。若系统阻止打开，请核对来源后通过系统设置的“仍要打开”处理，不需要关闭系统安全功能。
- Intel包的云端构建不能代替实机测试。外接麦克风、长时录制和最低系统版本的实机覆盖情况见[验收记录](../docs/ACCEPTANCE-macOS.md)。

## 构建与验证

```bash
bash scripts/build-macos.sh
open "artifacts/macos-pure-release-arm64/MeetingRecorder Pure.app"
```

默认产物为 `MeetingRecorder-Pure-1.2.0-macOS-arm64.dmg` 与 `SHA256SUMS-macOS-Pure-arm64.txt`。镜像内包含应用、Applications快捷入口和安装说明。Intel使用 `ARCH=x86_64 bash scripts/build-macos.sh`。GitHub Actions独立命名为 `MeetingRecorder-Pure-macOS-<架构>`。

显式开发试录（普通启动不会自动录制）：

```bash
"artifacts/macos-pure-release-arm64/MeetingRecorder Pure.app/Contents/MacOS/MeetingRecorderPure" \
  --smoke-test /tmp/meetingrecorder-pure-test --window --window-title '测试窗口标题' --no-system-audio --pause
```

另支持 `--audio-only`、`--expect-sound`、`--with-microphone`。程序恢复测试前设置，输出媒体、`smoke-result.json` 和解码图片；必须检查报告中的 `success` 和实际媒体。
