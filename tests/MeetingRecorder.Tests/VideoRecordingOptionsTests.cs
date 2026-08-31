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
