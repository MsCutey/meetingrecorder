# MeetingRecorder v1 验收清单

## 自动化检查

```powershell
dotnet test tests/MeetingRecorder.Tests/MeetingRecorder.Tests.csproj -c Release -p:Platform=x64
```

测试覆盖状态机、暂停时长扣除、文件命名、原子设置持久化、热键冲突、有界 PCM 队列、磁盘阈值和异常恢复源选择。

在装有真实显示器与回放设备的 Windows 主机上，可启用短录制集成测试：

```powershell
$env:MEETINGRECORDER_HARDWARE_TESTS = "1"
dotnet test tests/MeetingRecorder.Tests/MeetingRecorder.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~HardwareRecordingIntegrationTests"
```

该测试会实际生成 M4A 与 MP4，并检查非空音频、`avc1` 视频轨标记和 `mp4a` 音轨标记；默认构建中跳过，避免 CI 机器没有采集设备而误报。

## 媒体轨道检查

安装 FFmpeg 后执行：

```powershell
powershell -File scripts/verify-media.ps1 -VideoFile "D:\Recordings\sample.mp4" -AudioFile "D:\Recordings\sample.m4a"
```

脚本要求 MP4 恰有一条 H.264 视频轨和一条 AAC 音轨，M4A 恰有一条 AAC 音轨且无视频轨。

## Windows 集成矩阵

在 Windows 10 22H2 与当前 Windows 11 的干净 x64 虚拟机分别验证安装版与便携版：

1. 显示器、普通窗口、腾讯会议窗口各录制 2 分钟。
2. 在 100%、125%、150%、200% DPI 和多显示器排列下重复窗口选择与预览。
3. 分别测试仅系统声、仅麦克风、二者混音；用人声和短促提示音确认两路音量表。
4. 录制中最小化窗口、关闭窗口、拔插耳机/麦克风、切换默认设备；确认警告出现且时间轴不中断。
5. 占用热键后启动应用；确认软件明确报告冲突且允许修改。
6. 用受控测试卷把可用空间降至 5 GB 和 512 MB 阈值附近；确认警告和安全停止。
7. 模拟异常终止；重启后确认恢复横幅出现，并保留 `.partial` 或恢复 MP4。
8. 禁用硬件编码器后录制，确认软件回退警告与可播放成品。

## 8 小时压力测试

1. 1080p/30 fps，系统声与麦克风同时启用，连续录制 8 小时。
2. 每 30 分钟记录工作集、托管堆、文件大小和磁盘剩余时间。
3. 在第 2、4、6 小时各暂停 60 秒，并各进行一次音频设备拔插。
4. 停止后确认文件可完整拖动播放；内存没有持续无界增长；暂停共 180 秒不计入成品。
5. 对开头和结尾的同步提示音/画面逐帧测量，音画偏差应不超过 150 ms。

上述硬件与长时测试必须在目标机器上完成；自动化单元测试不能替代真实采集设备验收。
