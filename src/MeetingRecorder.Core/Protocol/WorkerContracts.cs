using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Protocol;

public enum WorkerMode
{
    Discovery,
    Recording
}

public enum WorkerErrorKind
{
    Validation,
    Preflight,
    HardwareEncoderUnavailable,
    EngineFailure,
    BufferOverflow,
    Protocol,
    Unexpected
}

public sealed record WorkerHello(string Token, WorkerMode Mode, int ProcessId, string Version);
public sealed record WorkerDiscoveryRequest(int ParentProcessId);
public sealed record WorkerDiscoveryResult(
    IReadOnlyList<CaptureTargetInfo> CaptureTargets,
    IReadOnlyList<AudioDeviceInfo> SystemAudioDevices,
    IReadOnlyList<AudioDeviceInfo> MicrophoneDevices,
    IReadOnlyList<string> Warnings);

public sealed record WorkerRecordingRequest
{
    public required string SessionId { get; init; }
    public required RecordingMode Mode { get; init; }
    public CaptureTargetInfo? CaptureTarget { get; init; }
    public AudioDeviceInfo? SystemAudioDevice { get; init; }
    public AudioDeviceInfo? MicrophoneDevice { get; init; }
    public bool CaptureSystemAudio { get; init; }
    public bool CaptureMicrophone { get; init; }
    public VideoQualityPreset Quality { get; init; }
    public required string PartialPath { get; init; }
    public string? RecoveryMediaPath { get; init; }
    public bool UseHardwareEncoding { get; init; }
    public bool EnablePreview { get; init; }
    public bool StartMicrophoneMuted { get; init; }
    public required string EngineLogPath { get; init; }
}

public sealed record WorkerStateUpdate(RecordingState State);
public sealed record WorkerMicrophoneUpdate(bool Enabled);
public sealed record WorkerCompleted(TimeSpan Duration, string? Warning);
public sealed record WorkerError(WorkerErrorKind Kind, string Message, string? Detail = null);

public sealed record PreflightIssue(string Code, string Message, bool IsFatal);
public sealed record PreflightResult(IReadOnlyList<PreflightIssue> Issues)
{
    public bool IsSuccess => Issues.All(issue => !issue.IsFatal);
}
