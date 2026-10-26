using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Protocol;
using MeetingRecorder.Engine.Recording;

namespace MeetingRecorder.Tests;

public sealed class VideoRecordingOptionsTests
{
    [Fact]
    public void WindowHealthCheck_RejectsOpaqueBlackButAcceptsImageContent()
    {
        Assert.False(VideoRecordingEngine.HasVisiblePixels([0, 0, 0, 255, 1, 1, 1, 255]));
        Assert.True(VideoRecordingEngine.HasVisiblePixels([0, 0, 0, 255, 0, 100, 0, 255]));
    }

    [Fact]
    public void StartMuted_ZerosMicrophoneVolumeBeforeNativeCaptureStarts()
    {
        var audio = VideoRecordingEngine.CreateAudioOptions(Request(true, true) with { StartMicrophoneMuted = true });
        Assert.True(audio.IsInputDeviceEnabled);
        Assert.Equal(0f, audio.InputVolume);
        Assert.True(audio.IsOutputDeviceEnabled);
    }

    [Theory]
    [InlineData(2560, 1440, VideoQualityPreset.FullHd, 1920, 1080)]
    [InlineData(1920, 1200, VideoQualityPreset.Hd, 1152, 720)]
    [InlineData(800, 601, VideoQualityPreset.FullHd, 800, 600)]
    [InlineData(1080, 1920, VideoQualityPreset.FullHd, 606, 1080)]
    [InlineData(1921, 1081, VideoQualityPreset.Source, 1920, 1080)]
    [InlineData(1, 0, VideoQualityPreset.Hd, 2, 2)]
    public void OutputSize_PreservesAspectRatioWithoutUpscaling(int width, int height, VideoQualityPreset quality, int expectedWidth, int expectedHeight)
    {
        var request = Request(false, false) with
        {
            CaptureTarget = new CaptureTargetInfo("test", "test", CaptureTargetKind.Window, null, 1, width, height),
            Quality = quality
        };
        var size = VideoRecordingEngine.CreateOutputOptions(request).OutputFrameSize;
        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
    }

    [Fact]
    public void FallbackSize_UsesActualCropOrDisplayInsteadOfOldWindowDimensions()
    {
        var request = Request(false, false) with
        {
            CaptureTarget = new CaptureTargetInfo("test", "test", CaptureTargetKind.Window, null, 1, 320, 200)
        };
        var display = new ScreenRecorderLib.ScreenSize(2560, 1440);
        var full = VideoRecordingEngine.CreateOutputOptions(request, sourceSize: display).OutputFrameSize;
        Assert.Equal(1920, full.Width);
        Assert.Equal(1080, full.Height);
        using var source = new ScreenRecorderLib.DisplayRecordingSource("test");
        var size = VideoRecordingEngine.ConfigureSourceCrop(source, new ScreenRecorderLib.ScreenRect(0, 0, 640, 480), display);
        var cropped = VideoRecordingEngine.CreateOutputOptions(request, size).OutputFrameSize;
        Assert.Equal(640, cropped.Width);
        Assert.Equal(480, cropped.Height);
    }

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
    public void WindowFallbackCrop_AppliesBeforeCompositionWithoutSecondOutputCrop()
    {
        var request = Request(captureSystemAudio: true, captureMicrophone: false);
        var crop = new ScreenRecorderLib.ScreenRect(10, 20, 640, 480);

        using var source = new ScreenRecorderLib.DisplayRecordingSource("test");
        var size = VideoRecordingEngine.ConfigureSourceCrop(source, crop, new ScreenRecorderLib.ScreenSize(1920, 1080));
        var output = VideoRecordingEngine.CreateOutputOptions(request, size);

        Assert.Same(crop, source.SourceRect);
        Assert.Null(output.SourceRect);
        Assert.Equal(640, output.OutputFrameSize.Width);
        Assert.Equal(480, output.OutputFrameSize.Height);
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
