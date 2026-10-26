using System.Text;
using System.Diagnostics;
using MeetingRecorder.Core.Abstractions;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Recording;

namespace MeetingRecorder.Tests;

[Collection("Hardware")]
public sealed class HardwareRecordingIntegrationTests
{
    [HardwareFact]
    public async Task AudioOnlyAndDisplayRecording_CreateExpectedMediaTracks()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeetingRecorderHardwareTests", Guid.NewGuid().ToString("N"));
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
            await using var service = new WorkerRecordingService(
                repository,
                launcher: new WorkerProcessLauncher(Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_WORKER")),
                presentationCoordinator: new TestRecordingPresentationCoordinator(
                    new RecordingPresentationDecision(false, true)));
            var audioDevices = await service.GetAudioDevicesAsync(AudioSourceKind.SystemAudio);
            var audioDevice = audioDevices.FirstOrDefault(device => device.IsDefault) ?? audioDevices.First();

            await service.StartAsync(new RecordingRequest
            {
                Mode = RecordingMode.AudioOnly,
                CaptureSystemAudio = true,
                CaptureMicrophone = false,
                SystemAudioDevice = audioDevice,
                OutputDirectory = root,
                CountdownSeconds = 0,
                PreferHardwareEncoding = false
            });
            var audioWallClock = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            await service.PauseAsync();
            await Task.Delay(TimeSpan.FromSeconds(1));
            await service.ResumeAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            await service.StopAsync();
            audioWallClock.Stop();
            var m4a = service.CurrentOutputPath!;
            Assert.EndsWith(".m4a", m4a, StringComparison.OrdinalIgnoreCase);
            Assert.True(new FileInfo(m4a).Length > 1_000);
            var m4aAtoms = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(m4a));
            Assert.Contains("mp4a", m4aAtoms, StringComparison.Ordinal);
            Assert.DoesNotContain("avc1", m4aAtoms, StringComparison.Ordinal);
            var audioHistory = Assert.Single(await repository.LoadHistoryAsync(), item => item.FilePath == m4a);
            Assert.InRange(audioHistory.Duration, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5));
            Assert.True(audioWallClock.Elapsed - audioHistory.Duration > TimeSpan.FromMilliseconds(600));

            var display = (await service.GetCaptureTargetsAsync()).First(target => target.Kind == CaptureTargetKind.Display);
            var previewCount = 0;
            var invalidPreviewCount = 0;
            service.PreviewFrameAvailable += (_, eventArgs) =>
            {
                if (eventArgs.Frame.Width <= 0 || eventArgs.Frame.Height <= 0)
                {
                    Interlocked.Increment(ref invalidPreviewCount);
                }
                Interlocked.Increment(ref previewCount);
            };
            await service.StartAsync(new RecordingRequest
            {
                Mode = RecordingMode.VideoAndAudio,
                CaptureTarget = display,
                CaptureSystemAudio = true,
                CaptureMicrophone = false,
                SystemAudioDevice = audioDevice,
                Quality = VideoQualityPreset.Hd,
                OutputDirectory = root,
                CountdownSeconds = 0,
                PreferHardwareEncoding = true
            });
            await Task.Delay(TimeSpan.FromSeconds(2));
            await service.StopAsync();
            var mp4 = service.CurrentOutputPath!;
            var atoms = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(mp4));
            Assert.Contains("avc1", atoms, StringComparison.Ordinal);
            Assert.Contains("mp4a", atoms, StringComparison.Ordinal);
            Assert.True(Volatile.Read(ref previewCount) > 0, "源预览在视频录制期间没有产生任何帧。");
            Assert.Equal(0, Volatile.Read(ref invalidPreviewCount));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [HardwareFact]
    public async Task DualAudioDisplayRecording_RemainsRealtimeAndKeepsPreviewResponsive()
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("MEETINGRECORDER_DUAL_TEST_SECONDS"), out var configured)
            ? Math.Clamp(configured, 3, 120)
            : 10;
        var root = Path.Combine(Path.GetTempPath(), "MeetingRecorderDualAudioTests", Guid.NewGuid().ToString("N"));
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
            await using var service = new WorkerRecordingService(
                repository,
                launcher: new WorkerProcessLauncher(Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_WORKER")),
                presentationCoordinator: new TestRecordingPresentationCoordinator(
                    new RecordingPresentationDecision(false, true)));
            var display = (await service.GetCaptureTargetsAsync()).First(target => target.Kind == CaptureTargetKind.Display);
            var system = SelectDevice(
                await service.GetAudioDevicesAsync(AudioSourceKind.SystemAudio),
                "MEETINGRECORDER_DUAL_SYSTEM_DEVICE");
            var microphone = SelectDevice(
                await service.GetAudioDevicesAsync(AudioSourceKind.Microphone),
                "MEETINGRECORDER_DUAL_MICROPHONE_DEVICE");
            Assert.NotNull(system);
            Assert.NotNull(microphone);

            var previewCount = 0;
            service.PreviewFrameAvailable += (_, _) => Interlocked.Increment(ref previewCount);
            await service.StartAsync(new RecordingRequest
            {
                Mode = RecordingMode.VideoAndAudio,
                CaptureTarget = display,
                CaptureSystemAudio = true,
                CaptureMicrophone = true,
                SystemAudioDevice = system,
                MicrophoneDevice = microphone,
                Quality = VideoQualityPreset.FullHd,
                OutputDirectory = root,
                CountdownSeconds = 0,
                PreferHardwareEncoding = true
            });
            var wallClock = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            wallClock.Stop();
            await service.StopAsync();

            var output = service.CurrentOutputPath ?? throw new Xunit.Sdk.XunitException("双路录制没有生成输出路径。");
            var item = Assert.Single(await repository.LoadHistoryAsync(), entry => entry.FilePath == output);
            Assert.True(File.Exists(output));
            Assert.True(new FileInfo(output).Length > 10_000);
            Assert.InRange(item.Duration.TotalSeconds, seconds - 1.5, seconds + 2.5);
            Assert.InRange(item.Duration / wallClock.Elapsed, 0.85, 1.05);
            Assert.True(Volatile.Read(ref previewCount) >= seconds, "双路录制期间预览更新明显落后。");
            var atoms = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(output));
            Assert.Contains("avc1", atoms, StringComparison.Ordinal);
            Assert.Contains("mp4a", atoms, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static AudioDeviceInfo? SelectDevice(IReadOnlyList<AudioDeviceInfo> devices, string environmentVariable)
    {
        var filter = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            return devices.FirstOrDefault(device =>
                device.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                device.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }
        return devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault();
    }
}
