using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;

namespace MeetingRecorder.Tests;

public sealed class AppRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MeetingRecorderTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SettingsAreWrittenAtomicallyAndReadBack()
    {
        var repository = CreateRepository();
        var settings = new AppSettings { OutputDirectory = Path.Combine(_root, "Output"), CountdownSeconds = 5 };
        await repository.SaveSettingsAsync(settings);
        var loaded = await repository.LoadSettingsAsync();
        Assert.Equal(5, loaded.CountdownSeconds);
        Assert.Equal(settings.OutputDirectory, loaded.OutputDirectory);
        Assert.False(File.Exists(repository.Paths.SettingsPath + ".tmp"));
    }

    [Fact]
    public async Task AudioRecoveryPrefersPlayableTemporaryMp4()
    {
        var repository = CreateRepository();
        var output = Path.Combine(_root, "Output");
        Directory.CreateDirectory(output);
        await repository.SaveSettingsAsync(new AppSettings { OutputDirectory = output });
        var partialM4a = Path.Combine(output, "会议录制_20260829_120000.partial.m4a");
        var recoveryMp4 = Path.Combine(output, "会议录制_20260829_120000.recovery.partial.mp4");
        await File.WriteAllBytesAsync(partialM4a, [1, 2, 3]);
        await File.WriteAllBytesAsync(recoveryMp4, [4, 5, 6, 7]);
        var session = new ActiveSession
        {
            Id = "session",
            Mode = RecordingMode.AudioOnly,
            StartedAt = DateTimeOffset.Now,
            FinalPath = Path.Combine(output, "会议录制_20260829_120000.m4a"),
            PartialPath = partialM4a,
            RecoveryVideoPath = recoveryMp4,
            Status = "Failed"
        };
        await repository.SaveActiveSessionAsync(session);

        var candidate = Assert.Single(await repository.ScanRecoveryAsync());
        Assert.Equal("session", candidate.Id);
        var recovered = await repository.RecoverAsync(candidate);
        Assert.EndsWith(".mp4", recovered, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(recovered));
        Assert.True(File.Exists(partialM4a.Replace(".partial.m4a", ".unfinalized.m4a", StringComparison.OrdinalIgnoreCase)));
        Assert.False(File.Exists(repository.Paths.ActiveSessionPath));
    }

    private AppRepository CreateRepository()
    {
        Directory.CreateDirectory(_root);
        return new AppRepository(new AppDataPaths(
            _root,
            Path.Combine(_root, "settings.json"),
            Path.Combine(_root, "history.json"),
            Path.Combine(_root, "active-session.json"),
            Path.Combine(_root, "Assets"),
            true));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
