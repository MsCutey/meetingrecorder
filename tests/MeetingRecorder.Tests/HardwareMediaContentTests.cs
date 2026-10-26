using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Recording;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit.Abstractions;

namespace MeetingRecorder.Tests;

[Collection("Hardware")]
public sealed class HardwareMediaContentTests(ITestOutputHelper output)
{
    [HardwareFact]
    public async Task SystemTone_DecodesFromAudioAndVideo_AndPauseIsExcluded()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeetingRecorderContentTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var service = CreateService(root);
            var system = (await service.GetAudioDevicesAsync(AudioSourceKind.SystemAudio)).First(device => device.IsDefault);
            var display = (await service.GetCaptureTargetsAsync()).First(target => target.Kind == CaptureTargetKind.Display);
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(system.Id);
            using var player = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
            player.Init(new TestTone());
            player.Play();
            foreach (var mode in new[] { RecordingMode.AudioOnly, RecordingMode.VideoAndAudio })
            {
                await service.StartAsync(new RecordingRequest
                {
                    Mode = mode, CaptureTarget = mode == RecordingMode.VideoAndAudio ? display : null,
                    CaptureSystemAudio = true, CaptureMicrophone = false, SystemAudioDevice = system,
                    OutputDirectory = root, CountdownSeconds = 0, Quality = VideoQualityPreset.Hd
                });
                await Task.Delay(1500);
                await service.PauseAsync();
                await Task.Delay(1500);
                await service.ResumeAsync();
                await Task.Delay(1500);
                await service.StopAsync();
                var (duration, peak) = DecodeAudio(service.CurrentOutputPath!);
                output.WriteLine($"{mode}: decoded duration={duration:F3}s, peak={peak:F6}");
                Assert.InRange(duration, 2.5, 4.2);
                Assert.True(peak > 0.005, $"测试音没有出现在 {mode} 成品中：{peak}");
                if (mode == RecordingMode.VideoAndAudio) await DecodeVideoAsync(service.CurrentOutputPath!);
            }
            player.Stop();
        }
        finally { Directory.Delete(root, true); }
    }

    [HardwareFact]
    public async Task MicrophoneOnly_StartMutedProducesSilence_InBothModes()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeetingRecorderMuteTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var service = CreateService(root);
            var microphone = (await service.GetAudioDevicesAsync(AudioSourceKind.Microphone)).First(device => device.IsDefault);
            var display = (await service.GetCaptureTargetsAsync()).First(target => target.Kind == CaptureTargetKind.Display);
            foreach (var mode in new[] { RecordingMode.AudioOnly, RecordingMode.VideoAndAudio })
            {
                await service.StartAsync(new RecordingRequest
                {
                    Mode = mode, CaptureTarget = mode == RecordingMode.VideoAndAudio ? display : null,
                    CaptureSystemAudio = false, CaptureMicrophone = true, MicrophoneDevice = microphone,
                    StartMicrophoneMuted = true, OutputDirectory = root, CountdownSeconds = 0,
                    Quality = VideoQualityPreset.Hd
                });
                await Task.Delay(1800);
                await service.StopAsync();
                var (duration, peak) = DecodeAudio(service.CurrentOutputPath!);
                output.WriteLine($"{mode} muted: decoded duration={duration:F3}s, peak={peak:F6}");
                Assert.InRange(duration, 1.3, 3.5);
                Assert.InRange(peak, 0, 0.0001);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [HardwareFact]
    public async Task SelectedWindow_RecordsFramesAndSupportsMicrophoneToggleWhilePaused()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeetingRecorderWindowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var service = CreateService(root);
            // Opt-in selects a controlled test window, never an arbitrary personal window.
            var title = Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_WINDOW");
            Assert.False(string.IsNullOrWhiteSpace(title), "请设置 MEETINGRECORDER_TEST_WINDOW 为测试窗口标题。");
            var target = (await service.GetCaptureTargetsAsync()).First(item => item.Kind == CaptureTargetKind.Window && item.DisplayName.Contains(title!, StringComparison.Ordinal));
            var microphone = (await service.GetAudioDevicesAsync(AudioSourceKind.Microphone)).First(device => device.IsDefault);
            var frames = 0;
            var previewEnabled = Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_NO_PREVIEW") != "1";
            service.PreviewFrameAvailable += (_, _) => Interlocked.Increment(ref frames);
            await service.StartAsync(new RecordingRequest
            {
                Mode = RecordingMode.VideoAndAudio, CaptureTarget = target,
                CaptureSystemAudio = false, CaptureMicrophone = true, MicrophoneDevice = microphone,
                StartMicrophoneMuted = true, OutputDirectory = root, CountdownSeconds = 0,
                Quality = VideoQualityPreset.Hd, PreviewEnabled = previewEnabled
            });
            await Task.Delay(1000);
            await service.PauseAsync();
            await service.SetMicrophoneEnabledAsync(true);
            await service.SetMicrophoneEnabledAsync(false);
            await service.ResumeAsync();
            await Task.Delay(1500);
            await service.StopAsync();
            if (previewEnabled) Assert.True(frames > 0);
            else Assert.Equal(0, frames);
            var (duration, peak) = DecodeAudio(service.CurrentOutputPath!);
            output.WriteLine($"Window: frames={frames}, decoded duration={duration:F3}s, peak={peak:F6}");
            Assert.InRange(peak, 0, 0.0001);
            Assert.InRange(duration, 2, 4);
            await DecodeVideoAsync(service.CurrentOutputPath!);
        }
        finally { Directory.Delete(root, true); }
    }

    private static WorkerRecordingService CreateService(string root)
    {
        var data = Path.Combine(root, "data");
        var repository = new AppRepository(new AppDataPaths(data, Path.Combine(data, "settings.json"),
            Path.Combine(data, "history.json"), Path.Combine(data, "active-session.json"), Path.Combine(data, "assets"), true));
        var worker = Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_WORKER");
        return new WorkerRecordingService(repository, launcher: string.IsNullOrWhiteSpace(worker) ? null : new WorkerProcessLauncher(worker));
    }

    private static (double Duration, float Peak) DecodeAudio(string path)
    {
        using var reader = new MediaFoundationReader(path);
        var samples = reader.ToSampleProvider();
        var buffer = new float[8192];
        long count = 0;
        float peak = 0;
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            count += read;
            for (var index = 0; index < read; index++) peak = Math.Max(peak, Math.Abs(buffer[index]));
        }
        Assert.True(count > 0, "成品音轨无法解码。");
        return ((double)count / samples.WaveFormat.SampleRate / samples.WaveFormat.Channels, peak);
    }

    private async Task DecodeVideoAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
        var properties = clip.GetVideoEncodingProperties();
        Assert.True(properties.Width >= 2 && properties.Width % 2 == 0);
        Assert.True(properties.Height >= 2 && properties.Height <= 720 && properties.Height % 2 == 0);
        var composition = new Windows.Media.Editing.MediaComposition();
        composition.Clips.Add(clip);
        using var thumbnail = await composition.GetThumbnailAsync(clip.OriginalDuration / 2, 160, 0,
            Windows.Media.Editing.VideoFramePrecision.NearestFrame);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(thumbnail);
        var data = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, new Windows.Graphics.Imaging.BitmapTransform(),
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        var pixels = data.DetachPixelData();
        var min = 765;
        var max = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var value = pixels[index] + pixels[index + 1] + pixels[index + 2];
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }
        output.WriteLine($"Decoded video: {properties.Width}x{properties.Height}, duration={clip.OriginalDuration.TotalSeconds:F3}s, RGB range={max - min}");
        Assert.True(max - min > 30, "成品解码帧没有可辨识的画面变化。");
    }

    private sealed class TestTone : IWaveProvider
    {
        private long _frame;
        public WaveFormat WaveFormat { get; } = new(48000, 16, 2);
        public int Read(byte[] buffer, int offset, int count)
        {
            count -= count % 4;
            for (var index = 0; index < count; index += 4)
            {
                var sample = (short)(Math.Sin(2 * Math.PI * 440 * _frame++ / 48000) * 2000);
                buffer[offset + index] = buffer[offset + index + 2] = (byte)sample;
                buffer[offset + index + 1] = buffer[offset + index + 3] = (byte)(sample >> 8);
            }
            return count;
        }
    }
}

[CollectionDefinition("Hardware", DisableParallelization = true)]
public sealed class HardwareCollection;
