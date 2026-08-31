using MeetingRecorder.Core.Utilities;

namespace MeetingRecorder.Core.Models;

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string OutputDirectory { get; init; } = DefaultOutputDirectory();
    public RecordingMode Mode { get; init; } = RecordingMode.VideoAndAudio;
    public VideoQualityPreset Quality { get; init; } = VideoQualityPreset.FullHd;
    public bool CaptureSystemAudio { get; init; } = true;
    public bool CaptureMicrophone { get; init; } = true;
    public string? SystemAudioDeviceId { get; init; }
    public string? MicrophoneDeviceId { get; init; }
    public int CountdownSeconds { get; init; } = 3;
    public string StartStopHotKey { get; init; } = "Ctrl+Shift+F9";
    public string PauseResumeHotKey { get; init; } = "Ctrl+Shift+F10";
    public bool MinimizeToTray { get; init; } = true;
    public bool HasAcceptedRecordingNotice { get; init; }
    public bool ExcludeMainWindowFromDisplayCapture { get; init; }
    public bool HasAcceptedCaptureProtectionWarning { get; init; }
    public bool PreviewEnabled { get; init; } = true;
    public bool RememberHistoryDeleteChoice { get; init; }
    public bool DeleteLocalFileWhenDeletingHistory { get; init; }

    public static string DefaultOutputDirectory()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
        {
            videos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        return Path.Combine(videos, "MeetingRecorder");
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"不支持的设置版本：{SchemaVersion}。");
        }
        if (CountdownSeconds is < 0 or > 30)
        {
            throw new InvalidDataException("倒计时必须在 0 到 30 秒之间。");
        }

        var startStop = HotKeyGesture.Parse(StartStopHotKey);
        var pauseResume = HotKeyGesture.Parse(PauseResumeHotKey);
        if (startStop == pauseResume)
        {
            throw new InvalidDataException("开始/停止与暂停/恢复热键不能相同。");
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            throw new InvalidDataException("输出目录不能为空。");
        }
    }
}
