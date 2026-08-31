using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Protocol;
using MeetingRecorder.Core.Utilities;
using MeetingRecorder.Engine.Abstractions;
using MeetingRecorder.Engine.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorderLib;

namespace MeetingRecorder.Engine.Recording;

/// <summary>
/// Last-resort audio-only path for endpoints that Windows rejects through managed WASAPI.
/// ScreenRecorderLib remains isolated in the per-session Worker and writes fragmented M4A so the
/// partial file itself is recoverable if the Worker terminates.
/// </summary>
public sealed class ScreenRecorderAudioOnlyEngine : IRecordingEngine
{
    private readonly WorkerRecordingRequest _request;
    private readonly RecordingClock _clock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly AudioEndpointPeakReader? _systemPeak;
    private readonly AudioEndpointPeakReader? _microphonePeak;
    private readonly TaskCompletionSource _started = NewSignal();
    private readonly TaskCompletionSource _completed = NewSignal();
    private Recorder? _recorder;
    private DisplayRecordingSource? _source;
    private Exception? _failure;
    private bool _isStarted;
    private bool _stopRequested;
    private bool _isStopped;
    private bool _disposed;

    public ScreenRecorderAudioOnlyEngine(WorkerRecordingRequest request)
    {
        _request = request;
        if (request.CaptureSystemAudio)
        {
            _systemPeak = new AudioEndpointPeakReader(request.SystemAudioDevice?.Id, DataFlow.Render);
        }
        if (request.CaptureMicrophone)
        {
            _microphonePeak = new AudioEndpointPeakReader(request.MicrophoneDevice?.Id, DataFlow.Capture);
        }
    }

    public TimeSpan Elapsed => _clock.Elapsed;
    public double SystemAudioLevel => _systemPeak?.ReadPeak() ?? 0;
    public double MicrophoneLevel => _microphonePeak?.ReadPeak() ?? 0;
    public string? Warning => "正在使用兼容音频引擎；异常中断时将从分段 M4A 临时文件恢复。";

    public event EventHandler<PreviewFrameEventArgs>? PreviewFrameAvailable;
    public event EventHandler<EngineFaultEventArgs>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isStarted) throw new InvalidOperationException("兼容录音引擎已经启动。");
            _source = DisplayRecordingSource.MainMonitor;
            _source.IsCursorCaptureEnabled = false;
            _source.IsBorderRequired = false;
            _source.IsVideoCaptureEnabled = true;
            _source.IsVideoFramePreviewEnabled = false;
            _recorder = Recorder.CreateRecorder(CreateOptions(_source));
            _recorder.OnStatusChanged += OnStatusChanged;
            _recorder.OnRecordingComplete += OnRecordingComplete;
            _recorder.OnRecordingFailed += OnRecordingFailed;
            _recorder.Record(_request.PartialPath);
            await _started.Task.WaitAsync(TimeSpan.FromSeconds(18), cancellationToken).ConfigureAwait(false);
            _clock.Start();
            _isStarted = true;
        }
        finally { _operationGate.Release(); }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            _recorder!.Pause();
            _clock.Pause();
        }
        finally { _operationGate.Release(); }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            _recorder!.Resume();
            _clock.Resume();
        }
        finally { _operationGate.Release(); }
    }

    public async Task<TimeSpan> StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isStopped) return _clock.Elapsed;
            EnsureActive();
            if (!_stopRequested)
            {
                _stopRequested = true;
                _clock.Stop();
                _recorder!.Stop();
            }
            await _completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_failure is not null) throw new InvalidOperationException("兼容音频引擎未能完成安全收尾。", _failure);
            TranscodeToAudioOnlyM4a();
            _isStopped = true;
            return _clock.Elapsed;
        }
        finally { _operationGate.Release(); }
    }

    private RecorderOptions CreateOptions(DisplayRecordingSource source) => new()
    {
        SourceOptions = new SourceOptions { RecordingSources = [source] },
        OutputOptions = new OutputOptions
        {
            RecorderMode = RecorderMode.Video,
            IsVideoCaptureEnabled = false,
            IsVideoFramePreviewEnabled = false,
            OutputFrameSize = new ScreenSize(1280, 720)
        },
        VideoEncoderOptions = new VideoEncoderOptions
        {
            Encoder = new H264VideoEncoder(),
            Framerate = 30,
            Bitrate = 6_000_000,
            IsHardwareEncodingEnabled = false,
            IsFragmentedMp4Enabled = true,
            IsMp4FastStartEnabled = false
        },
        AudioOptions = new AudioOptions
        {
            IsAudioEnabled = true,
            IsOutputDeviceEnabled = _request.CaptureSystemAudio,
            IsInputDeviceEnabled = _request.CaptureMicrophone,
            AudioOutputDevice = _request.SystemAudioDevice?.DeviceName,
            AudioInputDevice = _request.MicrophoneDevice?.DeviceName,
            // 原生 6.6.0 的强制单声道分支会逐样本分配临时内存；端点重采样已负责声道转换。
            ForceInputDeviceMono = false,
            InputDeviceMasterChannel = 0,
            OutputVolume = _request.CaptureSystemAudio && !_request.CaptureMicrophone ? 2f : 1f,
            InputVolume = 1f,
            Bitrate = AudioBitrate.bitrate_128kbps,
            Channels = AudioChannels.Stereo
        },
        LogOptions = new LogOptions
        {
            IsLogEnabled = true,
            LogFilePath = _request.EngineLogPath,
            LogSeverityLevel = LogLevel.Info
        }
    };

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs eventArgs)
    {
        if (eventArgs.Status == RecorderStatus.Recording) _started.TrySetResult();
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs eventArgs) => _completed.TrySetResult();

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs eventArgs)
    {
        var error = new InvalidOperationException(
            string.IsNullOrWhiteSpace(eventArgs.Error) ? "原生兼容音频引擎报告未知错误。" : eventArgs.Error);
        _failure = error;
        _started.TrySetException(error);
        _completed.TrySetResult();
        if (_isStarted) Faulted?.Invoke(this, new EngineFaultEventArgs(error));
    }

    private void EnsureActive()
    {
        if (!_isStarted || _isStopped || _recorder is null) throw new InvalidOperationException("兼容录音引擎当前不可操作。");
    }

    private void TranscodeToAudioOnlyM4a()
    {
        var transcodedPath = _request.PartialPath + ".audio-only.m4a";
        try
        {
            using (var reader = new MediaFoundationReader(_request.PartialPath))
            {
                MediaFoundationEncoder.EncodeToAac(reader, transcodedPath, 128_000);
            }
            if (!File.Exists(transcodedPath) || new FileInfo(transcodedPath).Length == 0)
            {
                throw new InvalidDataException("兼容音频引擎没有生成有效的 M4A 文件。");
            }
            File.Move(transcodedPath, _request.PartialPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(transcodedPath)) File.Delete(transcodedPath); }
            catch { }
            throw;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_isStarted && !_isStopped)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await StopAsync(timeout.Token).ConfigureAwait(false); }
            catch { }
        }
        if (_recorder is not null)
        {
            _recorder.OnStatusChanged -= OnStatusChanged;
            _recorder.OnRecordingComplete -= OnRecordingComplete;
            _recorder.OnRecordingFailed -= OnRecordingFailed;
            _recorder.Dispose();
        }
        _source?.Dispose();
        _systemPeak?.Dispose();
        _microphonePeak?.Dispose();
        _operationGate.Dispose();
        _disposed = true;
        GC.KeepAlive(PreviewFrameAvailable);
    }
}
