using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Protocol;
using MeetingRecorder.Engine.Abstractions;
using MeetingRecorder.Engine.Recording;

namespace MeetingRecorder.Tests;

public sealed class ResilientAudioOnlyRecordingEngineTests
{
    [Fact]
    public async Task CaptureInitializationFailure_StartsFallbackAndCleansPrimaryFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeetingRecorderFallbackTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var request = CreateRequest(root);
            await File.WriteAllTextAsync(request.PartialPath, "partial");
            await File.WriteAllTextAsync(request.RecoveryMediaPath!, "recovery");
            var primary = new FakeEngine
            {
                StartFailure = new AudioCaptureInitializationException(
                    "capture failed",
                    new InvalidOperationException("bad endpoint"))
            };
            var fallback = new FakeEngine();
            await using var engine = new ResilientAudioOnlyRecordingEngine(
                request,
                () => primary,
                () => fallback);

            await engine.StartAsync();
            await engine.PauseAsync();
            await engine.ResumeAsync();
            await engine.StopAsync();

            Assert.True(primary.Disposed);
            Assert.Equal(1, fallback.StartCount);
            Assert.Equal(1, fallback.PauseCount);
            Assert.Equal(1, fallback.ResumeCount);
            Assert.Equal(1, fallback.StopCount);
            Assert.False(File.Exists(request.PartialPath));
            Assert.False(File.Exists(request.RecoveryMediaPath));
            Assert.Contains("bad endpoint", engine.Warning, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NonCaptureFailure_DoesNotStartFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeetingRecorderFallbackTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var request = CreateRequest(root);
            var primary = new FakeEngine { StartFailure = new InvalidOperationException("encoder failed") };
            var fallbackCreated = false;
            await using var engine = new ResilientAudioOnlyRecordingEngine(
                request,
                () => primary,
                () =>
                {
                    fallbackCreated = true;
                    return new FakeEngine();
                });

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync());

            Assert.Equal("encoder failed", error.Message);
            Assert.False(fallbackCreated);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static WorkerRecordingRequest CreateRequest(string root) => new()
    {
        SessionId = Guid.NewGuid().ToString("N"),
        Mode = RecordingMode.AudioOnly,
        CaptureMicrophone = true,
        PartialPath = Path.Combine(root, "test.partial.m4a"),
        RecoveryMediaPath = Path.Combine(root, "test.recovery.partial.mp3"),
        EngineLogPath = Path.Combine(root, "engine.log")
    };

    private sealed class FakeEngine : IRecordingEngine
    {
        public Exception? StartFailure { get; init; }
        public int StartCount { get; private set; }
        public int PauseCount { get; private set; }
        public int ResumeCount { get; private set; }
        public int StopCount { get; private set; }
        public bool Disposed { get; private set; }
        public TimeSpan Elapsed => TimeSpan.Zero;
        public double SystemAudioLevel => 0;
        public double MicrophoneLevel => 0;
        public string? Warning => "fallback";
        public event EventHandler<PreviewFrameEventArgs>? PreviewFrameAvailable;
        public event EventHandler<EngineFaultEventArgs>? Faulted;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return StartFailure is null ? Task.CompletedTask : Task.FromException(StartFailure);
        }

        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            PauseCount++;
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            ResumeCount++;
            return Task.CompletedTask;
        }

        public Task SetMicrophoneEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<TimeSpan> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.FromResult(TimeSpan.Zero);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            GC.KeepAlive(PreviewFrameAvailable);
            GC.KeepAlive(Faulted);
            return ValueTask.CompletedTask;
        }
    }
}
