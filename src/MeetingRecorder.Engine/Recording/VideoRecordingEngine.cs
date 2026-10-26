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
    private ScreenRect? _sourceCrop;
    private ScreenSize? _sourceSize;
    private TaskCompletionSource _started = NewSignal();
    private TaskCompletionSource _completed = NewSignal();
    private Exception? _failure;
    private string? _fallbackWarning;
    private volatile bool _microphoneMuted;
    private bool _paused;
    private bool _probeWindowFrames;
    private int _windowImageReceived;
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
    public double MicrophoneLevel => _microphoneMuted ? 0 : _microphonePeak?.ReadPeak() ?? 0;
    public string? Warning => CombineWarnings(GetCaptureTargetWarning(), _fallbackWarning);

    public event EventHandler<PreviewFrameEventArgs>? PreviewFrameAvailable;
    public event EventHandler<EngineFaultEventArgs>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isStarted) throw new InvalidOperationException("录制引擎已经启动。");
            await StartVideoCaptureAsync(cancellationToken).ConfigureAwait(false);
            ApplyMicrophoneInitialMute();
            _clock.Start();
            _isStarted = true;
        }
        finally { _operationGate.Release(); }
    }

    private async Task StartVideoCaptureAsync(CancellationToken cancellationToken)
    {
        var target = _request.CaptureTarget ?? throw new InvalidOperationException("视频模式必须选择显示器或窗口。");
        var lastFailure = default(Exception);

        if (target.Kind == CaptureTargetKind.Window)
        {
            var handle = new nint(target.NativeHandle);
            if (!IsWindow(handle)) throw new WindowCaptureUnavailableException("所选窗口已关闭，无法开始窗口录制。请刷新画面来源后重试。");
            if (IsIconic(handle)) throw new WindowCaptureUnavailableException("所选窗口已最小化，请先恢复窗口再开始录制。");

            try
            {
                await StartRecorderAsync(CreateRecordingSource(_request), null, requireFrames: true, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                lastFailure = error;
            }

            var fallback = TryCreateDisplayFallback(handle);
            if (fallback is not null)
            {
                try
                {
                    await StartRecorderAsync(fallback.Value.Source, fallback.Value.Crop, requireFrames: false, cancellationToken, fallback.Value.Size).ConfigureAwait(false);
                    _fallbackWarning = "窗口捕获不可用，已自动改为录制该窗口所在显示器区域；画面中可能包含同屏的其他内容。";
                    return;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    lastFailure = error;
                }

                try
                {
                    await StartRecorderAsync(fallback.Value.FullDisplaySource, null, requireFrames: false, cancellationToken, fallback.Value.Size).ConfigureAwait(false);
                    _fallbackWarning = "窗口与显示器区域捕获均不可用，已自动改为录制整个显示器。";
                    return;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    lastFailure = error;
                }
            }

            throw new WindowCaptureUnavailableException(
                $"窗口录制不可用：{(fallback is null ? "已尝试窗口捕获" : "已尝试窗口捕获与显示器区域捕获")}（原生错误：{DescribeNativeError(lastFailure)}）。请改为录制整个显示器。",
                lastFailure);
        }

        try
        {
            await StartRecorderAsync(CreateRecordingSource(_request), null, requireFrames: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (_request.UseHardwareEncoding && error is not OperationCanceledException)
        {
            throw new HardwareEncoderUnavailableException("硬件 H.264 编码器初始化失败。", error);
        }
    }

    private async Task StartRecorderAsync(RecordingSourceBase source, ScreenRect? crop, bool requireFrames, CancellationToken cancellationToken, ScreenSize? sourceSize = null)
    {
        DisposeFailedRecorder();
        _source = source;
        _sourceCrop = crop;
        _sourceSize = sourceSize;
        if (sourceSize is null && _request.CaptureTarget is { Kind: CaptureTargetKind.Window } target &&
            GetWindowRect(new nint(target.NativeHandle), out var rect))
        {
            _sourceSize = new ScreenSize(rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        _started = NewSignal();
        _completed = NewSignal();
        _failure = null;
        _probeWindowFrames = requireFrames;
        Interlocked.Exchange(ref _windowImageReceived, 0);
        _recorder = Recorder.CreateRecorder(CreateRecorderOptions(source));
        if (_request.EnablePreview || _probeWindowFrames) _recorder.OnFrameRecorded += OnPreviewFrameRecorded;
        _recorder.OnStatusChanged += OnRecorderStatusChanged;
        _recorder.OnRecordingComplete += OnRecorderComplete;
        _recorder.OnRecordingFailed += OnRecorderFailed;
        try
        {
            _recorder.Record(_request.PartialPath);
            await _started.Task.WaitAsync(TimeSpan.FromSeconds(18), cancellationToken).ConfigureAwait(false);
            await VerifyCaptureHealthAsync(requireFrames, cancellationToken).ConfigureAwait(false);
            if (_probeWindowFrames && !_request.EnablePreview)
            {
                var disabled = _recorder.GetDynamicOptionsBuilder()
                    .SetDynamicOutputOptions(new DynamicOutputOptions { IsVideoFramePreviewEnabled = false }).Apply();
                if (!disabled) throw new WindowCaptureUnavailableException("无法关闭窗口启动验证的临时预览。");
                _recorder.OnFrameRecorded -= OnPreviewFrameRecorded;
            }
            _probeWindowFrames = false;
        }
        catch
        {
            DisposeFailedRecorder();
            DeletePartialOutput();
            throw;
        }
    }

    private async Task VerifyCaptureHealthAsync(bool requireFrames, CancellationToken cancellationToken)
    {
        var window = requireFrames ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(500);
        var start = Stopwatch.GetTimestamp();
        var initialFrame = _recorder!.CurrentFrameNumber;
        while (Stopwatch.GetElapsedTime(start) < window)
        {
            var failure = Volatile.Read(ref _failure);
            if (failure is not null) throw failure;
            if (requireFrames && _recorder.CurrentFrameNumber > initialFrame && Volatile.Read(ref _windowImageReceived) != 0) return;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        var lateFailure = Volatile.Read(ref _failure);
        if (lateFailure is not null) throw lateFailure;
        if (requireFrames)
        {
            throw new WindowCaptureUnavailableException("录制引擎已启动但 2 秒内未产生有效画面（无帧或全黑），判定当前捕获源不可用。");
        }
    }

    private void DisposeFailedRecorder()
    {
        if (_recorder is not null)
        {
            _recorder.OnFrameRecorded -= OnPreviewFrameRecorded;
            _recorder.OnStatusChanged -= OnRecorderStatusChanged;
            _recorder.OnRecordingComplete -= OnRecorderComplete;
            _recorder.OnRecordingFailed -= OnRecorderFailed;
            _recorder.Dispose();
            _recorder = null;
        }
        _source?.Dispose();
        _source = null;
        _sourceCrop = null;
    }

    private void DeletePartialOutput()
    {
        try { if (File.Exists(_request.PartialPath)) File.Delete(_request.PartialPath); }
        catch { }
    }

    private static string DescribeNativeError(Exception? error)
    {
        if (error is null) return "未知";
        var message = error.Message.TrimEnd('。', '.');
        return message == "参数错误" ? "参数错误 0x80070057" : message;
    }

    private static string? CombineWarnings(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return $"{first} {second}";
    }

    private void ApplyMicrophoneInitialMute()
    {
        if (!_request.CaptureMicrophone || !_request.StartMicrophoneMuted) return;
        SetMicrophoneMutedNative(true);
        _microphoneMuted = true;
    }

    public Task SetMicrophoneEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isStarted || _isStopped || _recorder is null) throw new InvalidOperationException("录制引擎当前不可操作。");
        if (!_request.CaptureMicrophone) throw new InvalidOperationException("本次录制未启用麦克风。");
        cancellationToken.ThrowIfCancellationRequested();
        if (!_paused) SetMicrophoneMutedNative(!enabled);
        _microphoneMuted = !enabled;
        return Task.CompletedTask;
    }

    private void SetMicrophoneMutedNative(bool muted)
    {
        var applied = _recorder!.GetDynamicOptionsBuilder()
            .SetDynamicAudioOptions(new DynamicAudioOptions { InputVolume = muted ? 0f : 1f })
            .Apply();
        if (!applied) throw new InvalidOperationException("原生录制引擎拒绝了麦克风开关请求，状态保持不变。");
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            // Keep capture silent across Resume until the queued microphone choice is applied.
            if (_request.CaptureMicrophone) SetMicrophoneMutedNative(true);
            _recorder!.Pause();
            _paused = true;
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
            if (_request.CaptureMicrophone)
                await NativeMicrophoneControl.ApplyAfterResumeAsync(_recorder, _microphoneMuted, cancellationToken).ConfigureAwait(false);
            _paused = false;
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
        OutputOptions = CreateOutputOptions(_request with { EnablePreview = _request.EnablePreview || _probeWindowFrames }, _sourceCrop, _sourceSize),
        VideoEncoderOptions = CreateVideoEncoderOptions(_request),
        AudioOptions = CreateAudioOptions(_request),
        LogOptions = new LogOptions
        {
            IsLogEnabled = true,
            LogFilePath = _request.EngineLogPath,
            LogSeverityLevel = LogLevel.Info
        }
    };

    internal static OutputOptions CreateOutputOptions(WorkerRecordingRequest request, ScreenRect? sourceCrop = null, ScreenSize? sourceSize = null) => new()
    {
        RecorderMode = RecorderMode.Video,
        OutputFrameSize = GetOutputSize(request, sourceCrop, sourceSize),
        Stretch = StretchMode.Uniform,
        IsVideoCaptureEnabled = true,
        SourceRect = sourceCrop,
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

    private (RecordingSourceBase Source, ScreenRect? Crop, RecordingSourceBase FullDisplaySource, ScreenSize Size)? TryCreateDisplayFallback(nint windowHandle)
    {
        try
        {
            if (!GetWindowRect(windowHandle, out var windowRect)) return null;
            var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
            if (monitor == nint.Zero) return null;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return null;

            var deviceName = info.DeviceName;
            if (string.IsNullOrWhiteSpace(deviceName) || !deviceName.StartsWith(@"\\.\", StringComparison.Ordinal)) return null;

            var crop = WindowCaptureFallback.ComputeDisplayCrop(
                windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom,
                info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);

            RecordingSourceBase CreateDisplaySource() => new DisplayRecordingSource(deviceName)
            {
                IsCursorCaptureEnabled = true,
                IsBorderRequired = false,
                IsVideoCaptureEnabled = true,
                IsVideoFramePreviewEnabled = false,
                Stretch = StretchMode.Uniform
            };

            if (crop is null)
            {
                return (CreateDisplaySource(), null, CreateDisplaySource(), new ScreenSize(info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top));
            }

            var rect = new ScreenRect(crop.Left, crop.Top, crop.Width, crop.Height);
            return (CreateDisplaySource(), rect, CreateDisplaySource(), new ScreenSize(info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top));
        }
        catch
        {
            return null;
        }
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
        InputVolume = request.StartMicrophoneMuted ? 0f : 1f,
        Bitrate = AudioBitrate.bitrate_128kbps,
        Channels = AudioChannels.Stereo
    };

    private static ScreenSize GetOutputSize(WorkerRecordingRequest request, ScreenRect? crop, ScreenSize? sourceSize)
    {
        var width = Math.Max(2, crop?.Width ?? sourceSize?.Width ?? request.CaptureTarget?.Width ?? 1920);
        var height = Math.Max(2, crop?.Height ?? sourceSize?.Height ?? request.CaptureTarget?.Height ?? 1080);
        var maxHeight = request.Quality switch
        {
            VideoQualityPreset.Hd => 720,
            VideoQualityPreset.Source => height,
            _ => 1080
        };
        var scale = Math.Min(1, maxHeight / (double)height);
        return new ScreenSize(Math.Max(2, MakeEven((int)(width * scale))), Math.Max(2, MakeEven((int)(height * scale))));
    }

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
        if (_probeWindowFrames && HasVisiblePixels(bytes)) Interlocked.Exchange(ref _windowImageReceived, 1);
        if (_request.EnablePreview)
            PreviewFrameAvailable?.Invoke(this, new PreviewFrameEventArgs(new PreviewFrame(bitmap.Width, bitmap.Height, bitmap.Stride, bytes)));
    }

    internal static bool HasVisiblePixels(ReadOnlySpan<byte> bgra)
    {
        // Ignore alpha: an opaque black frame is not proof of a usable window capture.
        for (var offset = 0; offset + 3 < bgra.Length; offset += 4)
            if (bgra[offset] > 3 || bgra[offset + 1] > 3 || bgra[offset + 2] > 3) return true;
        return false;
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
            _recorder.OnFrameRecorded -= OnPreviewFrameRecorded;
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

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint windowHandle, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint windowHandle, uint flags);

    // LibraryImport 源生成不支持含定长字符串的结构体封送，此调用沿用经典 DllImport。
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitorHandle, ref MonitorInfo monitorInfo);

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }
}
