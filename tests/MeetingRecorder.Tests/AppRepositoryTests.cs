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
        var settings = new AppSettings
        {
            OutputDirectory = Path.Combine(_root, "Output"),
            CountdownSeconds = 5,
            ExcludeMainWindowFromDisplayCapture = true,
            HasAcceptedCaptureProtectionWarning = true
        };
        await repository.SaveSettingsAsync(settings);
        var loaded = await repository.LoadSettingsAsync();
        Assert.Equal(5, loaded.CountdownSeconds);
        Assert.Equal(settings.OutputDirectory, loaded.OutputDirectory);
        Assert.True(loaded.ExcludeMainWindowFromDisplayCapture);
        Assert.True(loaded.HasAcceptedCaptureProtectionWarning);
        Assert.False(File.Exists(repository.Paths.SettingsPath + ".tmp"));
    }

    [Fact]
    public async Task AudioRecoveryPrefersPlayableTemporaryMp3()
    {
        var repository = CreateRepository();
        var output = Path.Combine(_root, "Output");
        Directory.CreateDirectory(output);
        await repository.SaveSettingsAsync(new AppSettings { OutputDirectory = output });
        var partialM4a = Path.Combine(output, "会议录制_20260829_120000.partial.m4a");
        var recoveryMp3 = Path.Combine(output, "会议录制_20260829_120000.recovery.partial.mp3");
        await File.WriteAllBytesAsync(partialM4a, [1, 2, 3]);
        await File.WriteAllBytesAsync(recoveryMp3, [4, 5, 6, 7]);
        var session = new ActiveSession
        {
            Id = "session",
            Mode = RecordingMode.AudioOnly,
            StartedAt = DateTimeOffset.Now,
            FinalPath = Path.Combine(output, "会议录制_20260829_120000.m4a"),
            PartialPath = partialM4a,
            RecoveryMediaPath = recoveryMp3,
            Status = "Failed"
        };
        await repository.SaveActiveSessionAsync(session);

        var candidate = Assert.Single(await repository.ScanRecoveryAsync());
        Assert.Equal("session", candidate.Id);
        var recovered = await repository.RecoverAsync(candidate);
        Assert.EndsWith(".mp3", recovered, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(recovered));
        Assert.True(File.Exists(partialM4a.Replace(".partial.m4a", ".unfinalized.m4a", StringComparison.OrdinalIgnoreCase)));
        Assert.False(File.Exists(repository.Paths.ActiveSessionPath));
    }

    [Fact]
    public async Task LegacyV1AudioRecoveryMp4_IsStillDiscovered()
    {
        var repository = CreateRepository();
        var output = Path.Combine(_root, "Output");
        Directory.CreateDirectory(output);
        await repository.SaveSettingsAsync(new AppSettings { OutputDirectory = output });
        var partialM4a = Path.Combine(output, "会议录制_20260829_130000.partial.m4a");
        var recoveryMp4 = Path.Combine(output, "会议录制_20260829_130000.recovery.partial.mp4");
        await File.WriteAllBytesAsync(partialM4a, [1]);
        await File.WriteAllBytesAsync(recoveryMp4, [2, 3]);

        var candidate = Assert.Single(await repository.ScanRecoveryAsync());

        Assert.Equal(recoveryMp4, candidate.RecoveryMediaPath);
        var recovered = await repository.RecoverAsync(candidate);
        Assert.EndsWith(".mp4", recovered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveHistory_DeletesMatchingIdAndKeepsOthers()
    {
        var repository = CreateRepository();
        var output = Path.Combine(_root, "Output");
        Directory.CreateDirectory(output);
        await repository.SaveSettingsAsync(new AppSettings { OutputDirectory = output });
        var keep = HistoryItem("keep", Path.Combine(output, "keep.mp4"));
        var remove = HistoryItem("remove", Path.Combine(output, "remove.mp4"));
        await repository.AddHistoryAsync(keep);
        await repository.AddHistoryAsync(remove);

        await repository.RemoveHistoryAsync("remove");

        var remaining = await repository.LoadHistoryAsync();
        var single = Assert.Single(remaining);
        Assert.Equal("keep", single.Id);
        Assert.False(File.Exists(repository.Paths.HistoryPath + ".tmp"));
    }

    [Fact]
    public async Task RemoveHistory_UnknownId_IsNoOp()
    {
        var repository = CreateRepository();
        var output = Path.Combine(_root, "Output");
        Directory.CreateDirectory(output);
        await repository.SaveSettingsAsync(new AppSettings { OutputDirectory = output });
        await repository.AddHistoryAsync(HistoryItem("only", Path.Combine(output, "only.mp4")));

        await repository.RemoveHistoryAsync("does-not-exist");

        var remaining = await repository.LoadHistoryAsync();
        Assert.Single(remaining);
    }

    private static RecordingHistoryItem HistoryItem(string id, string filePath) => new()
    {
        Id = id,
        FilePath = filePath,
        Mode = RecordingMode.VideoAndAudio,
        StartedAt = DateTimeOffset.Now,
        FinishedAt = DateTimeOffset.Now.AddMinutes(1),
        Duration = TimeSpan.FromMinutes(1),
        FileSizeBytes = 1024
    };
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
