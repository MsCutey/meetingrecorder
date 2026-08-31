using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Protocol;
using MeetingRecorder.Engine.Recording;

namespace MeetingRecorder.Tests;

public sealed class VideoRecordingOptionsTests
{
    [Fact]
    public void DualAudio_UsesLowLatencyWithoutExpensivePerSampleMonoCopy()
    {
        var request = Request(captureSystemAudio: true, captureMicrophone: true);

        var audio = VideoRecordingEngine.CreateAudioOptions(request);
        var video = VideoRecordingEngine.CreateVideoEncoderOptions(request);

        Assert.False(audio.ForceInputDeviceMono ?? true);
        Assert.Equal(1f, audio.OutputVolume);
        Assert.True(video.IsLowLatencyEnabled);
        Assert.False(video.IsThrottlingDisabled);
    }

    [Fact]
    public void SystemAudioOnly_GetsConservativeNativeBoost()
    {
        var request = Request(captureSystemAudio: true, captureMicrophone: false);

        var audio = VideoRecordingEngine.CreateAudioOptions(request);

        Assert.Equal(2f, audio.OutputVolume);
        Assert.False(audio.ForceInputDeviceMono ?? true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewDecision_ControlsNativeFrameGeneration(bool enabled)
    {
        var request = Request(captureSystemAudio: false, captureMicrophone: false) with
        {
            EnablePreview = enabled
        };

        var output = VideoRecordingEngine.CreateOutputOptions(request);

        Assert.Equal(enabled, output.IsVideoFramePreviewEnabled);
    }

    [Fact]
    public void WindowFallbackCrop_IsForwardedToOutputSourceRect()
    {
        var request = Request(captureSystemAudio: true, captureMicrophone: false);
        var crop = new ScreenRecorderLib.ScreenRect(10, 20, 640, 480);

        var output = VideoRecordingEngine.CreateOutputOptions(request, crop);

        Assert.NotNull(output.SourceRect);
        Assert.Equal(10, output.SourceRect!.Left);
        Assert.Equal(20, output.SourceRect.Top);
        Assert.Equal(640, output.SourceRect.Width);
        Assert.Equal(480, output.SourceRect.Height);
    }

    [Fact]
    public void WithoutCrop_SourceRectRemainsNull()
    {
        var output = VideoRecordingEngine.CreateOutputOptions(Request(captureSystemAudio: false, captureMicrophone: true));

        Assert.Null(output.SourceRect);
    }

    private static WorkerRecordingRequest Request(bool captureSystemAudio, bool captureMicrophone) => new()
    {
        SessionId = "test",
        Mode = RecordingMode.VideoAndAudio,
        CaptureSystemAudio = captureSystemAudio,
        CaptureMicrophone = captureMicrophone,
        PartialPath = "test.partial.mp4",
        EngineLogPath = "engine.log",
        UseHardwareEncoding = true
    };
}
