using System.IO.Pipes;
using MeetingRecorder.Core.Abstractions;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Protocol;
using MeetingRecorder.Core.Recording;

namespace MeetingRecorder.Tests;

public sealed class WorkerIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MeetingRecorderWorkerIsolation", Guid.NewGuid().ToString("N"));
    private static readonly WorkerTimeoutOptions FastTimeouts = new(
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(350));

    [Theory]
    [InlineData("Release")]
    [InlineData("Debug")]
    public void DevelopmentWorkerLocator_PrefersCallingConfiguration(string configuration)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "MeetingRecorder.sln"), string.Empty);
        foreach (var candidateConfiguration in new[] { "Release", "Debug" })
        {
            var workerDirectory = Path.Combine(
                _root,
                "src",
                "MeetingRecorder.Worker",
                "bin",
                "x64",
                candidateConfiguration,
                "net10.0-windows10.0.19041.0");
            Directory.CreateDirectory(workerDirectory);
            File.WriteAllBytes(Path.Combine(workerDirectory, "MeetingRecorder.Worker.exe"), [1]);
            File.WriteAllBytes(Path.Combine(workerDirectory, "MeetingRecorder.Worker.dll"), [1]);
        }
        var applicationDirectory = Path.Combine(
            _root,
            "tests",
            "MeetingRecorder.Tests",
            "bin",
            "x64",
            configuration,
            "net10.0-windows10.0.19041.0");
        Directory.CreateDirectory(applicationDirectory);

        var resolved = WorkerExecutableLocator.Find(applicationDirectory);

        Assert.Contains($"{Path.DirectorySeparatorChar}{configuration}{Path.DirectorySeparatorChar}", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1073741819)] // 0xC0000005 access violation
    [InlineData(-1073740940)] // 0xC0000374 heap corruption
    public async Task NativeCrash_LeavesServiceAliveAndDevicesRefreshable(int nativeExitCode)
    {
        var presentation = new TestRecordingPresentationCoordinator(
            new RecordingPresentationDecision(true, true));
        var launcher = new FakeWorkerLauncher(async context =>
        {
            if (context.Mode == WorkerMode.Discovery)
            {
                await ReplyToDiscoveryAsync(context);
                return;
            }
            var request = await ReadStartAsync(context);
            await File.WriteAllBytesAsync(request.PartialPath, [1, 2, 3, 4]);
            await context.Connection.SendAsync(IpcControlMessage.Empty(IpcMessageTypes.Started));
            await context.Connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.State, new WorkerStateUpdate(RecordingState.Recording)));
            await Task.Delay(30);
            context.Process.Complete(nativeExitCode);
        });
        await using var service = CreateService(launcher, presentation);

        await service.StartAsync(CreateVideoRequest());
        await WaitForStateAsync(service, RecordingState.Failed);

        Assert.Equal(RecordingState.Failed, service.State);
        var recovery = Assert.Single(
            await CreateRepository().ScanRecoveryAsync(),
            candidate => candidate.PartialMediaPath?.StartsWith(_root, StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(recovery.PartialMediaPath);
        var targets = await service.GetCaptureTargetsAsync();
        Assert.Single(targets);
        Assert.Equal(1, presentation.PrepareCalls);
        Assert.Equal(1, presentation.RestoreCalls);
    }

    [Fact]
    public async Task Countdown_DoesNotPrepareOrChangeWindowPresentation()
    {
        var presentation = new TestRecordingPresentationCoordinator(
            new RecordingPresentationDecision(true, true));
        var launcher = new FakeWorkerLauncher(_ => throw new Xunit.Sdk.XunitException("倒计时取消后不应启动 Worker。"));
        await using var service = CreateService(launcher, presentation);

        var start = service.StartAsync(CreateVideoRequest() with { CountdownSeconds = 10 });
        await WaitForStateAsync(service, RecordingState.Countdown);

        Assert.Equal(0, presentation.PrepareCalls);
        await service.StopAsync();
        await start;
        Assert.Equal(RecordingState.Idle, service.State);
        Assert.Equal(0, presentation.PrepareCalls);
        Assert.Equal(0, presentation.RestoreCalls);
    }

    [Fact]
    public async Task PresentationFailure_DisablesPreviewButRecordingContinues()
    {
        WorkerRecordingRequest? workerRequest = null;
        var presentation = new TestRecordingPresentationCoordinator(
            prepareError: new InvalidOperationException("display affinity unavailable"));
        var launcher = new FakeWorkerLauncher(async context =>
        {
            workerRequest = await ReadStartAsync(context);
            await context.Connection.SendAsync(IpcControlMessage.Empty(IpcMessageTypes.Started));
            await context.Connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.State, new WorkerStateUpdate(RecordingState.Recording)));
            var stop = Assert.IsType<IpcControlFrame>(await context.Connection.ReceiveAsync());
            Assert.Equal(IpcMessageTypes.Stop, stop.Message.Type);
            await File.WriteAllBytesAsync(workerRequest.PartialPath, [1, 2, 3, 4]);
            await context.Connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.Completed, new WorkerCompleted(TimeSpan.FromSeconds(1), null)));
            context.Process.Complete(0);
        });
        await using var service = CreateService(launcher, presentation);

        await service.StartAsync(CreateVideoRequest());
        await service.StopAsync();

        Assert.NotNull(workerRequest);
        Assert.False(workerRequest.EnablePreview);
        Assert.Equal(RecordingState.Idle, service.State);
        Assert.Equal(1, presentation.PrepareCalls);
        Assert.Equal(1, presentation.RestoreCalls);
    }

    [Fact]
    public async Task PreflightFailure_IsReportedWithoutClosingService()
    {
        var launcher = new FakeWorkerLauncher(async context =>
        {
            _ = await ReadStartAsync(context);
            await context.Connection.SendAsync(IpcControlMessage.Create(
                IpcMessageTypes.Error,
                new WorkerError(WorkerErrorKind.Preflight, "缺少 Media Foundation。")));
            context.Process.Complete(20);
        });
        await using var service = CreateService(launcher);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CreateVideoRequest()));

        Assert.Contains("Media Foundation", error.Message, StringComparison.Ordinal);
        Assert.Equal(RecordingState.Failed, service.State);
    }

    [Fact]
    public async Task StartupHang_TimesOutAndKillsOnlyWorker()
    {
        var presentation = new TestRecordingPresentationCoordinator(
            new RecordingPresentationDecision(true, true));
        var launcher = new FakeWorkerLauncher(async context =>
        {
            _ = await ReadStartAsync(context);
            await context.Process.WaitUntilExitedAsync();
        });
        await using var service = CreateService(launcher, presentation);

        var error = await Assert.ThrowsAsync<TimeoutException>(() => service.StartAsync(CreateVideoRequest()));

        Assert.Contains("未完成启动", error.Message, StringComparison.Ordinal);
        Assert.Equal(RecordingState.Failed, service.State);
        Assert.True(launcher.Processes.Single().WasKilled);
        Assert.Equal(1, presentation.RestoreCalls);
    }

    [Fact]
    public async Task StartupTimeout_IsOneBudgetAcrossHandshakeAndEngineInitialization()
    {
        var launcher = new FakeWorkerLauncher(async context =>
        {
            _ = await ReadStartAsync(context);
            await Task.Delay(200);
            await context.Connection.SendAsync(IpcControlMessage.Empty(IpcMessageTypes.Started));
        }, TimeSpan.FromMilliseconds(200));
        await using var service = CreateService(launcher);

        await Assert.ThrowsAsync<TimeoutException>(() => service.StartAsync(CreateVideoRequest()));

        Assert.Equal(RecordingState.Failed, service.State);
        Assert.True(launcher.Processes.Single().WasKilled);
    }

    [Fact]
    public async Task FinalizationHang_IsBoundedAndPreservesPartialFile()
    {
        var presentation = new TestRecordingPresentationCoordinator(
            new RecordingPresentationDecision(true, true));
        var launcher = new FakeWorkerLauncher(async context =>
        {
            var request = await ReadStartAsync(context);
            await File.WriteAllBytesAsync(request.PartialPath, [1, 2, 3, 4]);
            await context.Connection.SendAsync(IpcControlMessage.Empty(IpcMessageTypes.Started));
            await context.Connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.State, new WorkerStateUpdate(RecordingState.Recording)));
            var command = Assert.IsType<IpcControlFrame>(await context.Connection.ReceiveAsync());
            Assert.Equal(IpcMessageTypes.Stop, command.Message.Type);
            await context.Connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.State, new WorkerStateUpdate(RecordingState.Finalizing)));
            await context.Process.WaitUntilExitedAsync();
        });
        await using var service = CreateService(launcher, presentation);
        await service.StartAsync(CreateVideoRequest());

        await Assert.ThrowsAsync<TimeoutException>(() => service.StopAsync());

        Assert.Equal(RecordingState.Failed, service.State);
        Assert.Single(
            await CreateRepository().ScanRecoveryAsync(),
            candidate => candidate.PartialMediaPath?.StartsWith(_root, StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(launcher.Processes.Single().WasKilled);
        Assert.Equal(1, presentation.RestoreCalls);
    }

    [Fact]
    public async Task HardwareFailure_RetriesInFreshSoftwareWorker()
    {
        var recordingWorkers = 0;
        var presentation = new TestRecordingPresentationCoordinator(
            new RecordingPresentationDecision(true, true));
        var launcher = new FakeWorkerLauncher(async context =>
        {
            var request = await ReadStartAsync(context);
            Assert.True(request.EnablePreview);
            Interlocked.Increment(ref recordingWorkers);
            if (request.UseHardwareEncoding)
            {
                await context.Connection.SendAsync(IpcControlMessage.Create(
                    IpcMessageTypes.Error,
                    new WorkerError(WorkerErrorKind.HardwareEncoderUnavailable, "hardware unavailable")));
                context.Process.Complete(21);
                return;
            }

            await context.Connection.SendAsync(IpcControlMessage.Empty(IpcMessageTypes.Started));
            await context.Connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.State, new WorkerStateUpdate(RecordingState.Recording)));
            var stop = Assert.IsType<IpcControlFrame>(await context.Connection.ReceiveAsync());
            Assert.Equal(IpcMessageTypes.Stop, stop.Message.Type);
            await File.WriteAllBytesAsync(request.PartialPath, [1, 2, 3, 4]);
            await context.Connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.State, new WorkerStateUpdate(RecordingState.Finalizing)));
            await context.Connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.Completed, new WorkerCompleted(TimeSpan.FromSeconds(1), null)));
            context.Process.Complete(0);
        });
        await using var service = CreateService(launcher, presentation);

        await service.StartAsync(CreateVideoRequest());
        await service.StopAsync();

        Assert.Equal(2, recordingWorkers);
        Assert.Equal(2, launcher.Processes.Count);
        Assert.Equal(RecordingState.Idle, service.State);
        Assert.True(File.Exists(service.CurrentOutputPath));
        Assert.Equal(1, presentation.PrepareCalls);
        Assert.Equal(1, presentation.RestoreCalls);
    }

    private WorkerRecordingService CreateService(
        IWorkerProcessLauncher launcher,
        TestRecordingPresentationCoordinator? presentationCoordinator = null)
    {
        var repository = CreateRepository();
        var logger = new RollingFileLogger(Path.Combine(_root, "test.log"));
        return new WorkerRecordingService(repository, launcher, logger, FastTimeouts, presentationCoordinator);
    }

    private AppRepository CreateRepository()
    {
        Directory.CreateDirectory(_root);
        return new AppRepository(new AppDataPaths(
            _root,
            Path.Combine(_root, "settings.json"),
            Path.Combine(_root, "history.json"),
            Path.Combine(_root, "active-session.json"),
            Path.Combine(_root, "assets"),
            true));
    }

    private RecordingRequest CreateVideoRequest() => new()
    {
        Mode = RecordingMode.VideoAndAudio,
        CaptureTarget = new CaptureTargetInfo("display", "Display", CaptureTargetKind.Display, "display", 0, 1280, 720),
        CaptureSystemAudio = false,
        CaptureMicrophone = false,
        OutputDirectory = Path.Combine(_root, "output"),
        CountdownSeconds = 0,
        PreferHardwareEncoding = true
    };

    private static async Task<WorkerRecordingRequest> ReadStartAsync(FakeWorkerContext context)
    {
        var frame = Assert.IsType<IpcControlFrame>(await context.Connection.ReceiveAsync());
        Assert.Equal(IpcMessageTypes.Start, frame.Message.Type);
        return frame.Message.ReadPayload<WorkerRecordingRequest>();
    }

    private static async Task ReplyToDiscoveryAsync(FakeWorkerContext context)
    {
        var frame = Assert.IsType<IpcControlFrame>(await context.Connection.ReceiveAsync());
        Assert.Equal(IpcMessageTypes.Discover, frame.Message.Type);
        var result = new WorkerDiscoveryResult(
            [new CaptureTargetInfo("display", "Display", CaptureTargetKind.Display, "display", 0, 1280, 720)],
            [],
            [],
            []);
        await context.Connection.SendAsync(IpcControlMessage.Create(IpcMessageTypes.DiscoveryResult, result, frame.Message.RequestId));
        context.Process.Complete(0);
    }

    private static async Task WaitForStateAsync(WorkerRecordingService service, RecordingState expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (service.State != expected && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(expected, service.State);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeWorkerLauncher(
        Func<FakeWorkerContext, Task> behavior,
        TimeSpan? handshakeDelay = null) : IWorkerProcessLauncher
    {
        private int _nextProcessId = 20_000;
        public List<FakeWorkerProcess> Processes { get; } = [];

        public IWorkerProcess Start(string pipeName, string token, WorkerMode mode, string logDirectory)
        {
            var process = new FakeWorkerProcess(Interlocked.Increment(ref _nextProcessId));
            Processes.Add(process);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (handshakeDelay is { } delay) await Task.Delay(delay);
                    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.ConnectAsync(2_000);
                    await using var connection = new PipeConnection(pipe);
                    await connection.SendAsync(IpcControlMessage.Create(
                        IpcMessageTypes.Hello,
                        new WorkerHello(token, mode, process.Id, "fake-1.1.0")));
                    var accepted = Assert.IsType<IpcControlFrame>(await connection.ReceiveAsync());
                    Assert.Equal(IpcMessageTypes.HelloAccepted, accepted.Message.Type);
                    await behavior(new FakeWorkerContext(mode, process, connection));
                    if (!process.HasExited) process.Complete(0);
                }
                catch when (process.WasKilled)
                {
                }
                catch
                {
                    process.Complete(40);
                    throw;
                }
            });
            return process;
        }
    }

    private sealed record FakeWorkerContext(WorkerMode Mode, FakeWorkerProcess Process, PipeConnection Connection);

    private sealed class FakeWorkerProcess(int id) : IWorkerProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id { get; } = id;
        public bool HasExited => _exit.Task.IsCompleted;
        public int ExitCode => _exit.Task.GetAwaiter().GetResult();
        public bool WasKilled { get; private set; }

        public void Complete(int exitCode) => _exit.TrySetResult(exitCode);
        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => _exit.Task.WaitAsync(cancellationToken);
        public Task WaitUntilExitedAsync() => _exit.Task;
        public void Kill()
        {
            WasKilled = true;
            Complete(unchecked((int)0xC000013A));
        }
        public void Dispose() { }
    }
}
