using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Protocol;

namespace MeetingRecorder.Worker;

internal static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        _ = SetErrorMode(0x0002 | 0x8000);
        var options = WorkerArguments.TryParse(args);
        if (options is null) return WorkerExitCodes.InvalidArguments;

        var logger = new RollingFileLogger(Path.Combine(options.LogDirectory, "worker.log"));
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        logger.Info("worker-start", $"version={version}; pid={Environment.ProcessId}; mode={options.Mode}; os={Environment.OSVersion.VersionString}; x64={Environment.Is64BitProcess}");

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            logger.Error("unhandled", "Worker 发生未处理异常。", eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            logger.Error("unobserved-task", "Worker 发生未观察到的任务异常。", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        try
        {
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pipe = new NamedPipeClientStream(
                ".",
                options.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            await using var connection = new PipeConnection(pipe);
            await connection.SendAsync(IpcControlMessage.Create(
                IpcMessageTypes.Hello,
                new WorkerHello(options.Token, options.Mode, Environment.ProcessId, version)), connectTimeout.Token).ConfigureAwait(false);
            var accepted = await connection.ReceiveAsync(connectTimeout.Token).ConfigureAwait(false);
            if (accepted is not IpcControlFrame { Message.Type: IpcMessageTypes.HelloAccepted })
            {
                throw new InvalidDataException("主程序拒绝了 Worker 握手。");
            }

            var application = new WorkerApplication(connection, logger);
            return options.Mode == WorkerMode.Discovery
                ? await application.RunDiscoveryAsync().ConfigureAwait(false)
                : await application.RunRecordingAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException error)
        {
            logger.Error("timeout", "Worker 连接或操作超时。", error);
            return WorkerExitCodes.ProtocolFailure;
        }
        catch (InvalidDataException error)
        {
            logger.Error("protocol", "Worker IPC 协议失败。", error);
            return WorkerExitCodes.ProtocolFailure;
        }
        catch (Exception error)
        {
            logger.Error("unexpected", "Worker 意外退出。", error);
            return WorkerExitCodes.UnexpectedFailure;
        }
        finally
        {
            logger.Info("worker-exit", $"pid={Environment.ProcessId}");
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint mode);
}

internal sealed record WorkerArguments(string PipeName, string Token, WorkerMode Mode, string LogDirectory)
{
    public static WorkerArguments? TryParse(IReadOnlyList<string> args)
    {
        string? pipe = null;
        string? token = null;
        WorkerMode? mode = null;
        string? logDirectory = null;
        for (var index = 0; index + 1 < args.Count; index += 2)
        {
            switch (args[index])
            {
                case "--pipe": pipe = args[index + 1]; break;
                case "--token": token = args[index + 1]; break;
                case "--mode" when Enum.TryParse<WorkerMode>(args[index + 1], true, out var parsed): mode = parsed; break;
                case "--log-directory": logDirectory = args[index + 1]; break;
                default: return null;
            }
        }
        return string.IsNullOrWhiteSpace(pipe) || string.IsNullOrWhiteSpace(token) || mode is null || string.IsNullOrWhiteSpace(logDirectory)
            ? null
            : new WorkerArguments(pipe, token, mode.Value, logDirectory);
    }
}

internal static class WorkerExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 2;
    public const int ProtocolFailure = 10;
    public const int PreflightFailure = 20;
    public const int HardwareEncoderUnavailable = 21;
    public const int EngineFailure = 30;
    public const int UnexpectedFailure = 40;
}
