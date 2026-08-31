using System.Reflection;
using MeetingRecorder.Core.Abstractions;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Protocol;
using MeetingRecorder.Core.Utilities;

namespace MeetingRecorder.Core.Recording;

public sealed class WorkerRecordingService : IRecordingService
{
    private readonly IAppRepository _repository;
    private readonly IWorkerProcessLauncher _launcher;
    private readonly RollingFileLogger _logger;
    private readonly WorkerTimeoutOptions _timeouts;
    private readonly IRecordingPresentationCoordinator _presentationCoordinator;
    private readonly RecordingStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _discoveryGate = new();
    private CancellationTokenSource? _countdownCancellation;
    private Task<WorkerDiscoveryResult>? _discoveryTask;
    private DateTimeOffset _discoveryCreatedAt;
    private WorkerClientSession? _worker;
    private RecordingRequest? _request;
    private ActiveSession? _activeSession;
    private DateTimeOffset _startedAt;
    private string? _partialPath;
    private string? _finalPath;
    private string? _recoveryMediaPath;
    private string? _lastWarning;
    private bool _captureProtectionApplied;
    private bool _enablePreview;
    private int _generation;
    private int _terminal;
    private int _presentationPrepared;
    private bool _disposed;

    public WorkerRecordingService(
        IAppRepository repository,
        IWorkerProcessLauncher? launcher = null,
        RollingFileLogger? logger = null,
        WorkerTimeoutOptions? timeouts = null,
        IRecordingPresentationCoordinator? presentationCoordinator = null)
    {
        _repository = repository;
        _launcher = launcher ?? new WorkerProcessLauncher();
        _logger = logger ?? new RollingFileLogger(Path.Combine(repository.Paths.LogsDirectory, "app.log"));
        _timeouts = timeouts ?? WorkerTimeoutOptions.Default;
        _presentationCoordinator = presentationCoordinator ?? new SafeRecordingPresentationCoordinator();
        _timeouts.Validate();
        _logger.Info("service-created", $"version={Assembly.GetExecutingAssembly().GetName().Version}; os={Environment.OSVersion.VersionString}; x64={Environment.Is64BitProcess}");
    }

    public RecordingState State => _stateMachine.State;
    public string? CurrentOutputPath => _finalPath;

    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;
    public event EventHandler<RecordingProgressEventArgs>? ProgressChanged;
    public event EventHandler<PreviewFrameEventArgs>? PreviewFrameAvailable;
    public event EventHandler<RecordingCompletedEventArgs>? RecordingCompleted;

    public async Task<IReadOnlyList<CaptureTargetInfo>> GetCaptureTargetsAsync(CancellationToken cancellationToken = default) =>
        (await GetDiscoveryAsync(cancellationToken).ConfigureAwait(false)).CaptureTargets;

    public async Task<IReadOnlyList<AudioDeviceInfo>> GetAudioDevicesAsync(AudioSourceKind kind, CancellationToken cancellationToken = default)
    {
        var result = await GetDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        return kind == AudioSourceKind.SystemAudio ? result.SystemAudioDevices : result.MicrophoneDevices;
    }

    public async Task StartAsync(RecordingRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRequest(request);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == RecordingState.Failed) ChangeState(RecordingState.Idle);
            if (State != RecordingState.Idle) throw new InvalidOperationException("当前已有录制任务。");
            _request = request;
            _lastWarning = null;
            _terminal = 0;
            _countdownCancellation = new CancellationTokenSource();
            ChangeState(RecordingState.Countdown);
        }
        finally { _operationGate.Release(); }

        using var countdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _countdownCancellation.Token);
        try
        {
            for (var remaining = request.CountdownSeconds; remaining > 0; remaining--)
            {
                RaiseProgress(new RecordingProgress(TimeSpan.Zero, 0, null, 0, 0, $"{remaining} 秒后开始录制"));
                await Task.Delay(TimeSpan.FromSeconds(1), countdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_countdownCancellation.IsCancellationRequested)
        {
            await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { if (State == RecordingState.Countdown) ChangeState(RecordingState.Idle); }
            finally { _operationGate.Release(); }
            return;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RecordingState.Countdown) return;
            var presentation = await PreparePresentationAsync(request, cancellationToken).ConfigureAwait(false);
            _captureProtectionApplied = presentation.CaptureProtectionApplied;
            _enablePreview = presentation.EnablePreview;
            if (!string.IsNullOrWhiteSpace(presentation.Warning))
            {
                _lastWarning = presentation.Warning;
                RaiseProgress(new RecordingProgress(TimeSpan.Zero, 0, null, 0, 0, _lastWarning));
            }
            Directory.CreateDirectory(request.OutputDirectory);
            _startedAt = DateTimeOffset.Now;
            var paths = RecordingFileNameFactory.Create(request.OutputDirectory, request.Mode, _startedAt);
            _finalPath = paths.FinalPath;
            _partialPath = paths.PartialPath;
            _recoveryMediaPath = paths.RecoveryMediaPath;
            _activeSession = new ActiveSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Mode = request.Mode,
                StartedAt = _startedAt,
                FinalPath = _finalPath,
                PartialPath = _partialPath,
                RecoveryMediaPath = _recoveryMediaPath
            };
            await _repository.SaveActiveSessionAsync(_activeSession, cancellationToken).ConfigureAwait(false);
            _logger.Info("recording-start", $"session={_activeSession.Id}; mode={request.Mode}; target={request.CaptureTarget?.Kind}; systemAudio={request.CaptureSystemAudio}; microphone={request.CaptureMicrophone}; hardware={request.PreferHardwareEncoding}; quality={request.Quality}; captureProtectionRequested={request.ExcludeMainWindowFromDisplayCapture}; captureProtectionApplied={_captureProtectionApplied}; preview={_enablePreview}");

            WorkerClientSession session;
            var useHardware = request.Mode == RecordingMode.VideoAndAudio && request.PreferHardwareEncoding;
            try
            {
                session = await LaunchRecordingWorkerAsync(request, useHardware, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkerReportedException error) when (useHardware && error.Error.Kind == WorkerErrorKind.HardwareEncoderUnavailable)
            {
                _lastWarning = CombineWarnings(_lastWarning, "硬件编码器初始化失败，已在全新的 Worker 中改用软件编码。");
                RaiseProgress(new RecordingProgress(TimeSpan.Zero, CurrentFileSize(), null, 0, 0, _lastWarning));
                DeleteRetryOutput(_partialPath);
                session = await LaunchRecordingWorkerAsync(request, false, cancellationToken).ConfigureAwait(false);
            }

            _worker = session;
            _generation++;
            ChangeState(RecordingState.Recording);
            _ = MonitorWorkerAsync(session, _generation);
        }
        catch (Exception error)
        {
            await PreserveFailureAsync(error, TryGetExitCode(_worker), CancellationToken.None).ConfigureAwait(false);
            throw CreateUserFacingException(error);
        }
        finally { _operationGate.Release(); }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RecordingState.Recording || _worker is null) throw new InvalidOperationException("当前录制无法暂停。");
            await _worker.WaitForStateAsync(
                RecordingState.Paused,
                () => _worker.SendAsync(IpcControlMessage.Empty(IpcMessageTypes.Pause), cancellationToken),
                _timeouts.Command,
                cancellationToken).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RecordingState.Paused || _worker is null) throw new InvalidOperationException("当前录制无法恢复。");
            await _worker.WaitForStateAsync(
                RecordingState.Recording,
                () => _worker.SendAsync(IpcControlMessage.Empty(IpcMessageTypes.Resume), cancellationToken),
                _timeouts.Command,
                cancellationToken).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
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
                await RestorePresentationSafeAsync().ConfigureAwait(false);
                ChangeState(RecordingState.Idle);
                return;
            }
            if (State is not (RecordingState.Recording or RecordingState.Paused or RecordingState.Finalizing) || _worker is null) return;
            if (State != RecordingState.Finalizing) ChangeState(RecordingState.Finalizing);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_timeouts.Finalization);
                await _worker.SendAsync(IpcControlMessage.Empty(IpcMessageTypes.Stop), timeout.Token).ConfigureAwait(false);
                var winner = await Task.WhenAny(_worker.Completion, _worker.Error, _worker.Exit).WaitAsync(timeout.Token).ConfigureAwait(false);
                if (winner == _worker.Error) throw new WorkerReportedException(await _worker.Error.ConfigureAwait(false));
                if (winner == _worker.Exit)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    if (_worker.Completion.IsCompletedSuccessfully)
                    {
                        winner = _worker.Completion;
                    }
                    else
                    {
                        throw new WorkerExitedException(await _worker.Exit.ConfigureAwait(false));
                    }
                }
                var completed = await _worker.Completion.ConfigureAwait(false);
                Interlocked.Exchange(ref _terminal, 1);
                var history = await FinalizeSuccessfulRecordingAsync(completed, cancellationToken).ConfigureAwait(false);
                await DisposeWorkerAsync().ConfigureAwait(false);
                await RestorePresentationSafeAsync().ConfigureAwait(false);
                ChangeState(RecordingState.Idle);
                RecordingCompleted?.Invoke(this, new RecordingCompletedEventArgs(history));
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _terminal, 1);
                _worker.Kill();
                var timeoutError = new TimeoutException("Worker 在 2 分钟内未完成安全收尾，已终止并保留恢复文件。", error);
                await PreserveFailureAsync(timeoutError, TryGetExitCode(_worker), CancellationToken.None).ConfigureAwait(false);
                await DisposeWorkerAsync().ConfigureAwait(false);
                throw timeoutError;
            }
            catch (Exception error)
            {
                Interlocked.Exchange(ref _terminal, 1);
                var exitCode = TryGetExitCode(_worker);
                await PreserveFailureAsync(error, exitCode, CancellationToken.None).ConfigureAwait(false);
                await DisposeWorkerAsync().ConfigureAwait(false);
                throw CreateUserFacingException(error);
            }
        }
        finally { _operationGate.Release(); }
    }

    private Task<WorkerDiscoveryResult> GetDiscoveryAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_discoveryGate)
        {
            if (_discoveryTask is null || _discoveryTask.IsFaulted || _discoveryTask.IsCanceled ||
                (_discoveryTask.IsCompleted && DateTimeOffset.UtcNow - _discoveryCreatedAt > TimeSpan.FromSeconds(2)))
            {
                _discoveryCreatedAt = DateTimeOffset.UtcNow;
                _discoveryTask = RunDiscoveryAsync(cancellationToken);
            }
            return _discoveryTask;
        }
    }

    private async Task<WorkerDiscoveryResult> RunDiscoveryAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeouts.Discovery);
        WorkerClientSession? worker = null;
        try
        {
            worker = await WorkerClientSession.LaunchAsync(_launcher, WorkerMode.Discovery, _logger, _repository.Paths.LogsDirectory, _timeouts.Discovery, timeout.Token).ConfigureAwait(false);
            await worker.SendAsync(IpcControlMessage.Create(IpcMessageTypes.Discover, new WorkerDiscoveryRequest(Environment.ProcessId)), timeout.Token).ConfigureAwait(false);
            var result = await worker.WaitForDiscoveryAsync(_timeouts.Discovery, timeout.Token).ConfigureAwait(false);
            foreach (var warning in result.Warnings) _logger.Warning("device-discovery", warning);
            return result;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            worker?.Kill();
            throw new TimeoutException("设备检查超过 15 秒。录制引擎可能无响应，请稍后重试。", error);
        }
        catch (Exception error)
        {
            _logger.Error("device-discovery", "设备检查失败。", error);
            throw new InvalidOperationException($"无法检查录制设备：{CreateUserFacingException(error).Message}", error);
        }
        finally
        {
            if (worker is not null)
            {
                try { await worker.Exit.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { worker.Kill(); }
                await worker.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<WorkerClientSession> LaunchRecordingWorkerAsync(RecordingRequest request, bool useHardware, CancellationToken cancellationToken)
    {
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(_timeouts.Startup);
        WorkerClientSession? worker = null;
        try
        {
            worker = await WorkerClientSession.LaunchAsync(_launcher, WorkerMode.Recording, _logger, _repository.Paths.LogsDirectory, _timeouts.Startup, startupTimeout.Token).ConfigureAwait(false);
            HookWorker(worker);
            var payload = new WorkerRecordingRequest
            {
                SessionId = _activeSession?.Id ?? throw new InvalidOperationException("录制会话尚未创建。"),
                Mode = request.Mode,
                CaptureTarget = request.CaptureTarget,
                SystemAudioDevice = request.SystemAudioDevice,
                MicrophoneDevice = request.MicrophoneDevice,
                CaptureSystemAudio = request.CaptureSystemAudio,
                CaptureMicrophone = request.CaptureMicrophone,
                Quality = request.Quality,
                PartialPath = _partialPath ?? throw new InvalidOperationException("临时输出路径尚未创建。"),
                RecoveryMediaPath = _recoveryMediaPath,
                UseHardwareEncoding = useHardware,
                EnablePreview = _enablePreview,
                EngineLogPath = Path.Combine(_repository.Paths.LogsDirectory, "engine-native.log")
            };
            await worker.SendAsync(IpcControlMessage.Create(IpcMessageTypes.Start, payload), startupTimeout.Token).ConfigureAwait(false);
            await worker.WaitForStartedAsync(_timeouts.Startup, startupTimeout.Token).ConfigureAwait(false);
            return worker;
        }
        catch (Exception error)
        {
            if (worker is not null)
            {
                UnhookWorker(worker);
                worker.Kill();
                await worker.DisposeAsync().ConfigureAwait(false);
            }
            if (error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"录制 Worker 在 {_timeouts.Startup.TotalSeconds:0.#} 秒内未完成启动。请检查日志后重试。", error);
            }
            throw;
        }
    }

    private void HookWorker(WorkerClientSession worker)
    {
        worker.StateReceived += OnWorkerStateReceived;
        worker.ProgressReceived += OnWorkerProgressReceived;
        worker.PreviewReceived += OnWorkerPreviewReceived;
    }

    private void UnhookWorker(WorkerClientSession worker)
    {
        worker.StateReceived -= OnWorkerStateReceived;
        worker.ProgressReceived -= OnWorkerProgressReceived;
        worker.PreviewReceived -= OnWorkerPreviewReceived;
    }

    private void OnWorkerStateReceived(object? sender, RecordingState state)
    {
        try
        {
            if (state == RecordingState.Paused && State == RecordingState.Recording) ChangeState(RecordingState.Paused);
            else if (state == RecordingState.Recording && State == RecordingState.Paused) ChangeState(RecordingState.Recording);
            else if (state == RecordingState.Finalizing && State is RecordingState.Recording or RecordingState.Paused) ChangeState(RecordingState.Finalizing);
        }
        catch (InvalidOperationException error) { _logger.Error("state-update", "忽略无效的 Worker 状态变化。", error); }
    }

    private void OnWorkerProgressReceived(object? sender, RecordingProgress progress)
    {
        if (_lastWarning is not null && string.IsNullOrWhiteSpace(progress.Warning)) progress = progress with { Warning = _lastWarning };
        RaiseProgress(progress);
    }

    private void OnWorkerPreviewReceived(object? sender, PreviewFrame preview) =>
        PreviewFrameAvailable?.Invoke(this, new PreviewFrameEventArgs(preview));

    private async Task MonitorWorkerAsync(WorkerClientSession worker, int generation)
    {
        var winner = await Task.WhenAny(worker.Completion, worker.Error, worker.Exit).ConfigureAwait(false);
        if (generation != _generation || Volatile.Read(ref _terminal) != 0) return;
        if (winner == worker.Exit)
        {
            await Task.Delay(50).ConfigureAwait(false);
            if (worker.Completion.IsCompletedSuccessfully) winner = worker.Completion;
            else if (worker.Error.IsCompletedSuccessfully) winner = worker.Error;
        }

        if (winner == worker.Completion)
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (generation != _generation || Interlocked.Exchange(ref _terminal, 1) != 0) return;
                if (State is RecordingState.Recording or RecordingState.Paused) ChangeState(RecordingState.Finalizing);
                try
                {
                    var history = await FinalizeSuccessfulRecordingAsync(await worker.Completion.ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
                    await DisposeWorkerAsync().ConfigureAwait(false);
                    await RestorePresentationSafeAsync().ConfigureAwait(false);
                    ChangeState(RecordingState.Idle);
                    RecordingCompleted?.Invoke(this, new RecordingCompletedEventArgs(history));
                }
                catch (Exception completionError)
                {
                    await PreserveFailureAsync(completionError, worker.Exit.IsCompletedSuccessfully ? await worker.Exit.ConfigureAwait(false) : null, CancellationToken.None).ConfigureAwait(false);
                    await DisposeWorkerAsync().ConfigureAwait(false);
                }
            }
            finally { _operationGate.Release(); }
            return;
        }

        Exception error;
        int? exitCode = null;
        if (winner == worker.Error)
        {
            error = new WorkerReportedException(await worker.Error.ConfigureAwait(false));
            if (worker.Exit.IsCompletedSuccessfully) exitCode = await worker.Exit.ConfigureAwait(false);
        }
        else
        {
            exitCode = await worker.Exit.ConfigureAwait(false);
            if (worker.Error.IsCompletedSuccessfully)
            {
                error = new WorkerReportedException(await worker.Error.ConfigureAwait(false));
            }
            else
            {
                error = new WorkerExitedException(exitCode.Value);
            }
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != _generation || Interlocked.Exchange(ref _terminal, 1) != 0) return;
            await PreserveFailureAsync(error, exitCode, CancellationToken.None).ConfigureAwait(false);
            await DisposeWorkerAsync().ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    private async Task<RecordingHistoryItem> FinalizeSuccessfulRecordingAsync(WorkerCompleted completed, CancellationToken cancellationToken)
    {
        if (_activeSession is null || _request is null || _partialPath is null || _finalPath is null)
        {
            throw new InvalidOperationException("录制会话信息不完整。");
        }
        if (!File.Exists(_partialPath) || new FileInfo(_partialPath).Length == 0)
        {
            throw new InvalidDataException("Worker 没有生成有效的媒体文件。");
        }
        File.Move(_partialPath, _finalPath, false);
        if (_request.Mode == RecordingMode.AudioOnly && _recoveryMediaPath is not null && File.Exists(_recoveryMediaPath))
        {
            File.Delete(_recoveryMediaPath);
        }
        var item = new RecordingHistoryItem
        {
            Id = _activeSession.Id,
            FilePath = _finalPath,
            Mode = _request.Mode,
            StartedAt = _startedAt,
            FinishedAt = DateTimeOffset.Now,
            Duration = completed.Duration,
            FileSizeBytes = new FileInfo(_finalPath).Length,
            Warning = completed.Warning ?? _lastWarning
        };
        await _repository.AddHistoryAsync(item, cancellationToken).ConfigureAwait(false);
        await _repository.ClearActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        _logger.Info("recording-complete", $"session={item.Id}; durationMs={(long)item.Duration.TotalMilliseconds}; bytes={item.FileSizeBytes}");
        return item;
    }

    private async Task PreserveFailureAsync(Exception error, int? exitCode, CancellationToken cancellationToken)
    {
        _lastWarning = BuildFailureMessage(error, exitCode);
        _logger.Error("recording-failed", _lastWarning, error);
        await RestorePresentationSafeAsync().ConfigureAwait(false);
        if (_activeSession is not null)
        {
            await _repository.SaveActiveSessionAsync(_activeSession with
            {
                Status = "Failed",
                WorkerExitCode = exitCode,
                FailureMessage = _lastWarning
            }, cancellationToken).ConfigureAwait(false);
        }
        if (State != RecordingState.Failed) ChangeState(RecordingState.Failed);
        RaiseProgress(new RecordingProgress(TimeSpan.Zero, CurrentFileSize(), null, 0, 0, _lastWarning));
    }

    private async Task DisposeWorkerAsync()
    {
        var worker = _worker;
        _worker = null;
        if (worker is null) return;
        UnhookWorker(worker);
        await worker.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<RecordingPresentationDecision> PreparePresentationAsync(
        RecordingRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _presentationPrepared, 1);
        try
        {
            return await _presentationCoordinator.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RestorePresentationSafeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            _logger.Warning("capture-protection", $"录制显示准备失败，已按无防捕获、无整屏预览继续：{error.Message}");
            await RestorePresentationSafeAsync().ConfigureAwait(false);
            return new RecordingPresentationDecision(
                false,
                false,
                $"无法启用录制界面防捕获，已关闭整屏实时预览并继续录制：{error.Message}");
        }
    }

    private async Task RestorePresentationSafeAsync()
    {
        if (Interlocked.Exchange(ref _presentationPrepared, 0) == 0) return;
        try
        {
            await _presentationCoordinator.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.Info("capture-protection", "录制显示状态已恢复。");
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _presentationPrepared, 1);
            _logger.Warning("capture-protection", $"恢复录制显示状态失败，将在退出时重试：{error.Message}");
        }
        finally
        {
            _captureProtectionApplied = false;
            _enablePreview = false;
        }
    }

    private int? TryGetExitCode(WorkerClientSession? worker) =>
        worker?.Exit.IsCompletedSuccessfully == true ? worker.Exit.Result : null;

    private long CurrentFileSize()
    {
        long total = 0;
        foreach (var path in new[] { _partialPath, _recoveryMediaPath })
        {
            try { if (path is not null && File.Exists(path)) total += new FileInfo(path).Length; }
            catch (IOException) { }
        }
        return total;
    }

    private static void DeleteRetryOutput(string? path)
    {
        if (path is not null && File.Exists(path)) File.Delete(path);
    }

    private static string CombineWarnings(string? first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : $"{first} {second}";

    private static string BuildFailureMessage(Exception error, int? exitCode)
    {
        var message = CreateUserFacingException(error).Message;
        if (exitCode is not null) message += $"（Worker 退出码 {WorkerClientSession.FormatExitCode(exitCode.Value)}）";
        return message;
    }

    private static Exception CreateUserFacingException(Exception error) => error switch
    {
        WorkerExitedException exited when unchecked((uint)exited.ExitCode) == 0xC0000005 =>
            new InvalidOperationException("录制引擎发生原生访问冲突。主界面仍可继续使用，异常文件已保留。", error),
        WorkerExitedException exited when unchecked((uint)exited.ExitCode) == 0xC0000374 =>
            new InvalidOperationException("录制引擎检测到原生堆损坏。主界面仍可继续使用，异常文件已保留。", error),
        WorkerExitedException => new InvalidOperationException("录制引擎意外退出。主界面仍可继续使用，异常文件已保留。", error),
        WorkerReportedException reported when reported.Error.Kind == WorkerErrorKind.Preflight =>
            new InvalidOperationException(reported.Message, error),
        TimeoutException => error,
        _ => error
    };

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
        if (request.Mode == RecordingMode.VideoAndAudio && request.CaptureTarget is null)
        {
            throw new InvalidOperationException("视频模式必须选择显示器或窗口。");
        }
        if (request.Mode == RecordingMode.AudioOnly && !request.CaptureSystemAudio && !request.CaptureMicrophone)
        {
            throw new InvalidOperationException("仅音频模式至少需要启用一种声音来源。");
        }
        if (request.CountdownSeconds is < 0 or > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "倒计时必须在 0 到 30 秒之间。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _countdownCancellation?.Cancel();
        if (State is RecordingState.Recording or RecordingState.Paused or RecordingState.Finalizing)
        {
            try { await StopAsync().ConfigureAwait(false); } catch { }
        }
        if (_worker is not null)
        {
            _worker.Kill();
            await DisposeWorkerAsync().ConfigureAwait(false);
        }
        await RestorePresentationSafeAsync().ConfigureAwait(false);
        _countdownCancellation?.Dispose();
        _operationGate.Dispose();
        _disposed = true;
    }
}
