# Windows 1.2.0 beta 验证记录

同步基线：GitHub main `508d7f7`（Mac 1.2.0 beta）。Windows 10/11 x64，.NET 10.0.400 SDK。

## 适用差异

| 功能 | Windows 处理 |
| --- | --- |
| 屏幕/窗口 MP4、音频 M4A、暂停、麦克风开关、历史和恢复 | 保留原生 Windows 路径并回归测试 |
| 输出比例和尺寸 | 最高 1080/720 像素高，不放大，偶数尺寸；降级使用实际裁剪/显示器尺寸 |
| 自动睡眠 | Worker 持有系统电源请求，涵盖暂停和保存，关闭句柄后释放 |
| Mac TCC / 台前调度 | 不适用；Windows 保留窗口最小化提示和显示器降级 |
| 窗口模式系统音频 | Windows 采集所选输出端点的系统声，不限定为窗口所属应用 |
| 混音、预览、防捕获 | 保留 Windows 既有增益、实时预览和默认关闭的实验性防捕获 |
| 设置迁移 | 保留 schema 4，兼容旧版设置与历史 |

Worker 与主程序均声明 PerMonitorV2，避免 200% 缩放下设备枚举及窗口裁剪使用逻辑像素，造成来源尺寸减半。

电源请求遵循 [Microsoft PowerCreateRequest](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-powercreaterequest) 的句柄生命周期；[系统策略](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-powersetrequest) 仍可能允许主动睡眠及电池供电下 Modern Standby。没有修改系统电源策略。

## 验证结果

验证日期：2026-10-26。本机 Windows x64，3072×1920 显示器、Realtek 输出端点及内置麦克风阵列。

- 最终完整回归 96 项通过，0 失败、0 跳过，Release 构建无警告或错误。
- 真实设备连续启停：4 种视频声音组合和 3 种纯音频组合各 20 次，共 140 次通过；最终修改后再按每种 3 次复核，共 21 次通过。
- 实录覆盖纯音频、整屏、双路声音、暂停续录、默认麦克风静音和暂停期间切换麦克风。双路 10 秒录制的时长与预览响应检查通过。
- 440 Hz 测试音经 Windows Media Foundation 解码确认进入 M4A 和 MP4；约 3 秒有效内容加 1.5 秒暂停，解码成品约 3.0 / 3.5 秒，暂停未进入成品。
- 默认静音的纯音频和视频成品解码峰值均为 0；窗口模式在暂停中开关麦克风、恢复后仍静音，解码峰值为 0。
- 视频成品通过 Windows MediaComposition 解码取帧，检查偶数尺寸、最高 720 像素高以及非纯色内容。整屏成品为 1152×720（16:10），测试窗口成品为 1044×720。
- 实测发现窗口原生路径可生成全黑帧。已增加启动图像检查，无法取得有效图像时降级为显示器区域；降级后窗口成品解码检查通过。持续全黑的合法画面也可能触发该降级，界面会提示可能包含同屏其他内容。
- 启动窗口图像检查最多临时使用约 2 秒低分辨率帧；预览关闭时不发送给主界面，验证完成后关闭原生预览回调。
- 主界面、录制须知、设置窗口、设备列表与画质文案已完成本机界面检查。

测试报告位于本地 `tests/MeetingRecorder.Tests/TestResults/windows-final.trx`；140 次矩阵记录在 `windows-full.trx`。最终发布 Worker 的复核记录使用 `windows-release-final.trx`。试录媒体由测试自动删除，诊断不进入源码或发布包。

## 复现

普通单元测试按 README 命令执行。实机测试需保持一个受控测试窗口可见，并设置：

```powershell
$env:MEETINGRECORDER_HARDWARE_TESTS = '1'
$env:MEETINGRECORDER_HARDWARE_STRESS = '1'
$env:MEETINGRECORDER_STRESS_CYCLES = '20'
$env:MEETINGRECORDER_TEST_WINDOW = 'MeetingRecorder · 会议录屏'
dotnet test tests/MeetingRecorder.Tests/MeetingRecorder.Tests.csproj -c Release -p:Platform=x64
```

可用 `MEETINGRECORDER_TEST_WORKER` 指定最终安装包中的 Worker，使用 `HardwareMediaContentTests` 过滤器检查最终二进制；`MEETINGRECORDER_TEST_NO_PREVIEW=1` 可验证窗口录制不向主界面发送预览。内容测试会播放低音量测试音和使用真实设备；不要在真实会议中运行。

## 验证边界

本次为 beta 本机验收。尚未覆盖干净 Windows 10 虚拟机、Windows N/KN、8 小时持续录制、设备热插拔、真实磁盘耗尽和各驱动的锁屏/休眠行为。异常 Worker 隔离、恢复入口和磁盘阈值有自动测试，但不代表所有异常媒体均可恢复。安装器未做商业代码签名。
