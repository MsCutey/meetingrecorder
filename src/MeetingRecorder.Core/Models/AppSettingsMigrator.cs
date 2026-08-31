namespace MeetingRecorder.Core.Models;

public static class AppSettingsMigrator
{
    public static AppSettings Migrate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.SchemaVersion switch
        {
            AppSettings.CurrentSchemaVersion => NormalizeCaptureProtection(settings),
            2 => NormalizeCaptureProtection(settings with { SchemaVersion = AppSettings.CurrentSchemaVersion }),
            <= 1 => NormalizeCaptureProtection(
                settings with
                {
                    SchemaVersion = AppSettings.CurrentSchemaVersion,
                    // V1 保存的是 ScreenRecorderLib 每次枚举生成的随机 ID，无法稳定匹配。
                    // 清空后由界面选择 Windows 默认端点并写回稳定的 endpoint ID。
                    SystemAudioDeviceId = null,
                    MicrophoneDeviceId = null
                }),
            _ => throw new InvalidDataException($"设置文件来自更新版本（schema {settings.SchemaVersion}）。")
        };
    }

    private static AppSettings NormalizeCaptureProtection(AppSettings settings) =>
        settings.ExcludeMainWindowFromDisplayCapture && !settings.HasAcceptedCaptureProtectionWarning
            ? settings with { ExcludeMainWindowFromDisplayCapture = false }
            : settings;
}
