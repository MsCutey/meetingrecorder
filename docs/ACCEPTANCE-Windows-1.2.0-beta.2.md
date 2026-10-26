# Windows 1.2.0 beta.2 窗口录制修复

基线：GitHub main `0f9aa2a`，公开包 `v1.2.0-windows-beta.1`。此修复包仍为测试版。

## 问题与修复

1. Windows 1.1.3 的启动路径已有 `IsIconic` 直接报错检查。现在先通过 `ShowWindowAsync(SW_SHOWNOACTIVATE)` 恢复目标窗口，等待可用且不抢焦点；窗口已关闭或三秒内无法恢复时明确报错。
2. 原生窗口捕获不可用时，6.6.0 的输出层裁剪在本机没有达到预期：选择窗口而成品包含整屏，画布尺寸仍按窗口设置。现将裁剪放到捕获来源层，输出层只负责等比缩放。
3. `GetWindowRect` 包含不可见边框。改用 DWM 可见边框物理像素作为启动时的尺寸和裁剪依据；不再把覆盖显示器 85% 以上的窗口直接替换成整屏。

## 实机验证

2026-10-26，Windows 11 x64，3072×1920、200% 缩放。测试创建自己的 WPF 校准窗口，绘制红色正方形与蓝色圆形；通过生产 Worker 录制，独立 FFmpeg 解码 MP4 像素。原始诊断图与录像不进入仓库或安装包。

- 旧包源分辨率横窗输出 1200×840、方块约 78×78，包含桌面；修复后可见来源 1178×829，偶数编码 1178×828，方块约 199×200（期望 200×200）。竖窗 818×1189 输出 818×1188，方块同样约 199×200。
- 测试同时断言方块比例、绝对像素大小和输出尺寸，避免“正方形没变形但整个桌面被缩进来”也通过。
- 横竖源分辨率、竖窗 720p/1080p、关闭预览，以及每组的暂停续录通过。来源选择后再改变窗口尺寸，仍按开始时的实际尺寸录制。
- 最小化窗口自动恢复，录制前后的前台窗口句柄一致，恢复后的预览校准方块比例正确。

开发 Worker 报告：`tests/MeetingRecorder.Tests/TestResults/geometry-regression.trx`。

最终安装版复核：`windows-beta2-installed.trx`，98 项通过、0 失败、0 跳过；包含 7 种音视频组合各一次启停、实际音轨解码和窗口暂停中麦克风切换。初次整体验证的旧窗口测试依赖外部标题而未匹配，现改成自建校准窗口，完整重跑通过。

安装器返回 0；686 个安装文件与发布目录逐一 SHA-256 一致，升级前后历史 JSON 哈希不变。便携 ZIP 内 688 个文件逐一读取并与发布目录校验一致。原设置与历史已备份，既有录像未清理。

复现：

```powershell
$env:MEETINGRECORDER_HARDWARE_TESTS = '1'
$env:MEETINGRECORDER_TEST_WORKER = '<待验证的 MeetingRecorder.Worker.exe 完整路径>'
$env:MEETINGRECORDER_TEST_FFMPEG = '<可信 ffmpeg.exe 完整路径>'
dotnet test tests/MeetingRecorder.Tests/MeetingRecorder.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~HardwareWindowGeometryTests
```

## 边界

此机器的原生窗口路径会降级为显示器区域，因此实机像素验证主要覆盖该路径。区域降级不会跟随录制中移动或缩放的窗口，也无法排除遮挡物；界面已明确提示。Windows 10、其他显卡驱动、跨显示器与长时间录制仍需额外验收。
