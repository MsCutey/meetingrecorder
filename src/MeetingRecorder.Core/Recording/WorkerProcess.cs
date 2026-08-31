using System.Diagnostics;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Protocol;

namespace MeetingRecorder.Core.Recording;

public interface IWorkerProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken = default);
    void Kill();
}

public interface IWorkerProcessLauncher
{
    IWorkerProcess Start(string pipeName, string token, WorkerMode mode, string logDirectory);
}

public sealed class WorkerProcessLauncher(string? executablePath = null) : IWorkerProcessLauncher
{
    private readonly string? _configuredExecutablePath = executablePath;

    public IWorkerProcess Start(string pipeName, string token, WorkerMode mode, string logDirectory)
    {
        var executablePath = _configuredExecutablePath ?? WorkerExecutableLocator.Find();
        var bootstrapLogger = new RollingFileLogger(Path.Combine(logDirectory, "worker-bootstrap.log"));
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--token");
        startInfo.ArgumentList.Add(token);
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add(mode.ToString());
        startInfo.ArgumentList.Add("--log-directory");
        startInfo.ArgumentList.Add(logDirectory);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动录制 Worker 进程。");
        return new SystemWorkerProcess(process, bootstrapLogger);
    }
}

public static class WorkerExecutableLocator
{
    public static string Find(string? applicationDirectory = null)
    {
        applicationDirectory ??= AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(applicationDirectory, "Worker", "MeetingRecorder.Worker.exe"),
            Path.Combine(applicationDirectory, "MeetingRecorder.Worker.exe")
        };
        foreach (var candidate in candidates)
        {
            if (IsRunnableCandidate(candidate)) return Path.GetFullPath(candidate);
        }

        var directory = new DirectoryInfo(applicationDirectory);
        for (var depth = 0; directory is not null && depth < 9; depth++, directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "MeetingRecorder.sln"))) continue;
            foreach (var configuration in GetDevelopmentConfigurations(applicationDirectory))
            {
                var developmentCandidate = Path.Combine(
                    directory.FullName,
                    "src",
                    "MeetingRecorder.Worker",
                    "bin",
                    "x64",
                    configuration,
                    "net10.0-windows10.0.19041.0",
                    "MeetingRecorder.Worker.exe");
                if (IsRunnableCandidate(developmentCandidate)) return developmentCandidate;
            }
        }
        throw new FileNotFoundException("找不到 MeetingRecorder.Worker.exe。请修复安装或重新生成发布包。");
    }

    private static IEnumerable<string> GetDevelopmentConfigurations(string applicationDirectory)
    {
        var segments = Path.GetFullPath(applicationDirectory)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Contains("Release", StringComparer.OrdinalIgnoreCase))
        {
            return ["Release", "Debug"];
        }
        if (segments.Contains("Debug", StringComparer.OrdinalIgnoreCase))
        {
            return ["Debug", "Release"];
        }
        return ["Release", "Debug"];
    }

    private static bool IsRunnableCandidate(string executablePath) =>
        File.Exists(executablePath) && File.Exists(Path.ChangeExtension(executablePath, ".dll"));
}

internal sealed class SystemWorkerProcess : IWorkerProcess
{
    private readonly Process _process;
    private readonly RollingFileLogger _logger;

    public SystemWorkerProcess(Process process, RollingFileLogger logger)
    {
        _process = process;
        _logger = logger;
        process.OutputDataReceived += OnOutputDataReceived;
        process.ErrorDataReceived += OnErrorDataReceived;
        try { process.BeginOutputReadLine(); }
        catch (InvalidOperationException) { }
        try { process.BeginErrorReadLine(); }
        catch (InvalidOperationException) { }
    }

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;
    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => _process.WaitForExitAsync(cancellationToken);
    public void Kill()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data)) _logger.Info("worker-stdout", eventArgs.Data);
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data)) _logger.Error("worker-stderr", eventArgs.Data);
    }

    public void Dispose()
    {
        try { _process.CancelOutputRead(); } catch (InvalidOperationException) { }
        try { _process.CancelErrorRead(); } catch (InvalidOperationException) { }
        _process.OutputDataReceived -= OnOutputDataReceived;
        _process.ErrorDataReceived -= OnErrorDataReceived;
        _process.Dispose();
    }
}
