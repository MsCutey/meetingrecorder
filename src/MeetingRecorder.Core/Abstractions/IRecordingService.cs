using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Abstractions;

public interface IRecordingService : IAsyncDisposable
{
    RecordingState State { get; }
    string? CurrentOutputPath { get; }

    event EventHandler<RecordingStateChangedEventArgs>? StateChanged;
    event EventHandler<RecordingProgressEventArgs>? ProgressChanged;
    event EventHandler<PreviewFrameEventArgs>? PreviewFrameAvailable;
    event EventHandler<RecordingCompletedEventArgs>? RecordingCompleted;

    Task<IReadOnlyList<CaptureTargetInfo>> GetCaptureTargetsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AudioDeviceInfo>> GetAudioDevicesAsync(AudioSourceKind kind, CancellationToken cancellationToken = default);
    Task StartAsync(RecordingRequest request, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
