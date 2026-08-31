using System.Text;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Recording;

namespace MeetingRecorder.Tests;

public sealed class HardwareStartStopStressTests
{
    [HardwareStressFact]
    public async Task AvailableDeviceMatrix_RepeatedlyStartsStopsAndPauses()
    {
        var cycles = int.TryParse(Environment.GetEnvironmentVariable("MEETINGRECORDER_STRESS_CYCLES"), out var configured)
            ? Math.Clamp(configured, 1, 100)
            : 20;
        var root = Path.Combine(Path.GetTempPath(), "MeetingRecorderHardwareStress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "data");
            var repository = new AppRepository(new AppDataPaths(
                data,
                Path.Combine(data, "settings.json"),
                Path.Combine(data, "history.json"),
                Path.Combine(data, "active-session.json"),
                Path.Combine(data, "assets"),
                true));
            await using var service = new WorkerRecordingService(repository);
            var display = (await service.GetCaptureTargetsAsync()).First(target => target.Kind == CaptureTargetKind.Display);
            var systemDevices = await service.GetAudioDevicesAsync(AudioSourceKind.SystemAudio);
            var microphoneDevices = await service.GetAudioDevicesAsync(AudioSourceKind.Microphone);
            var system = SelectDevice(systemDevices, "MEETINGRECORDER_STRESS_SYSTEM_DEVICE");
            var microphone = SelectDevice(microphoneDevices, "MEETINGRECORDER_STRESS_MICROPHONE_DEVICE");

            var combinations = new List<AudioCombination>
            {
                new("无声音", false, false, null, null, VideoOnly: true)
            };
            if (system is not null) combinations.Add(new("仅系统声", true, false, system, null));
            if (microphone is not null) combinations.Add(new("仅麦克风", false, true, null, microphone));
            if (system is not null && microphone is not null) combinations.Add(new("双路混音", true, true, system, microphone));
            var combinationFilter = Environment.GetEnvironmentVariable("MEETINGRECORDER_STRESS_COMBINATION");
            if (!string.IsNullOrWhiteSpace(combinationFilter))
            {
                combinations = combinations.Where(item => item.Name == combinationFilter).ToList();
            }
            var modeFilter = Environment.GetEnvironmentVariable("MEETINGRECORDER_STRESS_MODE");

            foreach (var combination in combinations)
            {
                if (!string.Equals(modeFilter, nameof(RecordingMode.AudioOnly), StringComparison.OrdinalIgnoreCase))
                {
                    await RunCombinationAsync(service, root, display, RecordingMode.VideoAndAudio, combination, cycles);
                }
                if (!combination.VideoOnly &&
                    !string.Equals(modeFilter, nameof(RecordingMode.VideoAndAudio), StringComparison.OrdinalIgnoreCase))
                {
                    await RunCombinationAsync(service, root, display, RecordingMode.AudioOnly, combination, cycles);
                }
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task RunCombinationAsync(
        WorkerRecordingService service,
        string outputDirectory,
        CaptureTargetInfo display,
        RecordingMode mode,
        AudioCombination combination,
        int cycles)
    {
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            try
            {
                await service.StartAsync(new RecordingRequest
                {
                    Mode = mode,
                    CaptureTarget = mode == RecordingMode.VideoAndAudio ? display : null,
                    CaptureSystemAudio = combination.CaptureSystem,
                    CaptureMicrophone = combination.CaptureMicrophone,
                    SystemAudioDevice = combination.SystemDevice,
                    MicrophoneDevice = combination.MicrophoneDevice,
                    Quality = VideoQualityPreset.Hd,
                    OutputDirectory = outputDirectory,
                    CountdownSeconds = 0,
                    PreferHardwareEncoding = mode == RecordingMode.VideoAndAudio
                });
                if (cycle == 0)
                {
                    await Task.Delay(200);
                    await service.PauseAsync();
                    await Task.Delay(250);
                    await service.ResumeAsync();
                    await Task.Delay(200);
                }
                else
                {
                    await Task.Delay(300);
                }
                await service.StopAsync();
            }
            catch (Exception error)
            {
                var detail = error is WorkerReportedException reported ? reported.Error.Detail : null;
                throw new InvalidOperationException(
                    $"{mode}/{combination.Name} 第 {cycle + 1} 次失败。{Environment.NewLine}{detail}",
                    error);
            }

            Assert.Equal(RecordingState.Idle, service.State);
            var output = service.CurrentOutputPath ?? throw new Xunit.Sdk.XunitException("录制完成后没有输出路径。");
            Assert.True(File.Exists(output), $"{mode}/{combination.Name} 第 {cycle + 1} 次未生成文件。");
            Assert.True(new FileInfo(output).Length > 512, $"{mode}/{combination.Name} 第 {cycle + 1} 次文件为空。");
            if (mode == RecordingMode.AudioOnly)
            {
                var container = Encoding.ASCII.GetString(await File.ReadAllBytesAsync(output));
                Assert.Contains("mp4a", container, StringComparison.Ordinal);
                Assert.DoesNotContain("avc1", container, StringComparison.Ordinal);
            }
            File.Delete(output);
        }
    }

    private static AudioDeviceInfo? SelectDevice(
        IReadOnlyList<AudioDeviceInfo> devices,
        string environmentVariable)
    {
        var filter = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            return devices.FirstOrDefault(device =>
                    device.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    device.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    device.DeviceName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                ?? throw new Xunit.Sdk.XunitException(
                    $"{environmentVariable} 未匹配任何活动设备：{filter}。");
        }
        return devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault();
    }

    private sealed record AudioCombination(
        string Name,
        bool CaptureSystem,
        bool CaptureMicrophone,
        AudioDeviceInfo? SystemDevice,
        AudioDeviceInfo? MicrophoneDevice,
        bool VideoOnly = false);
}
