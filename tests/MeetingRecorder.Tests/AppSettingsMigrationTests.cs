using System.Text.Json;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;

namespace MeetingRecorder.Tests;

public sealed class AppSettingsMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MeetingRecorderSettingsMigration", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task V1RandomAudioIds_AreClearedAndSchemaUpgradesToThree()
    {
        Directory.CreateDirectory(_root);
        var paths = CreatePaths();
        var oldSettings = new
        {
            schemaVersion = 1,
            outputDirectory = Path.Combine(_root, "output"),
            systemAudioDeviceId = "random-screen-recorder-id-1",
            microphoneDeviceId = "random-screen-recorder-id-2",
            countdownSeconds = 3,
            startStopHotKey = "Ctrl+Shift+F9",
            pauseResumeHotKey = "Ctrl+Shift+F10"
        };
        await File.WriteAllTextAsync(paths.SettingsPath, JsonSerializer.Serialize(oldSettings));

        var loaded = await new AppRepository(paths).LoadSettingsAsync();

        Assert.Equal(3, loaded.SchemaVersion);
        Assert.Null(loaded.SystemAudioDeviceId);
        Assert.Null(loaded.MicrophoneDeviceId);
        Assert.False(loaded.ExcludeMainWindowFromDisplayCapture);
        Assert.False(loaded.HasAcceptedCaptureProtectionWarning);
        Assert.True(loaded.PreviewEnabled);
        Assert.False(loaded.RememberHistoryDeleteChoice);
    }

    [Fact]
    public async Task CaptureProtectionWithoutWarningAcceptance_DefaultsToSafeValues()
    {
        Directory.CreateDirectory(_root);
        var paths = CreatePaths();
        await File.WriteAllTextAsync(paths.SettingsPath, """
            {
              "schemaVersion": 2,
              "outputDirectory": "C:\\Recordings",
              "countdownSeconds": 3,
              "startStopHotKey": "Ctrl+Shift+F9",
              "pauseResumeHotKey": "Ctrl+Shift+F10",
              "excludeMainWindowFromDisplayCapture": true
            }
            """);

        var loaded = await new AppRepository(paths).LoadSettingsAsync();

        Assert.False(loaded.ExcludeMainWindowFromDisplayCapture);
        Assert.False(loaded.HasAcceptedCaptureProtectionWarning);
    }

    [Fact]
    public async Task V2Settings_UpgradeToSchemaThreeWithNewDefaults()
    {
        Directory.CreateDirectory(_root);
        var paths = CreatePaths();
        await File.WriteAllTextAsync(paths.SettingsPath, """
            {
              "schemaVersion": 2,
              "outputDirectory": "C:\\Recordings",
              "countdownSeconds": 5,
              "startStopHotKey": "Ctrl+Shift+F9",
              "pauseResumeHotKey": "Ctrl+Shift+F10",
              "hasAcceptedRecordingNotice": true,
              "excludeMainWindowFromDisplayCapture": true,
              "hasAcceptedCaptureProtectionWarning": true
            }
            """);

        var loaded = await new AppRepository(paths).LoadSettingsAsync();

        Assert.Equal(3, loaded.SchemaVersion);
        Assert.Equal(5, loaded.CountdownSeconds);
        Assert.True(loaded.HasAcceptedRecordingNotice);
        Assert.True(loaded.ExcludeMainWindowFromDisplayCapture);
        Assert.True(loaded.HasAcceptedCaptureProtectionWarning);
        Assert.True(loaded.PreviewEnabled);
        Assert.False(loaded.RememberHistoryDeleteChoice);
        Assert.False(loaded.DeleteLocalFileWhenDeletingHistory);
    }

    private AppDataPaths CreatePaths() => new(
        _root,
        Path.Combine(_root, "settings.json"),
        Path.Combine(_root, "history.json"),
        Path.Combine(_root, "active-session.json"),
        Path.Combine(_root, "assets"),
        true);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
