using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Protocol;
using MeetingRecorder.Core.Utilities;
using MeetingRecorder.Engine.Abstractions;
using MeetingRecorder.Engine.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingRecorder.Engine.Recording;

public sealed class AudioOnlyRecordingEngine : IRecordingEngine
{
    private const int SampleRate = 48_000;
    private const int BytesPerFrame = 4;
    private const int FramesPerBlock = 960;
    private const int BytesPerBlock = FramesPerBlock * BytesPerFrame;
    private readonly WorkerRecordingRequest _request;
    private readonly RecordingClock _clock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly BoundedPcmWaveProvider _aacProvider = new(capacity: TimeSpan.FromSeconds(10));
    private readonly BoundedPcmWaveProvider _mp3Provider = new(capacity: TimeSpan.FromSeconds(10));
    private readonly Pcm16AutomaticGainControl _systemAudioGain = new();
    private readonly byte[] _systemBlock = new byte[BytesPerBlock];
    private readonly byte[] _microphoneBlock = new byte[BytesPerBlock];
    private readonly byte[] _mixedBlock = new byte[BytesPerBlock];
    private AudioCaptureSource? _systemSource;
    private AudioCaptureSource? _microphoneSource;
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pumpTask;
    private Task? _aacEncoderTask;
    private Task? _mp3EncoderTask;
    private long _framesEmitted;
    private double _systemLevel;
    private double _microphoneLevel;
    private Exception? _failure;
    private volatile bool _paused;
    private bool _started;
    private bool _stopRequested;
    private bool _stopped;
    private bool _disposed;
    private int _faultRaised;

    public AudioOnlyRecordingEngine(WorkerRecordingRequest request) => _request = request;

    public TimeSpan Elapsed => _clock.Elapsed;
    public double SystemAudioLevel => Volatile.Read(ref _systemLevel);
    public double MicrophoneLevel => Volatile.Read(ref _microphoneLevel);
    public string? Warning => Volatile.Read(ref _failure)?.Message;

    public event EventHandler<PreviewFrameEventArgs>? PreviewFrameAvailable;
    public event EventHandler<EngineFaultEventArgs>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started) throw new InvalidOperationException("录音引擎已经启动。");
            if (!_request.CaptureSystemAudio && !_request.CaptureMicrophone)
            {
                throw new InvalidOperationException("仅音频模式至少需要启用一种声音来源。");
            }
            if (string.IsNullOrWhiteSpace(_request.RecoveryMediaPath))
            {
                throw new InvalidOperationException("未提供音频恢复文件路径。");
            }

            if (_request.CaptureSystemAudio)
            {
                _systemSource = new AudioCaptureSource(_request.SystemAudioDevice?.Id, DataFlow.Render);
                _systemSource.Faulted += OnCaptureFaulted;
            }
            if (_request.CaptureMicrophone)
            {
                _microphoneSource = new AudioCaptureSource(_request.MicrophoneDevice?.Id, DataFlow.Capture);
                _microphoneSource.Faulted += OnCaptureFaulted;
            }

            try
            {
                _systemSource?.Start();
                _microphoneSource?.Start();
            }
            catch (Exception error)
            {
                throw new AudioCaptureInitializationException("NAudio 无法初始化所选音频端点。", error);
            }

            _aacEncoderTask = Task.Run(() => MediaFoundationEncoder.EncodeToAac(_aacProvider, _request.PartialPath, 128_000), CancellationToken.None);
            _mp3EncoderTask = Task.Run(() => MediaFoundationEncoder.EncodeToMp3(_mp3Provider, _request.RecoveryMediaPath, 96_000), CancellationToken.None);
            ObserveEncoder(_aacEncoderTask, "AAC/M4A");
            ObserveEncoder(_mp3EncoderTask, "MP3 恢复");
            _clock.Start();
            _pumpCancellation = new CancellationTokenSource();
            _pumpTask = RunPumpAsync(_pumpCancellation.Token);
            _started = true;
            await Task.Yield();
            if (_aacEncoderTask.IsFaulted) await _aacEncoderTask.ConfigureAwait(false);
            if (_mp3EncoderTask.IsFaulted) await _mp3EncoderTask.ConfigureAwait(false);
        }
        catch
        {
            _aacProvider.Complete();
            _mp3Provider.Complete();
            throw;
        }
        finally { _operationGate.Release(); }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            _paused = true;
            _clock.Pause();
            _systemSource?.Pause();
            _microphoneSource?.Pause();
            Volatile.Write(ref _systemLevel, 0);
            Volatile.Write(ref _microphoneLevel, 0);
        }
        finally { _operationGate.Release(); }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            _systemSource?.Resume();
            _microphoneSource?.Resume();
            _systemAudioGain.Reset();
            _clock.Resume();
            _paused = false;
        }
        finally { _operationGate.Release(); }
    }

    public async Task<TimeSpan> StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped) return _clock.Elapsed;
            EnsureActive();
            if (!_stopRequested)
            {
                _stopRequested = true;
                _paused = true;
                var duration = _clock.Stop();
                try { _systemSource?.Stop(); }
                catch (Exception error) { RaiseFault(error); }
                try { _microphoneSource?.Stop(); }
                catch (Exception error) { RaiseFault(error); }
                _pumpCancellation?.Cancel();
                if (_pumpTask is not null)
                {
                    try { await _pumpTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
                try { PumpToDuration(duration); }
                catch (Exception error) { RaiseFault(error); }
                finally
                {
                    _aacProvider.Complete();
                    _mp3Provider.Complete();
                }
            }
            if (_aacEncoderTask is not null) await _aacEncoderTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_mp3EncoderTask is not null) await _mp3EncoderTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            var failure = Volatile.Read(ref _failure);
            if (failure is not null) throw new InvalidOperationException("音频录制未能正常完成。", failure);
            _stopped = true;
            return _clock.Elapsed;
        }
        finally { _operationGate.Release(); }
    }

    private async Task RunPumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_paused) PumpToDuration(_clock.Elapsed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { RaiseFault(error); }
    }

    private void PumpToDuration(TimeSpan duration)
    {
        var targetFrames = Math.Max(0, (long)Math.Floor(duration.TotalSeconds * SampleRate));
        while (_framesEmitted < targetFrames)
        {
            var frameCount = (int)Math.Min(FramesPerBlock, targetFrames - _framesEmitted);
            var byteCount = frameCount * BytesPerFrame;
            var system = _systemBlock.AsSpan(0, byteCount);
            var microphone = _microphoneBlock.AsSpan(0, byteCount);
            var mixed = _mixedBlock.AsSpan(0, byteCount);
            _systemSource?.Read(_systemBlock, byteCount);
            _microphoneSource?.Read(_microphoneBlock, byteCount);
            if (_systemSource is not null) _systemAudioGain.ProcessInPlace(system);
            Volatile.Write(ref _systemLevel, _systemSource is null ? 0 : PcmLevelMeter.CalculatePeak(system));
            Volatile.Write(ref _microphoneLevel, _microphoneSource is null ? 0 : PcmLevelMeter.CalculatePeak(microphone));
            Pcm16StereoMixer.Mix(system, microphone, mixed, _systemSource is not null, _microphoneSource is not null);
            if (!_aacProvider.TryWrite(mixed) || !_mp3Provider.TryWrite(mixed))
            {
                throw new AudioBufferOverflowException("音频编码缓冲区已满，已停止录制以防止内存持续增长。");
            }
            _framesEmitted += frameCount;
        }
    }

    private void ObserveEncoder(Task encoderTask, string name) => _ = encoderTask.ContinueWith(
        task => RaiseFault(new InvalidOperationException($"{name} 编码器异常退出。", task.Exception?.GetBaseException())),
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private void OnCaptureFaulted(object? sender, Exception error) => RaiseFault(error);

    private void RaiseFault(Exception error)
    {
        Interlocked.CompareExchange(ref _failure, error, null);
        if (Interlocked.Exchange(ref _faultRaised, 1) == 0) Faulted?.Invoke(this, new EngineFaultEventArgs(error));
    }

    private void EnsureActive()
    {
        if (!_started || _stopped) throw new InvalidOperationException("录音引擎当前不可操作。");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_started && !_stopped)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await StopAsync(timeout.Token).ConfigureAwait(false); }
            catch { }
        }
        _pumpCancellation?.Cancel();
        _systemSource?.Dispose();
        _microphoneSource?.Dispose();
        _aacProvider.Dispose();
        _mp3Provider.Dispose();
        _pumpCancellation?.Dispose();
        _operationGate.Dispose();
        _disposed = true;
        GC.KeepAlive(PreviewFrameAvailable);
    }
}
