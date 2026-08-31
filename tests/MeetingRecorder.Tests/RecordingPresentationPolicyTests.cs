using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Utilities;

namespace MeetingRecorder.Tests;

public sealed class RecordingPresentationPolicyTests
{
    [Fact]
    public void AudioOnly_NeverProtectsOrPreviews()
    {
        var request = Request(RecordingMode.AudioOnly, null, exclude: true);

        Assert.False(RecordingPresentationPolicy.ShouldAttemptCaptureProtection(request));
        Assert.False(RecordingPresentationPolicy.ShouldEnablePreview(request, captureProtectionApplied: true));
    }

    [Fact]
    public void WindowCapture_PreviewsWhenSwitchEnabled()
    {
        var request = Request(RecordingMode.VideoAndAudio, CaptureTargetKind.Window, exclude: false);

        Assert.False(RecordingPresentationPolicy.ShouldAttemptCaptureProtection(request));
        Assert.True(RecordingPresentationPolicy.ShouldEnablePreview(request, captureProtectionApplied: false));
    }

    [Fact]
    public void DisplayCapture_PreviewsWhenSwitchEnabledEvenWithoutProtection()
    {
        var request = Request(RecordingMode.VideoAndAudio, CaptureTargetKind.Display, exclude: false);

        Assert.False(RecordingPresentationPolicy.ShouldAttemptCaptureProtection(request));
        Assert.True(RecordingPresentationPolicy.ShouldEnablePreview(request, captureProtectionApplied: false));
    }

    [Fact]
    public void ProtectedDisplay_PreviewsRegardlessOfProtectionOutcome()
    {
        var request = Request(RecordingMode.VideoAndAudio, CaptureTargetKind.Display, exclude: true);

        Assert.True(RecordingPresentationPolicy.ShouldAttemptCaptureProtection(request));
        Assert.True(RecordingPresentationPolicy.ShouldEnablePreview(request, captureProtectionApplied: false));
        Assert.True(RecordingPresentationPolicy.ShouldEnablePreview(request, captureProtectionApplied: true));
    }

    [Fact]
    public void PreviewSwitchOff_DisablesPreviewForAllTargets()
    {
        var display = Request(RecordingMode.VideoAndAudio, CaptureTargetKind.Display, exclude: false) with { PreviewEnabled = false };
        var window = Request(RecordingMode.VideoAndAudio, CaptureTargetKind.Window, exclude: false) with { PreviewEnabled = false };

        Assert.False(RecordingPresentationPolicy.ShouldEnablePreview(display, captureProtectionApplied: true));
        Assert.False(RecordingPresentationPolicy.ShouldEnablePreview(window, captureProtectionApplied: false));
    }

    private static RecordingRequest Request(
        RecordingMode mode,
        CaptureTargetKind? kind,
        bool exclude) =>
        new()
        {
            Mode = mode,
            CaptureTarget = kind is null
                ? null
                : new CaptureTargetInfo("target", "Target", kind.Value, "device", 1, 1280, 720),
            OutputDirectory = Path.GetTempPath(),
            ExcludeMainWindowFromDisplayCapture = exclude
        };
}
