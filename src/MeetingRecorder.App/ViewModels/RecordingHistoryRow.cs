using MeetingRecorder.Core.Models;
using System.IO;

namespace MeetingRecorder.App.ViewModels;

public sealed record RecordingHistoryRow(RecordingHistoryItem Item)
{
    public string FileName => Path.GetFileName(Item.FilePath);
    public string ModeText => Item.Mode == RecordingMode.AudioOnly ? "仅声音" : "视频 + 声音";
    public string StartedText => Item.StartedAt.LocalDateTime.ToString("yyyy-MM-dd  HH:mm");
    public string DurationText => Item.Duration.ToString(Item.Duration.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
    public string SizeText => FormatBytes(Item.FileSizeBytes);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.00} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024L => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B"
    };
}
