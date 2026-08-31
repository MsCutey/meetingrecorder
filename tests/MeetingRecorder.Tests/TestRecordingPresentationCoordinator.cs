using MeetingRecorder.Core.Abstractions;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Tests;

internal sealed class TestRecordingPresentationCoordinator(
    RecordingPresentationDecision? decision = null,
    Exception? prepareError = null) : IRecordingPresentationCoordinator
{
    public int PrepareCalls { get; private set; }
    public int RestoreCalls { get; private set; }
    public RecordingRequest? LastRequest { get; private set; }

    public Task<RecordingPresentationDecision> PrepareAsync(
        RecordingRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PrepareCalls++;
        LastRequest = request;
        return prepareError is null
            ? Task.FromResult(decision ?? new RecordingPresentationDecision(false, false))
            : Task.FromException<RecordingPresentationDecision>(prepareError);
    }

    public Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        RestoreCalls++;
        return Task.CompletedTask;
    }
}
