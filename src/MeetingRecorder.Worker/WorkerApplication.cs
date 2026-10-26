using System.Threading.Channels;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Protocol;
using MeetingRecorder.Core.Utilities;
using MeetingRecorder.Engine.Abstractions;
using MeetingRecorder.Engine.Diagnostics;
using MeetingRecorder.Engine.Discovery;
using MeetingRecorder.Engine.Recording;

namespace MeetingRecorder.Worker;

internal sealed class WorkerApplication(PipeConnection connection, RollingFileLogger logger)
{
    public async Task<int> RunDiscoveryAsync()
    {
        var frame = await connection.ReceiveAsync().ConfigureAwait(false);
        if (frame is not IpcControlFrame { Message.Type: IpcMessageTypes.Discover } control)
        {
            throw new InvalidDataException("设备 Worker 未收到发现请求。");
        }
        var request = control.Message.ReadPayload<WorkerDiscoveryRequest>();
        logger.Info("discovery-start", $"parentPid={request.ParentProcessId}");
        var preflight = WorkerPreflight.CheckEnvironment();
        var discoveryBlockers = preflight.Issues
            .Where(issue => issue.Code is "process-x64" or "windows-version" or "vc-runtime")
            .ToArray();
        if (discoveryBlockers.Length > 0)
        {
            var message = string.Join(Environment.NewLine, discoveryBlockers.Select(issue => issue.Message));
            await SendErrorAsync(new WorkerError(WorkerErrorKind.Preflight, message)).ConfigureAwait(false);
            return WorkerExitCodes.PreflightFailure;
        }
        var discovered = await Task.Run(() => DeviceDiscovery.DiscoverAll(request.ParentProcessId)).ConfigureAwait(false);
        var warnings = discovered.Warnings
            .Concat(preflight.Issues.Select(issue => issue.Message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var result = discovered with { Warnings = warnings };
        await connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.DiscoveryResult, result, control.Message.RequestId)).ConfigureAwait(false);
        logger.Info("discovery-complete", $"targets={result.CaptureTargets.Count}; render={result.SystemAudioDevices.Count}; capture={result.MicrophoneDevices.Count}; warnings={result.Warnings.Count}");
        return WorkerExitCodes.Success;
    }

    public async Task<int> RunRecordingAsync()
    {
        var frame = await connection.ReceiveAsync().ConfigureAwait(false);
        if (frame is not IpcControlFrame { Message.Type: IpcMessageTypes.Start } control)
        {
            throw new InvalidDataException("录制 Worker 未收到启动请求。");
        }
        var request = control.Message.ReadPayload<WorkerRecordingRequest>();
        logger.Info("recording-request", $"session={request.SessionId}; mode={request.Mode}; target={request.CaptureTarget?.Kind}; systemAudio={request.CaptureSystemAudio}; microphone={request.CaptureMicrophone}; microphoneMuted={request.StartMicrophoneMuted}; hardware={request.UseHardwareEncoding}; quality={request.Quality}; preview={request.EnablePreview}");

        var preflight = WorkerPreflight.Check(request.PartialPath);
        foreach (var issue in preflight.Issues)
        {
            if (issue.IsFatal) logger.Error("preflight", $"{issue.Code}: {issue.Message}");
            else logger.Warning("preflight", $"{issue.Code}: {issue.Message}");
        }
        if (!preflight.IsSuccess)
        {
            var message = string.Join(Environment.NewLine, preflight.Issues.Where(issue => issue.IsFatal).Select(issue => issue.Message));
            await SendErrorAsync(new WorkerError(WorkerErrorKind.Preflight, message)).ConfigureAwait(false);
            return WorkerExitCodes.PreflightFailure;
        }

        using var powerRequest = new RecordingPowerRequest();
        logger.Info("power-request", "已请求阻止自动睡眠，录制结束或 Worker 退出时释放。");
        await using IRecordingEngine engine = request.Mode == RecordingMode.AudioOnly
            ? new ResilientAudioOnlyRecordingEngine(request)
            : new VideoRecordingEngine(request);
        Channel<PreviewFrame>? previews = null;
        Task previewSender = Task.CompletedTask;
        if (request.EnablePreview)
        {
            var previewChannel = Channel.CreateBounded<PreviewFrame>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            previews = previewChannel;
            engine.PreviewFrameAvailable += (_, eventArgs) => previewChannel.Writer.TryWrite(eventArgs.Frame);
        }
        using var lifetime = new CancellationTokenSource();
        var engineFault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Faulted += (_, eventArgs) => engineFault.TrySetResult(eventArgs.Error);
        if (previews is not null) previewSender = SendPreviewsAsync(previews.Reader, lifetime.Token);

        try
        {
            await engine.StartAsync().ConfigureAwait(false);
        }
        catch (HardwareEncoderUnavailableException error)
        {
            logger.Error("hardware-encoder", "硬件编码器初始化失败。", error);
            await SendErrorAsync(new WorkerError(WorkerErrorKind.HardwareEncoderUnavailable, error.Message, error.ToString())).ConfigureAwait(false);
            lifetime.Cancel();
            previews?.Writer.TryComplete();
            await IgnoreCancellationAsync(previewSender).ConfigureAwait(false);
            return WorkerExitCodes.HardwareEncoderUnavailable;
        }
        catch (Exception error)
        {
            logger.Error("engine-start", "录制引擎启动失败。", error);
            await SendErrorAsync(new WorkerError(WorkerErrorKind.EngineFailure, error.Message, error.ToString())).ConfigureAwait(false);
            lifetime.Cancel();
            previews?.Writer.TryComplete();
            await IgnoreCancellationAsync(previewSender).ConfigureAwait(false);
            return WorkerExitCodes.EngineFailure;
        }

        await connection.SendAsync(IpcControlMessage.Empty(IpcMessageTypes.Started)).ConfigureAwait(false);
        await SendStateAsync(RecordingState.Recording).ConfigureAwait(false);
        logger.Info("engine-started", $"session={request.SessionId}");
        var microphoneEnabled = !request.StartMicrophoneMuted;

        var automaticStop = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressTask = RunProgressLoopAsync(engine, request, automaticStop, lifetime.Token);
        try
        {
            while (true)
            {
                var receiveTask = connection.ReceiveAsync(lifetime.Token).AsTask();
                var winner = await Task.WhenAny(receiveTask, engineFault.Task, automaticStop.Task).ConfigureAwait(false);
                if (winner == engineFault.Task)
                {
                    var error = await engineFault.Task.ConfigureAwait(false);
                    return await FailRecordingAsync(engine, error).ConfigureAwait(false);
                }
                if (winner == automaticStop.Task)
                {
                    return await CompleteRecordingAsync(engine, await automaticStop.Task.ConfigureAwait(false)).ConfigureAwait(false);
                }

                var commandFrame = await receiveTask.ConfigureAwait(false);
                if (commandFrame is null) throw new EndOfStreamException("主程序已断开命名管道。");
                if (commandFrame is not IpcControlFrame command) continue;
                switch (command.Message.Type)
                {
                    case IpcMessageTypes.Pause:
                        await engine.PauseAsync().ConfigureAwait(false);
                        await SendStateAsync(RecordingState.Paused).ConfigureAwait(false);
                        break;
                    case IpcMessageTypes.Resume:
                        await engine.ResumeAsync().ConfigureAwait(false);
                        await SendStateAsync(RecordingState.Recording).ConfigureAwait(false);
                        break;
                    case IpcMessageTypes.Microphone:
                        {
                            var update = command.Message.ReadPayload<WorkerMicrophoneUpdate>();
                            try
                            {
                                await engine.SetMicrophoneEnabledAsync(update.Enabled).ConfigureAwait(false);
                                microphoneEnabled = update.Enabled;
                                logger.Info("microphone-toggle", $"session={request.SessionId}; enabled={update.Enabled}");
                            }
                            catch (Exception error)
                            {
                                logger.Error("microphone-toggle", "麦克风开关未生效，录制继续。", error);
                            }
                            await connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.Microphone, new WorkerMicrophoneUpdate(microphoneEnabled))).ConfigureAwait(false);
                            break;
                        }
                    case IpcMessageTypes.Stop:
                        return await CompleteRecordingAsync(engine, null).ConfigureAwait(false);
                    default:
                        throw new InvalidDataException($"录制 Worker 收到未知命令：{command.Message.Type}。");
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.Error("recording-loop", "录制控制循环失败。", error);
            return await FailRecordingAsync(engine, error).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            previews?.Writer.TryComplete();
            await IgnoreCancellationAsync(progressTask).ConfigureAwait(false);
            await IgnoreCancellationAsync(previewSender).ConfigureAwait(false);
        }
    }

    private async Task<int> CompleteRecordingAsync(IRecordingEngine engine, string? warning)
    {
        await SendStateAsync(RecordingState.Finalizing).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var duration = await engine.StopAsync(timeout.Token).ConfigureAwait(false);
            await connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.Completed, new WorkerCompleted(duration, warning))).ConfigureAwait(false);
            logger.Info("recording-complete", $"durationMs={(long)duration.TotalMilliseconds}; warning={warning is not null}");
            return WorkerExitCodes.Success;
        }
        catch (Exception error)
        {
            return await FailRecordingAsync(engine, error, attemptStop: false).ConfigureAwait(false);
        }
    }

    private async Task<int> FailRecordingAsync(IRecordingEngine engine, Exception error, bool attemptStop = true)
    {
        var context = error is InvalidOperationException && error.Message == "参数错误。"
            ? "（Windows 窗口捕获返回 E_INVALIDARG）"
            : string.Empty;
        logger.Error("recording-failed", $"录制失败，保留恢复文件。{context}", error);
        if (attemptStop)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await engine.StopAsync(timeout.Token).ConfigureAwait(false); }
            catch (Exception stopError) { logger.Error("failure-finalize", "异常后的安全收尾失败。", stopError); }
        }
        var kind = error is AudioBufferOverflowException ? WorkerErrorKind.BufferOverflow : WorkerErrorKind.EngineFailure;
        try { await SendErrorAsync(new WorkerError(kind, error.Message, error.ToString())).ConfigureAwait(false); }
        catch (Exception sendError) { logger.Error("error-report", "无法向主程序报告错误。", sendError); }
        return WorkerExitCodes.EngineFailure;
    }

    private async Task RunProgressLoopAsync(
        IRecordingEngine engine,
        WorkerRecordingRequest request,
        TaskCompletionSource<string?> automaticStop,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var elapsed = engine.Elapsed;
                var fileSize = CurrentFileSize(request.PartialPath, request.RecoveryMediaPath);
                var available = GetAvailableDiskBytes(request.PartialPath);
                var action = DiskSpacePolicy.Evaluate(available);
                var warning = engine.Warning;
                if (action == DiskSpaceAction.Stop)
                {
                    warning = "可用磁盘空间低于 512 MB，已自动安全停止。";
                    automaticStop.TrySetResult(warning);
                }
                else if (action == DiskSpaceAction.Warn)
                {
                    warning = "可用磁盘空间低于 5 GB。";
                }
                var remaining = EstimateRemainingDiskTime(available, fileSize, elapsed, request.Mode);
                var progress = new RecordingProgress(elapsed, fileSize, remaining, engine.SystemAudioLevel, engine.MicrophoneLevel, warning);
                await connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.Progress, progress), cancellationToken).ConfigureAwait(false);
                if (action == DiskSpaceAction.Stop) return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SendPreviewsAsync(ChannelReader<PreviewFrame> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var preview in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await connection.SendPreviewAsync(preview, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private ValueTask SendStateAsync(RecordingState state) =>
        connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.State, new WorkerStateUpdate(state)));

    private ValueTask SendErrorAsync(WorkerError error) =>
        connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.Error, error));

    private static long CurrentFileSize(params string?[] paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try { if (File.Exists(path)) total += new FileInfo(path).Length; }
            catch (IOException) { }
        }
        return total;
    }

    private static long GetAvailableDiskBytes(string outputPath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(outputPath));
            return string.IsNullOrWhiteSpace(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return long.MaxValue; }
    }

    private static TimeSpan? EstimateRemainingDiskTime(long available, long fileSize, TimeSpan elapsed, RecordingMode mode)
    {
        if (available <= 0) return TimeSpan.Zero;
        var bytesPerSecond = elapsed.TotalSeconds >= 10 && fileSize > 0
            ? fileSize / elapsed.TotalSeconds
            : mode == RecordingMode.AudioOnly ? 28_000d : 780_000d;
        return bytesPerSecond <= 0 ? null : TimeSpan.FromSeconds(Math.Min(available / bytesPerSecond, TimeSpan.MaxValue.TotalSeconds));
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
