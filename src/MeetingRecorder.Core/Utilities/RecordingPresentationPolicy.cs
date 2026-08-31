using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Utilities;

public static class RecordingPresentationPolicy
{
    public static bool ShouldAttemptCaptureProtection(RecordingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Mode == RecordingMode.VideoAndAudio &&
               request.CaptureTarget?.Kind == CaptureTargetKind.Display &&
               request.ExcludeMainWindowFromDisplayCapture;
    }

    public static bool ShouldEnablePreview(RecordingRequest request, bool captureProtectionApplied)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = captureProtectionApplied;
        return request.PreviewEnabled &&
               request.Mode == RecordingMode.VideoAndAudio &&
               request.CaptureTarget is not null;
    }
}
