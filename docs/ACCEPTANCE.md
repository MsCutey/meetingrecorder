# MeetingRecorder V1.1.2 验收清单

## 自动化检查

```powershell
dotnet test tests/MeetingRecorder.Tests/MeetingRecorder.Tests.csproj -c Release -p:Platform=x64
```

自动化测试覆盖：

- 普通、最大化、最小化与隐藏状态下启动录制时窗口状态保持不变；只有用户主动最小化/关闭才进入托盘。
- IPC 长度分帧、二进制预览帧、协议版本拒绝和当前用户管道握手。
- 假 Worker 的访问冲突 `0xC0000005`、堆损坏 `0xC0000374`、启动无响应、收尾无响应、启动前检查失败和硬件编码软件回退。
- 设置 schema 1 → 2 → 3 迁移，旧随机音频 ID 清空并由界面写回默认 endpoint ID；新增字段（预览开关、删除记忆）升级后取默认值。
- 防捕获设置默认关闭、旧 schema 2 测试设置安全回退、倒计时不触发防捕获、预览开关默认开启并决定 Worker 预览帧发送，以及录制失败或 Worker 异常退出后的窗口状态恢复。
- 48 kHz PCM16 双声道混音、无条件 6 dB 衰减回归、块级限幅、系统声自动增益的最大增益/噪声门/突发大音量保护、缺失输入补静音、暂停时长扣除和编码缓冲上限。
- 双路视频关闭逐样本强制单声道复制、启用低延迟但保持节流保护；仅系统声视频采用保守增益。
- 开发构建按调用方的 Release/Debug 配置选择对应 Worker，避免误启动旧引擎。
- 音频异常恢复优先选择 `.recovery.partial.mp3`；兼容音频路径保留分段 `.partial.m4a`，并兼容 V1.0.0 的恢复 MP4。
- 应用 ICO 包含 16、20、24、32、40、48、64、128、256 像素九种尺寸；主程序和安装器应能提取出相同产品图标。

## 当前电脑真实设备冒烟

```powershell
$env:MEETINGRECORDER_HARDWARE_TESTS = "1"
dotnet test tests/MeetingRecorder.Tests/MeetingRecorder.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~HardwareRecordingIntegrationTests"
```

该测试实际创建 M4A 与 MP4，检查文件非空、`avc1` 视频轨标记和 `mp4a` 音轨标记，并通过测试显示协调器显式开启预览以验证预览链路。双路用例还比较成品时长与墙钟时间，防止声音混合拖慢视频；可通过 `MEETINGRECORDER_DUAL_TEST_SECONDS` 设置 3–120 秒，并用 `MEETINGRECORDER_DUAL_SYSTEM_DEVICE`、`MEETINGRECORDER_DUAL_MICROPHONE_DEVICE` 指定稳定端点 ID。默认构建跳过，以免无采集设备的 CI 误报。

连续启停矩阵默认对每个可用组合运行 20 次；首轮包含暂停/恢复。仅做快速检查时可把次数改为 1 或 2：

```powershell
$env:MEETINGRECORDER_HARDWARE_STRESS = "1"
$env:MEETINGRECORDER_STRESS_CYCLES = "20"
dotnet test tests/MeetingRecorder.Tests/MeetingRecorder.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~HardwareStartStopStressTests"
```

多设备电脑可用 `MEETINGRECORDER_STRESS_MICROPHONE_DEVICE` 与
`MEETINGRECORDER_STRESS_SYSTEM_DEVICE` 按端点名称或稳定 ID 选择设备；还可用
`MEETINGRECORDER_STRESS_MODE`、`MEETINGRECORDER_STRESS_COMBINATION` 缩小验证范围。

安装 FFmpeg 后可进一步验证轨道：

```powershell
powershell -File scripts/verify-media.ps1 -VideoFile "D:\Recordings\sample.mp4" -AudioFile "D:\Recordings\sample.m4a"
```

## Windows 设备与窗口矩阵

在当前故障电脑上，视频模式验证无声音、仅系统声、仅麦克风、双路混音；仅音频模式验证仅系统声、仅麦克风和双路混音（仅音频模式沿用 V1.0.0 规则，至少启用一种声音来源）。每组连续启停 20 次，并至少暂停/恢复一次：

1. 安装后首次启动，确认主页面完成绘制、任务栏缩略图正常且日志包含 `main-window-rendered`；启动阶段不得出现防捕获启用日志。
2. 普通窗口点击录制，确认页面、大小、位置、状态和焦点不变。
3. 最大化窗口点击录制，确认保持最大化。
4. 预览开关默认开启时，整屏录制预览显示低分辨率画面；软件窗口可能进入成品（预期行为，设置中有递归风险提示）。
5. 手动启用实验性防捕获，确认首次风险提示；停止后任务栏缩略图和窗口显示恢复正常。
6. 用户手动最小化或关闭，确认进入托盘；托盘和热键启动不弹窗、不抢焦点。
7. 每次停止后确认主界面仍存活、历史记录正确、没有遗留 Worker。
8. 强制结束 Worker，确认主界面进入“录制失败”，显示退出码与恢复入口，防捕获已撤销并可继续刷新设备。
9. 确认主窗口、任务栏、托盘、安装器、开始菜单、桌面快捷方式和“已安装的应用”均显示 MeetingRecorder 产品图标。

10. 首次启动授权须知窗口的“我已了解并同意”可直接点击；在 150% 缩放下确认窗口高度自适应、底部按钮不被裁切。
11. 打开“⚙ 设置”关闭预览开关，确认正在进行的预览立即消失且占位文案变为“实时预览已关闭 · 可在设置中开启”；再次开始录制确认不再显示预览帧。
12. 删除历史记录：不勾选“同时删除本地文件”时记录移除、文件保留；勾选时文件一并删除；勾选“记住我的选择”后再次删除不再弹窗；在设置中取消“记住我的删除选择”，确认下次删除重新弹窗。
13. 存在可恢复候选时点击恢复横幅的“✕”，确认本次会话内不再提醒该批候选；新产生一次异常录制后，确认横幅重新显示。

声音组合需要用不同的人声/提示音人工确认双路均可辨认；自动化只能验证轨道和文件结构。

## 干净系统矩阵

在 Windows 10 22H2 与当前稳定版 Windows 11 x64 干净虚拟机分别验证安装版和便携版。虚拟机预先不安装 .NET 或 VC++：

1. 安装版能安装嵌入的 VC++ x64 运行库，自包含主程序和 Worker 均正常启动。
2. 便携版包含 `prerequisites\vc_redist.x64.exe`，按说明安装后可运行。
3. 缺少 Media Foundation 的 Windows N/KN 环境显示明确提示，主界面不退出。
4. MP4 含 H.264 视频和 AAC 音轨；M4A 只有 AAC 音轨。
5. 暂停时间不进入成品；使用同步提示音/画面测量音画偏差不超过 150 ms。
6. 从故障内部 V1.1.0 覆盖安装和全新安装两种路径启动时，主页面及任务栏缩略图均正常显示。

## 8 小时压力与恢复

1. 1080p/30 fps、系统声与麦克风同时启用，连续录制 8 小时。
2. 每 30 分钟记录主程序与 Worker 工作集、文件大小和磁盘剩余时间；内存不得持续无界增长。
3. 第 2、4、6 小时各暂停 60 秒；成品时长应扣除 180 秒。
4. 测试中强制结束一次 Worker，确认主界面不退出且 `.partial.mp4` 或 `.recovery.partial.mp3` 可找回。
5. 正常压力测试停止后，文件可完整拖动播放，开头和结尾音画同步误差均不超过 150 ms。

干净虚拟机、声音可辨认、同步误差与 8 小时测试必须在目标环境人工完成；单元测试不能替代这些验收。
