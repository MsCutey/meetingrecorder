using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Protocol;
using MeetingRecorder.Engine.Abstractions;

namespace MeetingRecorder.Engine.Recording;

public sealed class ResilientAudioOnlyRecordingEngine : IRecordingEngine
{
    private readonly WorkerRecordingRequest _request;
    private readonly Func<IRecordingEngine> _fallbackFactory;
    private IRecordingEngine _active;
    private string? _fallbackReason;
    private bool _disposed;

    public ResilientAudioOnlyRecordingEngine(WorkerRecordingRequest request)
        : this(
            request,
            () => new AudioOnlyRecordingEngine(request),
            () => new ScreenRecorderAudioOnlyEngine(request))
    {
    }

    public ResilientAudioOnlyRecordingEngine(
        WorkerRecordingRequest request,
        Func<IRecordingEngine> primaryFactory,
        Func<IRecordingEngine> fallbackFactory)
    {
        _request = request;
        ArgumentNullException.ThrowIfNull(primaryFactory);
        ArgumentNullException.ThrowIfNull(fallbackFactory);
        _fallbackFactory = fallbackFactory;
        _active = primaryFactory();
        Attach(_active);
    }

    public TimeSpan Elapsed => _active.Elapsed;
    public double SystemAudioLevel => _active.SystemAudioLevel;
    public double MicrophoneLevel => _active.MicrophoneLevel;
    public string? Warning => _fallbackReason is null
        ? _active.Warning
        : $"{_active.Warning} 原因：{_fallbackReason}";

    public event EventHandler<PreviewFrameEventArgs>? PreviewFrameAvailable;
    public event EventHandler<EngineFaultEventArgs>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _active.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AudioCaptureInitializationException error)
        {
            Detach(_active);
            await _active.DisposeAsync().ConfigureAwait(false);
            DeleteIfPresent(_request.PartialPath);
            if (_request.RecoveryMediaPath is { } recoveryPath) DeleteIfPresent(recoveryPath);
            _fallbackReason = error.InnerException?.Message ?? error.Message;
            _active = _fallbackFactory();
            Attach(_active);
            await _active.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken = default) => _active.PauseAsync(cancellationToken);
    public Task ResumeAsync(CancellationToken cancellationToken = default) => _active.ResumeAsync(cancellationToken);
    public Task SetMicrophoneEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        _active.SetMicrophoneEnabledAsync(enabled, cancellationToken);
    public Task<TimeSpan> StopAsync(CancellationToken cancellationToken = default) => _active.StopAsync(cancellationToken);

    private void Attach(IRecordingEngine engine)
    {
        engine.PreviewFrameAvailable += OnPreviewFrameAvailable;
        engine.Faulted += OnFaulted;
    }

    private void Detach(IRecordingEngine engine)
    {
        engine.PreviewFrameAvailable -= OnPreviewFrameAvailable;
        engine.Faulted -= OnFaulted;
    }

    private void OnPreviewFrameAvailable(object? sender, PreviewFrameEventArgs eventArgs) =>
        PreviewFrameAvailable?.Invoke(this, eventArgs);

    private void OnFaulted(object? sender, EngineFaultEventArgs eventArgs) => Faulted?.Invoke(this, eventArgs);

    private static void DeleteIfPresent(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        Detach(_active);
        await _active.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
    }
}
