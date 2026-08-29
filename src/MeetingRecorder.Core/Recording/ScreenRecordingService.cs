using System.Runtime.InteropServices;
using System.Diagnostics;
using MeetingRecorder.Core.Abstractions;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Utilities;
using NAudio.Wave;
using ScreenRecorderLib;
using CoreRecordingMode = MeetingRecorder.Core.Models.RecordingMode;
using LibraryRecordingMode = ScreenRecorderLib.RecorderMode;

namespace MeetingRecorder.Core.Recording;

public sealed partial class ScreenRecordingService : IRecordingService
{
    private readonly IAppRepository _repository;
    private readonly RecordingStateMachine _stateMachine = new();
    private readonly RecordingClock _clock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private Recorder? _recorder;
    private RecordingSourceBase? _recordingSource;
    private BoundedPcmWaveProvider? _pcmProvider;
    private BoundedPcmBuffer? _pcmInputBuffer;
    private Task? _aacEncoderTask;
    private CancellationTokenSource? _audioPumpCancellation;
    private Task? _audioPumpTask;
    private TaskCompletionSource _startSignal = NewSignal();
    private TaskCompletionSource _completionSignal = NewSignal();
    private CancellationTokenSource? _countdownCancellation;
    private CancellationTokenSource? _progressCancellation;
    private Task? _progressTask;
    private RecordingRequest? _request;
    private ActiveSession? _session;
    private DateTimeOffset _startedAt;
    private string? _partialPath;
    private string? _finalPath;
    private string? _recoveryVideoPath;
    private string? _systemAudioSourceId;
    private string? _microphoneSourceId;
    private double _systemLevel;
    private double _microphoneLevel;
    private string? _lastWarning;
    private Exception? _engineFailure;
    private int _faultStopRequested;
    private long _lastPreviewTimestamp;
    private long _audioFramesEmitted;
    private TimeSpan _recordedDuration;
    private bool _disposed;

    public ScreenRecordingService(IAppRepository repository)
    {
        _repository = repository;
    }

    public RecordingState State => _stateMachine.State;
    public string? CurrentOutputPath => _finalPath;

    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;
    public event EventHandler<RecordingProgressEventArgs>? ProgressChanged;
    public event EventHandler<PreviewFrameEventArgs>? PreviewFrameAvailable;
    public event EventHandler<RecordingCompletedEventArgs>? RecordingCompleted;

    public async Task<IReadOnlyList<CaptureTargetInfo>> GetCaptureTargetsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await Task.Run<IReadOnlyList<CaptureTargetInfo>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = new List<CaptureTargetInfo>();
            var displayNumber = 1;
            foreach (var display in Recorder.GetDisplays())
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var measured = TryMeasureSource(display);
                    var width = PositiveDimension(display.OutputSize?.Width ?? 0, display.SourceRect?.Width ?? measured?.Width ?? 0);
                    var height = PositiveDimension(display.OutputSize?.Height ?? 0, display.SourceRect?.Height ?? measured?.Height ?? 0);
                    var name = string.IsNullOrWhiteSpace(display.FriendlyName) ? $"显示器 {displayNumber}" : display.FriendlyName;
                    targets.Add(new CaptureTargetInfo(
                        display.ID ?? display.DeviceName ?? $"display-{displayNumber}",
                        $"🖥 {name}  {FormatDimensions(width, height)}",
                        CaptureTargetKind.Display,
                        display.DeviceName,
                        0,
                        width,
                        height));
                    displayNumber++;
                }
                finally
                {
                    display.Dispose();
                }
            }

            foreach (var window in Recorder.GetWindows())
            {
                try
                {
                    if (window.Handle == nint.Zero || window.Pid == Environment.ProcessId || string.IsNullOrWhiteSpace(window.Title)) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    var measured = TryMeasureSource(window);
                    var width = PositiveDimension(window.OutputSize?.Width ?? 0, window.SourceRect?.Width ?? measured?.Width ?? 0);
                    var height = PositiveDimension(window.OutputSize?.Height ?? 0, window.SourceRect?.Height ?? measured?.Height ?? 0);
                    targets.Add(new CaptureTargetInfo(
                        window.ID ?? $"window-{window.Handle.ToInt64():X}",
                        $"▣ {window.Title}  {FormatDimensions(width, height)}",
                        CaptureTargetKind.Window,
                        null,
                        window.Handle.ToInt64(),
                        width,
                        height,
                        window.Pid));
                }
                finally
                {
                    window.Dispose();
                }
            }
            return targets;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AudioDeviceInfo>> GetAudioDevicesAsync(AudioSourceKind kind, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await Task.Run<IReadOnlyList<AudioDeviceInfo>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kind == AudioSourceKind.SystemAudio)
            {
                return Recorder.GetSystemAudioLoopbackDevices()
                    .Select(device => new AudioDeviceInfo(
                        device.ID,
                        device.DeviceName,
                        device.FriendlyName,
                        kind,
                        device.IsDefaultDevice))
                    .OrderByDescending(device => device.IsDefault)
                    .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            }

            return Recorder.GetSystemAudioCaptureDevices()
                .Select(device => new AudioDeviceInfo(
                    device.ID,
                    device.DeviceName,
                    device.FriendlyName,
                    kind,
                    device.IsDefaultDevice))
                .OrderByDescending(device => device.IsDefault)
                .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(RecordingRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateRequest(request);

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == RecordingState.Failed) ChangeState(RecordingState.Idle);
            if (State != RecordingState.Idle) throw new InvalidOperationException("当前已有录制任务。");
            _request = request;
            _lastWarning = null;
            _engineFailure = null;
            _faultStopRequested = 0;
            _recordedDuration = TimeSpan.Zero;
            _audioFramesEmitted = 0;
            _countdownCancellation = new CancellationTokenSource();
            ChangeState(RecordingState.Countdown);
        }
        finally
        {
            _operationGate.Release();
        }

        using var linkedCountdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _countdownCancellation.Token);
        try
        {
            for (var remaining = request.CountdownSeconds; remaining > 0; remaining--)
            {
                RaiseProgress(new RecordingProgress(TimeSpan.Zero, 0, null, 0, 0, $"{remaining} 秒后开始录制"));
                await Task.Delay(TimeSpan.FromSeconds(1), linkedCountdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            var canceledByStop = _countdownCancellation.IsCancellationRequested;
            await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (State == RecordingState.Countdown) ChangeState(RecordingState.Idle);
            }
            finally
            {
                _operationGate.Release();
            }
            if (!canceledByStop) throw;
            return;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RecordingState.Countdown) return;
            Directory.CreateDirectory(request.OutputDirectory);
            _startedAt = DateTimeOffset.Now;
            (_finalPath, _partialPath, _recoveryVideoPath) = RecordingFileNameFactory.Create(request.OutputDirectory, request.Mode, _startedAt);
            _session = new ActiveSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Mode = request.Mode,
                StartedAt = _startedAt,
                FinalPath = _finalPath,
                PartialPath = _partialPath,
                RecoveryVideoPath = _recoveryVideoPath
            };
            await _repository.SaveActiveSessionAsync(_session, cancellationToken).ConfigureAwait(false);

            var useHardware = request.Mode == CoreRecordingMode.VideoAndAudio && request.PreferHardwareEncoding;
            try
            {
                await StartEngineAsync(request, useHardware, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception firstError) when (useHardware && firstError is not OperationCanceledException)
            {
                _lastWarning = "硬件编码器初始化失败，已自动改用软件编码。";
                RaiseProgress(new RecordingProgress(TimeSpan.Zero, CurrentFileSize(), null, 0, 0, _lastWarning));
                CleanupRecorder();
                DeleteIfEmptyOrPartial(_partialPath);
                _engineFailure = null;
                await StartEngineAsync(request, false, cancellationToken).ConfigureAwait(false);
            }

            _clock.Start();
            ChangeState(RecordingState.Recording);
            if (request.Mode == CoreRecordingMode.AudioOnly)
            {
                _audioPumpCancellation = new CancellationTokenSource();
                _audioPumpTask = RunAudioPumpAsync(_audioPumpCancellation.Token);
            }
            _progressCancellation = new CancellationTokenSource();
            _progressTask = RunProgressLoopAsync(_progressCancellation.Token);
        }
        catch
        {
            CleanupRecorder();
            CompleteAudioProvider();
            if (State == RecordingState.Countdown) ChangeState(RecordingState.Failed);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RecordingState.Recording) throw new InvalidOperationException("当前录制无法暂停。");
            _recorder?.Pause();
            _clock.Pause();
            ChangeState(RecordingState.Paused);
            _pcmInputBuffer?.Clear();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RecordingState.Paused) throw new InvalidOperationException("当前录制无法恢复。");
            _recorder?.Resume();
            _pcmInputBuffer?.Clear();
            _clock.Resume();
            ChangeState(RecordingState.Recording);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (State == RecordingState.Countdown)
        {
            _countdownCancellation?.Cancel();
            return;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == RecordingState.Idle) return;
            if (State == RecordingState.Failed)
            {
                ChangeState(RecordingState.Idle);
                return;
            }
            if (State is not (RecordingState.Recording or RecordingState.Paused)) return;

            _recordedDuration = _clock.Stop();
            ChangeState(RecordingState.Finalizing);
            _progressCancellation?.Cancel();
            _audioPumpCancellation?.Cancel();
            if (_audioPumpTask is not null)
            {
                try { await _audioPumpTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (_request?.Mode == CoreRecordingMode.AudioOnly)
            {
                PumpAudioToDuration(_recordedDuration);
            }
            if (_recorder is not null && _recorder.Status is RecorderStatus.Recording or RecorderStatus.Paused)
            {
                _recorder.Stop();
            }

            Exception? finalizationError = _engineFailure;
            try
            {
                await _completionSignal.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                finalizationError ??= error;
            }

            CompleteAudioProvider();
            if (_aacEncoderTask is not null)
            {
                try
                {
                    await _aacEncoderTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    finalizationError ??= error;
                }
            }

            if (finalizationError is not null)
            {
                await PreserveFailedSessionAsync(finalizationError, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("录制未能正常完成，恢复文件已保留。", finalizationError);
            }

            var historyItem = await FinalizeSuccessfulRecordingAsync(cancellationToken).ConfigureAwait(false);
            CleanupRecorder();
            ChangeState(RecordingState.Idle);
            RecordingCompleted?.Invoke(this, new RecordingCompletedEventArgs(historyItem));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task StartEngineAsync(RecordingRequest request, bool hardwareEncoding, CancellationToken cancellationToken)
    {
        if (_partialPath is null) throw new InvalidOperationException("输出路径尚未初始化。");
        _startSignal = NewSignal();
        _completionSignal = NewSignal();
        _recordingSource = CreateRecordingSource(request);
        if (request.Mode == CoreRecordingMode.VideoAndAudio)
        {
            _recordingSource.OnFrameRecorded += OnPreviewFrameRecorded;
        }

        var options = CreateRecorderOptions(request, _recordingSource, hardwareEncoding);
        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnStatusChanged += OnRecorderStatusChanged;
        _recorder.OnRecordingComplete += OnRecorderComplete;
        _recorder.OnRecordingFailed += OnRecorderFailed;
        _recorder.OnAudioPacketRecorded += OnAudioPacketRecorded;
        if (request.WindowToExclude != nint.Zero)
        {
            Recorder.SetExcludeFromCapture(request.WindowToExclude, true);
        }

        var engineOutput = _partialPath;
        if (request.Mode == CoreRecordingMode.AudioOnly)
        {
            engineOutput = _recoveryVideoPath ?? throw new InvalidOperationException("音频恢复路径尚未初始化。");
            _pcmProvider = new BoundedPcmWaveProvider(capacity: TimeSpan.FromSeconds(10));
            _pcmInputBuffer = new BoundedPcmBuffer(48_000 * 2 * 2 * 5);
            var provider = _pcmProvider;
            var m4aPath = _partialPath;
            _aacEncoderTask = Task.Run(() => MediaFoundationEncoder.EncodeToAac(provider, m4aPath, 128_000), CancellationToken.None);
        }

        _recorder.Record(engineOutput);
        await _startSignal.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
    }

    private RecorderOptions CreateRecorderOptions(RecordingRequest request, RecordingSourceBase source, bool hardwareEncoding)
    {
        var sources = CreateAudioSources(request);
        var outputSize = GetOutputSize(request);
        return new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = [source] },
            OutputOptions = new OutputOptions
            {
                RecorderMode = LibraryRecordingMode.Video,
                OutputFrameSize = outputSize,
                Stretch = StretchMode.Uniform,
                IsVideoCaptureEnabled = true,
                IsVideoFramePreviewEnabled = request.Mode == CoreRecordingMode.VideoAndAudio,
                VideoFramePreviewSize = new ScreenSize(480, 270)
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder
                {
                    EncoderProfile = H264Profile.High,
                    BitrateMode = H264BitrateControlMode.CBR
                },
                Framerate = request.Mode == CoreRecordingMode.AudioOnly ? 1 : 30,
                Bitrate = request.Mode == CoreRecordingMode.AudioOnly ? 100_000 : 6_000_000,
                Quality = request.Mode == CoreRecordingMode.AudioOnly ? 20 : 75,
                IsFixedFramerate = true,
                IsHardwareEncodingEnabled = hardwareEncoding,
                IsLowLatencyEnabled = false,
                IsThrottlingDisabled = false,
                IsFragmentedMp4Enabled = true,
                IsMp4FastStartEnabled = false
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = sources.Count > 0,
                IsAudioPacketPreviewEnabled = sources.Count > 0,
                Bitrate = AudioBitrate.bitrate_128kbps,
                Channels = AudioChannels.Stereo,
                AudioSources = sources,
                MasterVolume = 1f
            }
        };
    }

    private RecordingSourceBase CreateRecordingSource(RecordingRequest request)
    {
        if (request.Mode == CoreRecordingMode.AudioOnly)
        {
            var blackFramePath = BlackFrameAsset.EnsureCreated(_repository.Paths.AssetsDirectory);
            return new ImageRecordingSource(blackFramePath)
            {
                OutputSize = new ScreenSize(64, 64),
                Stretch = StretchMode.Fill,
                IsVideoFramePreviewEnabled = false,
                IsVideoCaptureEnabled = true
            };
        }

        var target = request.CaptureTarget ?? throw new InvalidOperationException("请选择录制目标。");
        if (target.Kind == CaptureTargetKind.Display)
        {
            return new DisplayRecordingSource(target.DeviceName ?? string.Empty)
            {
                IsCursorCaptureEnabled = true,
                IsBorderRequired = false,
                IsVideoCaptureEnabled = true,
                IsVideoFramePreviewEnabled = true,
                VideoFramePreviewSize = new ScreenSize(480, 270),
                Stretch = StretchMode.Uniform
            };
        }

        return new WindowRecordingSource(new nint(target.NativeHandle))
        {
            IsCursorCaptureEnabled = true,
            IsBorderRequired = false,
            IsVideoCaptureEnabled = true,
            IsVideoFramePreviewEnabled = true,
            VideoFramePreviewSize = new ScreenSize(480, 270),
            Stretch = StretchMode.Uniform
        };
    }

    private List<AudioSourceBase> CreateAudioSources(RecordingRequest request)
    {
        var sources = new List<AudioSourceBase>();
        _systemAudioSourceId = null;
        _microphoneSourceId = null;

        if (request.CaptureSystemAudio)
        {
            var source = request.SystemAudioDevice is null
                ? LoopbackAudioSource.Default
                : new LoopbackAudioSource(request.SystemAudioDevice.DeviceName);
            source.Volume = 1f;
            source.IsAudioCaptureEnabled = true;
            _systemAudioSourceId = source.ID;
            sources.Add(source);
        }

        if (request.CaptureMicrophone)
        {
            var source = request.MicrophoneDevice is null
                ? CaptureAudioSource.Default
                : new CaptureAudioSource(request.MicrophoneDevice.DeviceName);
            source.Volume = 1f;
            source.IsAudioCaptureEnabled = true;
            source.ForceMono = true;
            _microphoneSourceId = source.ID;
            sources.Add(source);
        }
        return sources;
    }

    private static ScreenSize GetOutputSize(RecordingRequest request)
    {
        if (request.Mode == CoreRecordingMode.AudioOnly) return new ScreenSize(64, 64);
        return request.Quality switch
        {
            VideoQualityPreset.FullHd => new ScreenSize(1920, 1080),
            VideoQualityPreset.Hd => new ScreenSize(1280, 720),
            VideoQualityPreset.Source => new ScreenSize(
                MakeEven(Math.Max(2, request.CaptureTarget?.Width ?? 1920)),
                MakeEven(Math.Max(2, request.CaptureTarget?.Height ?? 1080))),
            _ => new ScreenSize(1920, 1080)
        };
    }

    private void OnRecorderStatusChanged(object? sender, RecordingStatusEventArgs eventArgs)
    {
        if (eventArgs.Status == RecorderStatus.Recording) _startSignal.TrySetResult();
    }

    private void OnRecorderComplete(object? sender, ScreenRecorderLib.RecordingCompleteEventArgs eventArgs)
    {
        _completionSignal.TrySetResult();
    }

    private void OnRecorderFailed(object? sender, RecordingFailedEventArgs eventArgs)
    {
        var error = new InvalidOperationException(string.IsNullOrWhiteSpace(eventArgs.Error) ? "录制引擎报告未知错误。" : eventArgs.Error);
        _engineFailure = error;
        _startSignal.TrySetException(error);
        _completionSignal.TrySetException(error);
        if (State is RecordingState.Recording or RecordingState.Paused)
        {
            RequestFaultStop("录制引擎发生错误，正在安全收尾。");
        }
    }

    private void OnAudioPacketRecorded(object? sender, AudioDataRecordedEventArgs eventArgs)
    {
        var packet = eventArgs.AudioData;
        if (packet?.Data is { Length: > 0 } mixed)
        {
            if (_request?.Mode == CoreRecordingMode.AudioOnly && _pcmInputBuffer is not null && !_pcmInputBuffer.TryWrite(mixed))
            {
                _engineFailure = new IOException("音频采集缓冲区已满。编码器速度不足，已停止录制以避免内存持续增长。");
                RequestFaultStop(_engineFailure.Message);
                return;
            }

            var sources = packet.Sources;
            if (sources is { Count: > 0 })
            {
                var systemData = sources.FirstOrDefault(source => source.Id == _systemAudioSourceId)?.Data;
                var microphoneData = sources.FirstOrDefault(source => source.Id == _microphoneSourceId)?.Data;
                if (systemData is { Length: > 0 }) _systemLevel = PcmLevelMeter.CalculatePeak(systemData);
                if (microphoneData is { Length: > 0 }) _microphoneLevel = PcmLevelMeter.CalculatePeak(microphoneData);
            }
            else
            {
                var mixedLevel = PcmLevelMeter.CalculatePeak(mixed);
                if (_request?.CaptureSystemAudio == true) _systemLevel = mixedLevel;
                if (_request?.CaptureMicrophone == true) _microphoneLevel = mixedLevel;
            }
        }
    }

    private void OnPreviewFrameRecorded(object? sender, FrameDataRecordedEventArgs eventArgs)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref _lastPreviewTimestamp);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(200)) return;
        if (Interlocked.CompareExchange(ref _lastPreviewTimestamp, now, previous) != previous) return;
        var bitmap = eventArgs.BitmapData;
        if (bitmap is null || bitmap.Data == nint.Zero || bitmap.Length <= 0 || bitmap.Width <= 0 || bitmap.Height <= 0) return;
        var bytes = new byte[bitmap.Length];
        Marshal.Copy(bitmap.Data, bytes, 0, bytes.Length);
        PreviewFrameAvailable?.Invoke(this, new PreviewFrameEventArgs(new PreviewFrame(bitmap.Width, bitmap.Height, bitmap.Stride, bytes)));
    }

    private async Task RunProgressLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (State is not (RecordingState.Recording or RecordingState.Paused)) continue;
                var elapsed = _clock.Elapsed;
                var fileSize = CurrentFileSize();
                var available = GetAvailableDiskBytes(_request?.OutputDirectory);
                var warning = _lastWarning;
                var action = DiskSpacePolicy.Evaluate(available);
                if (action == DiskSpaceAction.Stop)
                {
                    warning = "可用磁盘空间低于 512 MB，正在自动安全停止。";
                    RaiseProgress(new RecordingProgress(elapsed, fileSize, TimeSpan.Zero, _systemLevel, _microphoneLevel, warning));
                    RequestFaultStop(warning, isFailure: false);
                    break;
                }
                if (action == DiskSpaceAction.Warn)
                {
                    warning = "可用磁盘空间低于 5 GB。";
                }

                warning ??= GetCaptureTargetWarning();
                var estimated = EstimateRemainingDiskTime(available, fileSize, elapsed, _request?.Mode ?? CoreRecordingMode.VideoAndAudio);
                RaiseProgress(new RecordingProgress(elapsed, fileSize, estimated, _systemLevel, _microphoneLevel, warning));
                _systemLevel *= 0.7;
                _microphoneLevel *= 0.7;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAudioPumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (State == RecordingState.Recording) PumpAudioToDuration(_clock.Elapsed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void PumpAudioToDuration(TimeSpan duration)
    {
        const int sampleRate = 48_000;
        const int bytesPerFrame = 4;
        const int blockFrames = 960;
        var targetFrames = Math.Max(0, (long)Math.Floor(duration.TotalSeconds * sampleRate));
        while (_audioFramesEmitted < targetFrames)
        {
            var frameCount = (int)Math.Min(blockFrames, targetFrames - _audioFramesEmitted);
            var block = new byte[frameCount * bytesPerFrame];
            _pcmInputBuffer?.ReadAvailable(block);
            if (_pcmProvider is null || !_pcmProvider.TryWrite(block))
            {
                _engineFailure = new IOException("AAC 编码队列已满。磁盘或编码器速度不足，已停止录制以避免内存持续增长。");
                RequestFaultStop(_engineFailure.Message);
                return;
            }
            _audioFramesEmitted += frameCount;
        }
    }

    private string? GetCaptureTargetWarning()
    {
        var target = _request?.CaptureTarget;
        if (target?.Kind != CaptureTargetKind.Window) return null;
        var handle = new nint(target.NativeHandle);
        if (!NativeMethods.IsWindow(handle)) return "所选窗口已关闭；录音仍在继续，视频画面可能停留在最后一帧。";
        if (NativeMethods.IsIconic(handle)) return "所选窗口已最小化；录音仍在继续，视频画面可能暂停更新。";
        return null;
    }

    private async Task<RecordingHistoryItem> FinalizeSuccessfulRecordingAsync(CancellationToken cancellationToken)
    {
        if (_partialPath is null || _finalPath is null || _request is null || _session is null)
        {
            throw new InvalidOperationException("录制会话信息不完整。");
        }
        if (!File.Exists(_partialPath) || new FileInfo(_partialPath).Length == 0)
        {
            throw new InvalidDataException("录制引擎没有生成有效的媒体文件。");
        }

        File.Move(_partialPath, _finalPath, false);
        if (_request.Mode == CoreRecordingMode.AudioOnly &&
            _recoveryVideoPath is not null && File.Exists(_recoveryVideoPath))
        {
            File.Delete(_recoveryVideoPath);
        }

        var duration = _recordedDuration;
        var finishedAt = DateTimeOffset.Now;
        var item = new RecordingHistoryItem
        {
            Id = _session.Id,
            FilePath = _finalPath,
            Mode = _request.Mode,
            StartedAt = _startedAt,
            FinishedAt = finishedAt,
            Duration = duration,
            FileSizeBytes = new FileInfo(_finalPath).Length,
            Warning = _lastWarning
        };
        await _repository.AddHistoryAsync(item, cancellationToken).ConfigureAwait(false);
        await _repository.ClearActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        return item;
    }

    private async Task PreserveFailedSessionAsync(Exception error, CancellationToken cancellationToken)
    {
        _lastWarning = error.Message;
        if (_session is not null)
        {
            await _repository.SaveActiveSessionAsync(_session with { Status = "Failed" }, cancellationToken).ConfigureAwait(false);
        }
        CleanupRecorder();
        ChangeState(RecordingState.Failed);
    }

    private void RequestFaultStop(string warning, bool isFailure = true)
    {
        _lastWarning = warning;
        RaiseProgress(new RecordingProgress(_clock.Elapsed, CurrentFileSize(), null, _systemLevel, _microphoneLevel, warning));
        if (!isFailure) _engineFailure = null;
        if (Interlocked.Exchange(ref _faultStopRequested, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await StopAsync().ConfigureAwait(false); }
            catch { }
        });
    }

    private void CleanupRecorder()
    {
        if (_recordingSource is not null)
        {
            _recordingSource.OnFrameRecorded -= OnPreviewFrameRecorded;
        }
        if (_recorder is not null)
        {
            _recorder.OnStatusChanged -= OnRecorderStatusChanged;
            _recorder.OnRecordingComplete -= OnRecorderComplete;
            _recorder.OnRecordingFailed -= OnRecorderFailed;
            _recorder.OnAudioPacketRecorded -= OnAudioPacketRecorded;
            _recorder.Dispose();
        }
        _recordingSource?.Dispose();
        _recorder = null;
        _recordingSource = null;
        _audioPumpCancellation?.Cancel();
        _audioPumpCancellation?.Dispose();
        _audioPumpCancellation = null;
        _audioPumpTask = null;
        _pcmInputBuffer?.Clear();
        _pcmInputBuffer = null;
        _pcmProvider?.Dispose();
        _pcmProvider = null;
        _aacEncoderTask = null;
    }

    private void CompleteAudioProvider() => _pcmProvider?.Complete();

    private long CurrentFileSize()
    {
        var paths = _request?.Mode == CoreRecordingMode.AudioOnly
            ? new[] { _partialPath, _recoveryVideoPath }
            : new[] { _partialPath };
        long total = 0;
        foreach (var path in paths)
        {
            if (path is null) continue;
            try { if (File.Exists(path)) total += new FileInfo(path).Length; }
            catch (IOException) { }
        }
        return total;
    }

    private void ChangeState(RecordingState next)
    {
        var transition = _stateMachine.TransitionTo(next);
        StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(transition.Previous, transition.Current));
    }

    private void RaiseProgress(RecordingProgress progress) =>
        ProgressChanged?.Invoke(this, new RecordingProgressEventArgs(progress));

    private static void ValidateRequest(RecordingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        if (request.Mode == CoreRecordingMode.VideoAndAudio && request.CaptureTarget is null)
        {
            throw new InvalidOperationException("视频模式必须选择显示器或窗口。");
        }
        if (request.Mode == CoreRecordingMode.AudioOnly && !request.CaptureSystemAudio && !request.CaptureMicrophone)
        {
            throw new InvalidOperationException("仅音频模式至少需要启用一种声音来源。");
        }
        if (request.CountdownSeconds is < 0 or > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "倒计时必须在 0 到 30 秒之间。");
        }
    }

    private static TimeSpan? EstimateRemainingDiskTime(long available, long fileSize, TimeSpan elapsed, CoreRecordingMode mode)
    {
        if (available <= 0) return TimeSpan.Zero;
        var bytesPerSecond = elapsed.TotalSeconds >= 10 && fileSize > 0
            ? fileSize / elapsed.TotalSeconds
            : mode == CoreRecordingMode.AudioOnly ? 16_000d : 780_000d;
        if (bytesPerSecond <= 0) return null;
        return TimeSpan.FromSeconds(Math.Min(available / bytesPerSecond, TimeSpan.MaxValue.TotalSeconds));
    }

    private static long GetAvailableDiskBytes(string? outputDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(outputDirectory)) return long.MaxValue;
            var fullPath = Path.GetFullPath(outputDirectory);
            var root = Path.GetPathRoot(fullPath);
            return string.IsNullOrWhiteSpace(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (IOException) { return long.MaxValue; }
        catch (UnauthorizedAccessException) { return long.MaxValue; }
    }

    private static int PositiveDimension(double primary, double fallback)
    {
        var value = primary > 0 ? primary : fallback;
        return value > 0 ? (int)Math.Round(value) : 0;
    }

    private static ScreenSize? TryMeasureSource(RecordingSourceBase source)
    {
        try { return Recorder.GetOutputDimensionsForRecordingSources([source]).CombinedOutputSize; }
        catch { return null; }
    }

    private static string FormatDimensions(int width, int height) => width > 0 && height > 0 ? $"({width}×{height})" : string.Empty;
    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void DeleteIfEmptyOrPartial(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        File.Delete(path);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (State is RecordingState.Recording or RecordingState.Paused)
        {
            try { await StopAsync().ConfigureAwait(false); } catch { }
        }
        _countdownCancellation?.Cancel();
        _progressCancellation?.Cancel();
        if (_progressTask is not null)
        {
            try { await _progressTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        CleanupRecorder();
        _countdownCancellation?.Dispose();
        _progressCancellation?.Dispose();
        _audioPumpCancellation?.Dispose();
        _operationGate.Dispose();
        _disposed = true;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindow(nint windowHandle);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsIconic(nint windowHandle);
    }
}
