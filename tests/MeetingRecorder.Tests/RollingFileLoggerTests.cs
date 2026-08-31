using MeetingRecorder.Core.Diagnostics;

namespace MeetingRecorder.Tests;

public sealed class RollingFileLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MeetingRecorderLogger", Guid.NewGuid().ToString("N"));

    [Fact]
    public void UnwritableLogLocation_DoesNotBreakApplicationFlow()
    {
        Directory.CreateDirectory(_root);
        var parentThatIsAFile = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(parentThatIsAFile, "occupied");

        var logger = new RollingFileLogger(Path.Combine(parentThatIsAFile, "app.log"));
        logger.Info("startup", "应用继续运行");
        logger.Error("worker-exit", "仍可完成退出监控", new IOException("disk unavailable"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
