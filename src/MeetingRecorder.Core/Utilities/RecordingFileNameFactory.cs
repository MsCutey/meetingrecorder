using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Utilities;

public static class RecordingFileNameFactory
{
    public static (string FinalPath, string PartialPath, string? RecoveryMediaPath) Create(
        string directory,
        RecordingMode mode,
        DateTimeOffset timestamp,
        Func<string, bool>? pathExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        pathExists ??= File.Exists;
        var extension = mode == RecordingMode.AudioOnly ? ".m4a" : ".mp4";
        var stem = $"会议录制_{timestamp:yyyyMMdd_HHmmss}";
        var suffix = 0;

        while (true)
        {
            var uniqueStem = suffix == 0 ? stem : $"{stem}_{suffix}";
            var finalPath = Path.Combine(directory, uniqueStem + extension);
            var partialPath = Path.Combine(directory, uniqueStem + ".partial" + extension);
            var recoveryPath = mode == RecordingMode.AudioOnly
                ? Path.Combine(directory, uniqueStem + ".recovery.partial.mp3")
                : null;
            if (!pathExists(finalPath) && !pathExists(partialPath) && (recoveryPath is null || !pathExists(recoveryPath)))
            {
                return (finalPath, partialPath, recoveryPath);
            }
            suffix++;
        }
    }

    public static string MakeRecoveredPath(string suggestedPath, Func<string, bool>? pathExists = null)
    {
        pathExists ??= File.Exists;
        var directory = Path.GetDirectoryName(suggestedPath) ?? throw new InvalidDataException("恢复路径无效。");
        var stem = Path.GetFileNameWithoutExtension(suggestedPath) + "_已恢复";
        var extension = Path.GetExtension(suggestedPath);
        var candidate = Path.Combine(directory, stem + extension);
        var suffix = 1;
        while (pathExists(candidate))
        {
            candidate = Path.Combine(directory, $"{stem}_{suffix++}{extension}");
        }
        return candidate;
    }
}
