using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Utilities;

namespace MeetingRecorder.Core.Abstractions;

public interface IRecordingPresentationCoordinator
{
    Task<RecordingPresentationDecision> PrepareAsync(
        RecordingRequest request,
        CancellationToken cancellationToken = default);

    Task RestoreAsync(CancellationToken cancellationToken = default);
}

public sealed record RecordingPresentationDecision(
    bool CaptureProtectionApplied,
    bool EnablePreview,
    string? Warning = null);

internal sealed class SafeRecordingPresentationCoordinator : IRecordingPresentationCoordinator
{
    public Task<RecordingPresentationDecision> PrepareAsync(
        RecordingRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RecordingPresentationDecision(
            false,
            RecordingPresentationPolicy.ShouldEnablePreview(request, captureProtectionApplied: false)));
    }

    public Task RestoreAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
