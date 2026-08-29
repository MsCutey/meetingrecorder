namespace MeetingRecorder.Core.Models;

public enum RecordingMode
{
    VideoAndAudio,
    AudioOnly
}

public enum CaptureTargetKind
{
    Display,
    Window
}

public enum AudioSourceKind
{
    SystemAudio,
    Microphone
}

public enum VideoQualityPreset
{
    FullHd,
    Hd,
    Source
}

public enum RecordingState
{
    Idle,
    Countdown,
    Recording,
    Paused,
    Finalizing,
    Failed
}

public sealed record CaptureTargetInfo(
    string Id,
    string DisplayName,
    CaptureTargetKind Kind,
    string? DeviceName,
    long NativeHandle,
    int Width,
    int Height,
    int? ProcessId = null)
{
    public override string ToString() => DisplayName;
}

public sealed record AudioDeviceInfo(
    string Id,
    string DeviceName,
    string DisplayName,
    AudioSourceKind Kind,
    bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{DisplayName}（默认）" : DisplayName;
}

public sealed record RecordingRequest
{
    public required RecordingMode Mode { get; init; }
    public CaptureTargetInfo? CaptureTarget { get; init; }
    public AudioDeviceInfo? SystemAudioDevice { get; init; }
    public AudioDeviceInfo? MicrophoneDevice { get; init; }
    public bool CaptureSystemAudio { get; init; } = true;
    public bool CaptureMicrophone { get; init; } = true;
    public VideoQualityPreset Quality { get; init; } = VideoQualityPreset.FullHd;
    public required string OutputDirectory { get; init; }
    public int CountdownSeconds { get; init; } = 3;
    public bool PreferHardwareEncoding { get; init; } = true;
    public nint WindowToExclude { get; init; }
}

public sealed record RecordingProgress(
    TimeSpan Elapsed,
    long FileSizeBytes,
    TimeSpan? EstimatedDiskTimeRemaining,
    double SystemAudioLevel,
    double MicrophoneLevel,
    string? Warning = null);

public sealed record PreviewFrame(int Width, int Height, int Stride, byte[] Bgra32);

public sealed record RecordingHistoryItem
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required RecordingMode Mode { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public required TimeSpan Duration { get; init; }
    public required long FileSizeBytes { get; init; }
    public string? Warning { get; init; }
}

public sealed record ActiveSession
{
    public required string Id { get; init; }
    public required RecordingMode Mode { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string FinalPath { get; init; }
    public required string PartialPath { get; init; }
    public string? RecoveryVideoPath { get; init; }
    public string Status { get; init; } = "Recording";
}

public sealed record RecoveryCandidate(
    string Id,
    RecordingMode Mode,
    string SuggestedPath,
    string? PartialMediaPath,
    string? RecoveryVideoPath,
    DateTimeOffset StartedAt);

public sealed class RecordingStateChangedEventArgs(RecordingState previous, RecordingState current) : EventArgs
{
    public RecordingState Previous { get; } = previous;
    public RecordingState Current { get; } = current;
}

public sealed class RecordingProgressEventArgs(RecordingProgress progress) : EventArgs
{
    public RecordingProgress Progress { get; } = progress;
}

public sealed class PreviewFrameEventArgs(PreviewFrame frame) : EventArgs
{
    public PreviewFrame Frame { get; } = frame;
}

public sealed class RecordingCompletedEventArgs(RecordingHistoryItem item) : EventArgs
{
    public RecordingHistoryItem Item { get; } = item;
}
