using System.IO.Pipes;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Protocol;

namespace MeetingRecorder.Core.Recording;

internal sealed class WorkerClientSession : IAsyncDisposable
{
    private readonly IWorkerProcess _process;
    private readonly PipeConnection _connection;
    private readonly RollingFileLogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _started = NewSignal();
    private readonly TaskCompletionSource<WorkerCompleted> _completed = NewSignal<WorkerCompleted>();
    private readonly TaskCompletionSource<WorkerError> _error = NewSignal<WorkerError>();
    private readonly TaskCompletionSource<WorkerDiscoveryResult> _discovery = NewSignal<WorkerDiscoveryResult>();
    private readonly TaskCompletionSource<int> _exited = NewSignal<int>();
    private readonly Task _readerTask;
    private readonly Task _exitTask;
    private bool _disposed;

    private WorkerClientSession(IWorkerProcess process, PipeConnection connection, RollingFileLogger logger)
    {
        _process = process;
        _connection = connection;
        _logger = logger;
        _readerTask = ReadLoopAsync(_lifetime.Token);
        _exitTask = WatchExitAsync();
    }

    public int ProcessId => _process.Id;
    public Task<WorkerCompleted> Completion => _completed.Task;
    public Task<WorkerError> Error => _error.Task;
    public Task<int> Exit => _exited.Task;

    public event EventHandler<RecordingState>? StateReceived;
    public event EventHandler<RecordingProgress>? ProgressReceived;
    public event EventHandler<PreviewFrame>? PreviewReceived;

    public static async Task<WorkerClientSession> LaunchAsync(
        IWorkerProcessLauncher launcher,
        WorkerMode mode,
        RollingFileLogger logger,
        string logDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var pipeName = $"MeetingRecorder-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            64 * 1024,
            64 * 1024);
        IWorkerProcess? process = null;
        try
        {
            process = launcher.Start(pipeName, token, mode, logDirectory);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var connectTask = server.WaitForConnectionAsync(timeoutSource.Token);
            var exitTask = process.WaitForExitAsync(timeoutSource.Token);
            var connectionWinner = await Task.WhenAny(connectTask, exitTask).ConfigureAwait(false);
            if (connectionWinner == exitTask)
            {
                await exitTask.ConfigureAwait(false);
                throw new WorkerExitedException(process.ExitCode);
            }
            await connectTask.ConfigureAwait(false);
            var connection = new PipeConnection(server);
            var frame = await connection.ReceiveAsync(timeoutSource.Token).ConfigureAwait(false);
            if (frame is not IpcControlFrame { Message.Type: IpcMessageTypes.Hello } helloFrame)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new InvalidDataException("Worker 未发送有效握手。");
            }
            var hello = helloFrame.Message.ReadPayload<WorkerHello>();
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(token),
                    Convert.FromHexString(hello.Token)) ||
                hello.Mode != mode ||
                hello.ProcessId != process.Id)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new UnauthorizedAccessException("Worker 身份验证失败。");
            }
            await connection.SendAsync(IpcControlMessage.Empty(IpcMessageTypes.HelloAccepted), timeoutSource.Token).ConfigureAwait(false);
            logger.Info("worker-connected", $"pid={process.Id}; mode={mode}; protocol={IpcProtocol.Version}; workerVersion={hello.Version}");
            return new WorkerClientSession(process, connection, logger);
        }
        catch
        {
            server.Dispose();
            if (process is not null)
            {
                try { process.Kill(); } catch { }
                process.Dispose();
            }
            throw;
        }
    }

    public ValueTask SendAsync(IpcControlMessage message, CancellationToken cancellationToken = default) =>
        _connection.SendAsync(message, cancellationToken);

    public async Task<WorkerDiscoveryResult> WaitForDiscoveryAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var winner = await Task.WhenAny(_discovery.Task, _error.Task, _exited.Task).WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        if (winner == _discovery.Task) return await _discovery.Task.ConfigureAwait(false);
        if (winner == _error.Task) throw new WorkerReportedException(await _error.Task.ConfigureAwait(false));
        await Task.Delay(50, timeoutSource.Token).ConfigureAwait(false);
        if (_discovery.Task.IsCompletedSuccessfully) return await _discovery.Task.ConfigureAwait(false);
        if (_error.Task.IsCompletedSuccessfully) throw new WorkerReportedException(await _error.Task.ConfigureAwait(false));
        throw new WorkerExitedException(await _exited.Task.ConfigureAwait(false));
    }

    public async Task WaitForStartedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var winner = await Task.WhenAny(_started.Task, _error.Task, _exited.Task).WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        if (winner == _started.Task)
        {
            await _started.Task.ConfigureAwait(false);
            return;
        }
        if (winner == _error.Task) throw new WorkerReportedException(await _error.Task.ConfigureAwait(false));
        await Task.Delay(50, timeoutSource.Token).ConfigureAwait(false);
        if (_error.Task.IsCompletedSuccessfully)
        {
            throw new WorkerReportedException(await _error.Task.ConfigureAwait(false));
        }
        var exitCode = await _exited.Task.ConfigureAwait(false);
        throw new WorkerExitedException(exitCode);
    }

    public async Task WaitForStateAsync(RecordingState expected, Func<ValueTask> sendCommand, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var signal = NewSignal();
        void Handler(object? _, RecordingState state)
        {
            if (state == expected) signal.TrySetResult();
        }
        StateReceived += Handler;
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await sendCommand().AsTask().WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            var winner = await Task.WhenAny(signal.Task, _error.Task, _exited.Task).WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            if (winner == signal.Task) return;
            if (winner == _error.Task) throw new WorkerReportedException(await _error.Task.ConfigureAwait(false));
            throw new WorkerExitedException(await _exited.Task.ConfigureAwait(false));
        }
        finally { StateReceived -= Handler; }
    }

    public void Kill()
    {
        try { _process.Kill(); }
        catch (Exception error) { _logger.Error("worker-kill", "无法终止无响应的 Worker。", error); }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frame = await _connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null) break;
                if (frame is IpcPreviewFrame preview)
                {
                    PreviewReceived?.Invoke(this, preview.Preview);
                    continue;
                }
                var message = ((IpcControlFrame)frame).Message;
                switch (message.Type)
                {
                    case IpcMessageTypes.Started:
                        _started.TrySetResult();
                        break;
                    case IpcMessageTypes.State:
                        StateReceived?.Invoke(this, message.ReadPayload<WorkerStateUpdate>().State);
                        break;
                    case IpcMessageTypes.Progress:
                        ProgressReceived?.Invoke(this, message.ReadPayload<RecordingProgress>());
                        break;
                    case IpcMessageTypes.Completed:
                        _completed.TrySetResult(message.ReadPayload<WorkerCompleted>());
                        break;
                    case IpcMessageTypes.DiscoveryResult:
                        _discovery.TrySetResult(message.ReadPayload<WorkerDiscoveryResult>());
                        break;
                    case IpcMessageTypes.Error:
                        _error.TrySetResult(message.ReadPayload<WorkerError>());
                        break;
                    default:
                        throw new InvalidDataException($"收到未知 Worker 消息：{message.Type}。");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.Error("worker-reader", "读取 Worker 消息失败。", error);
            _error.TrySetResult(new WorkerError(WorkerErrorKind.Protocol, error.Message));
        }
    }

    private async Task WatchExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
            var code = _process.ExitCode;
            _logger.Info("worker-exited", $"pid={_process.Id}; exitCode={FormatExitCode(code)}");
            _exited.TrySetResult(code);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.Error("worker-exit-watch", "监视 Worker 退出失败。", error);
            _exited.TrySetResult(int.MinValue);
        }
    }

    public static string FormatExitCode(int exitCode) => $"{exitCode} (0x{unchecked((uint)exitCode):X8})";

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _connection.DisposeAsync().ConfigureAwait(false);
        if (!_process.HasExited)
        {
            using var gracefulExit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _process.WaitForExitAsync(gracefulExit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.Warning("worker-exit-timeout", $"Worker {_process.Id} 完成通信后仍未退出，已终止该子进程。");
                Kill();
                using var forcedExit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _process.WaitForExitAsync(forcedExit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        _lifetime.Cancel();
        try { await _readerTask.ConfigureAwait(false); } catch { }
        try { await _exitTask.ConfigureAwait(false); } catch { }
        _process.Dispose();
        _lifetime.Dispose();
        _disposed = true;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class WorkerReportedException(WorkerError error) : InvalidOperationException(error.Message)
{
    public WorkerError Error { get; } = error;

    public override string ToString() => string.IsNullOrWhiteSpace(Error.Detail)
        ? base.ToString()
        : $"{base.ToString()}{Environment.NewLine}Worker detail:{Environment.NewLine}{Error.Detail}";
}

public sealed class WorkerExitedException(int exitCode)
    : InvalidOperationException($"录制 Worker 意外退出：{WorkerClientSession.FormatExitCode(exitCode)}")
{
    public int ExitCode { get; } = exitCode;
}
