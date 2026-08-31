using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Engine.Abstractions;

public interface IRecordingEngine : IAsyncDisposable
{
    TimeSpan Elapsed { get; }
    double SystemAudioLevel { get; }
    double MicrophoneLevel { get; }
    string? Warning { get; }

    event EventHandler<PreviewFrameEventArgs>? PreviewFrameAvailable;
    event EventHandler<EngineFaultEventArgs>? Faulted;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task<TimeSpan> StopAsync(CancellationToken cancellationToken = default);
}

public sealed class EngineFaultEventArgs(Exception error) : EventArgs
{
    public Exception Error { get; } = error;
}

public sealed class HardwareEncoderUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class AudioBufferOverflowException(string message) : IOException(message);

public sealed class AudioCaptureInitializationException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
