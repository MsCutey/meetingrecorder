using System.Diagnostics;
using System.Runtime.InteropServices;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Protocol;
using MeetingRecorder.Core.Utilities;
using MeetingRecorder.Engine.Abstractions;
using MeetingRecorder.Engine.Audio;
using NAudio.CoreAudioApi;
using ScreenRecorderLib;

namespace MeetingRecorder.Engine.Recording;

public sealed partial class VideoRecordingEngine : IRecordingEngine
{
    private static readonly ScreenSize PreviewSize = new(320, 180);
    private readonly WorkerRecordingRequest _request;
    private readonly RecordingClock _clock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly AudioEndpointPeakReader? _systemPeak;
    private readonly AudioEndpointPeakReader? _microphonePeak;
    private Recorder? _recorder;
    private RecordingSourceBase? _source;
    private TaskCompletionSource _started = NewSignal();
    private TaskCompletionSource _completed = NewSignal();
    private Exception? _failure;
    private long _lastPreviewTimestamp;
    private bool _isStarted;
    private bool _stopRequested;
    private bool _isStopped;
    private bool _disposed;

    public VideoRecordingEngine(WorkerRecordingRequest request)
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
    public string? Warning => GetCaptureTargetWarning();

    public event EventHandler<PreviewFrameEventArgs>? PreviewFrameAvailable;
    public event EventHandler<EngineFaultEventArgs>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isStarted) throw new InvalidOperationException("录制引擎已经启动。");
            _source = CreateRecordingSource(_request);
            _recorder = Recorder.CreateRecorder(CreateRecorderOptions(_source));
            if (_request.EnablePreview) _recorder.OnFrameRecorded += OnPreviewFrameRecorded;
            _recorder.OnStatusChanged += OnRecorderStatusChanged;
            _recorder.OnRecordingComplete += OnRecorderComplete;
            _recorder.OnRecordingFailed += OnRecorderFailed;
            try
            {
                _recorder.Record(_request.PartialPath);
                await _started.Task.WaitAsync(TimeSpan.FromSeconds(18), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (_request.UseHardwareEncoding && error is not OperationCanceledException)
            {
                throw new HardwareEncoderUnavailableException("硬件 H.264 编码器初始化失败。", error);
            }
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
            if (!_isStarted) throw new InvalidOperationException("录制引擎尚未启动。");
            if (!_stopRequested)
            {
                _stopRequested = true;
                _clock.Stop();
                _recorder!.Stop();
            }
            await _completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_failure is not null) throw new InvalidOperationException("视频录制引擎未能完成安全收尾。", _failure);
            _isStopped = true;
            return _clock.Elapsed;
        }
        finally { _operationGate.Release(); }
    }

    private RecorderOptions CreateRecorderOptions(RecordingSourceBase source) => new()
    {
        SourceOptions = new SourceOptions { RecordingSources = [source] },
        OutputOptions = CreateOutputOptions(_request),
        VideoEncoderOptions = CreateVideoEncoderOptions(_request),
        AudioOptions = CreateAudioOptions(_request),
        LogOptions = new LogOptions
        {
            IsLogEnabled = true,
            LogFilePath = _request.EngineLogPath,
            LogSeverityLevel = LogLevel.Info
        }
    };

    internal static OutputOptions CreateOutputOptions(WorkerRecordingRequest request) => new()
    {
        RecorderMode = RecorderMode.Video,
        OutputFrameSize = GetOutputSize(request),
        Stretch = StretchMode.Uniform,
        IsVideoCaptureEnabled = true,
        // 只在主程序明确允许时生成缩小预览；源预览始终关闭。
        IsVideoFramePreviewEnabled = request.EnablePreview,
        VideoFramePreviewSize = PreviewSize
    };

    private static RecordingSourceBase CreateRecordingSource(WorkerRecordingRequest request)
    {
        var target = request.CaptureTarget ?? throw new InvalidOperationException("视频模式必须选择显示器或窗口。");
        if (target.Kind == CaptureTargetKind.Display)
        {
            return new DisplayRecordingSource(target.DeviceName ?? string.Empty)
            {
                IsCursorCaptureEnabled = true,
                IsBorderRequired = false,
                IsVideoCaptureEnabled = true,
                IsVideoFramePreviewEnabled = false,
                Stretch = StretchMode.Uniform
            };
        }
        return new WindowRecordingSource(new nint(target.NativeHandle))
        {
            IsCursorCaptureEnabled = true,
            IsBorderRequired = false,
            IsVideoCaptureEnabled = true,
            IsVideoFramePreviewEnabled = false,
            Stretch = StretchMode.Uniform
        };
    }

    internal static VideoEncoderOptions CreateVideoEncoderOptions(WorkerRecordingRequest request) => new()
    {
        Encoder = new H264VideoEncoder
        {
            EncoderProfile = H264Profile.High,
            BitrateMode = H264BitrateControlMode.CBR
        },
        Framerate = 30,
        Bitrate = 6_000_000,
        Quality = 75,
        IsFixedFramerate = true,
        IsHardwareEncodingEnabled = request.UseHardwareEncoding,
        // 同时采集两路音频时优先保证实时编码，不让音频同步等待拖住视频渲染。
        IsLowLatencyEnabled = request.CaptureSystemAudio && request.CaptureMicrophone,
        IsThrottlingDisabled = false,
        IsFragmentedMp4Enabled = true,
        IsMp4FastStartEnabled = false
    };

    internal static AudioOptions CreateAudioOptions(WorkerRecordingRequest request) => new()
    {
        IsAudioEnabled = request.CaptureSystemAudio || request.CaptureMicrophone,
        IsOutputDeviceEnabled = request.CaptureSystemAudio,
        IsInputDeviceEnabled = request.CaptureMicrophone,
        AudioOutputDevice = request.SystemAudioDevice?.DeviceName,
        AudioInputDevice = request.MicrophoneDevice?.DeviceName,
        // 6.6.0 的强制单声道实现会为每个音频样本分配临时容器；
        // 单声道端点已由其重采样器转换为立体声，无需再次逐样本复制。
        ForceInputDeviceMono = false,
        InputDeviceMasterChannel = 0,
        OutputVolume = request.CaptureSystemAudio && !request.CaptureMicrophone ? 2f : 1f,
        InputVolume = 1f,
        Bitrate = AudioBitrate.bitrate_128kbps,
        Channels = AudioChannels.Stereo
    };

    private static ScreenSize GetOutputSize(WorkerRecordingRequest request) => request.Quality switch
    {
        VideoQualityPreset.FullHd => new ScreenSize(1920, 1080),
        VideoQualityPreset.Hd => new ScreenSize(1280, 720),
        VideoQualityPreset.Source => new ScreenSize(
            MakeEven(Math.Max(2, request.CaptureTarget?.Width ?? 1920)),
            MakeEven(Math.Max(2, request.CaptureTarget?.Height ?? 1080))),
        _ => new ScreenSize(1920, 1080)
    };

    private void OnRecorderStatusChanged(object? sender, RecordingStatusEventArgs eventArgs)
    {
        if (eventArgs.Status == RecorderStatus.Recording) _started.TrySetResult();
    }

    private void OnRecorderComplete(object? sender, RecordingCompleteEventArgs eventArgs) => _completed.TrySetResult();

    private void OnRecorderFailed(object? sender, RecordingFailedEventArgs eventArgs)
    {
        var error = new InvalidOperationException(string.IsNullOrWhiteSpace(eventArgs.Error) ? "原生视频引擎报告未知错误。" : eventArgs.Error);
        _failure = error;
        _started.TrySetException(error);
        _completed.TrySetResult();
        if (_isStarted) Faulted?.Invoke(this, new EngineFaultEventArgs(error));
    }

    private void OnPreviewFrameRecorded(object? sender, FrameRecordedEventArgs eventArgs)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref _lastPreviewTimestamp);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(500)) return;
        if (Interlocked.CompareExchange(ref _lastPreviewTimestamp, now, previous) != previous) return;
        var bitmap = eventArgs.BitmapData;
        if (bitmap is null || bitmap.Data == nint.Zero || bitmap.Length <= 0 || bitmap.Width <= 0 || bitmap.Height <= 0) return;
        var bytes = new byte[bitmap.Length];
        Marshal.Copy(bitmap.Data, bytes, 0, bytes.Length);
        PreviewFrameAvailable?.Invoke(this, new PreviewFrameEventArgs(new PreviewFrame(bitmap.Width, bitmap.Height, bitmap.Stride, bytes)));
    }

    private string? GetCaptureTargetWarning()
    {
        var target = _request.CaptureTarget;
        if (target?.Kind != CaptureTargetKind.Window) return null;
        var handle = new nint(target.NativeHandle);
        if (!IsWindow(handle)) return "所选窗口已关闭；录音仍在继续，视频画面可能停留在最后一帧。";
        if (IsIconic(handle)) return "所选窗口已最小化；录音仍在继续，视频画面可能暂停更新。";
        return null;
    }

    private void EnsureActive()
    {
        if (!_isStarted || _isStopped || _recorder is null) throw new InvalidOperationException("录制引擎当前不可操作。");
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
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
            if (_request.EnablePreview) _recorder.OnFrameRecorded -= OnPreviewFrameRecorded;
            _recorder.OnStatusChanged -= OnRecorderStatusChanged;
            _recorder.OnRecordingComplete -= OnRecorderComplete;
            _recorder.OnRecordingFailed -= OnRecorderFailed;
            _recorder.Dispose();
        }
        _source?.Dispose();
        _systemPeak?.Dispose();
        _microphonePeak?.Dispose();
        _operationGate.Dispose();
        _disposed = true;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint windowHandle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint windowHandle);
}
