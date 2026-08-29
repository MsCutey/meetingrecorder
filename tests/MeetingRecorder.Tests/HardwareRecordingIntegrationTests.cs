using System.Text;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Recording;

namespace MeetingRecorder.Tests;

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
            await using var service = new ScreenRecordingService(repository);
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
            await Task.Delay(TimeSpan.FromSeconds(2));
            await service.StopAsync();
            var m4a = service.CurrentOutputPath!;
            Assert.EndsWith(".m4a", m4a, StringComparison.OrdinalIgnoreCase);
            Assert.True(new FileInfo(m4a).Length > 1_000);

            var display = (await service.GetCaptureTargetsAsync()).First(target => target.Kind == CaptureTargetKind.Display);
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
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
