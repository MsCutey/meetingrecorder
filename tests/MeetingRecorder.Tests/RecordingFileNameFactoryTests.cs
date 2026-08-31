using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Utilities;

namespace MeetingRecorder.Tests;

public sealed class RecordingFileNameFactoryTests
{
    [Fact]
    public void VideoName_UsesChinesePrefixAndPartialSuffix()
    {
        var timestamp = new DateTimeOffset(2026, 8, 29, 14, 5, 9, TimeSpan.FromHours(8));
        var paths = RecordingFileNameFactory.Create("D:\\Records", RecordingMode.VideoAndAudio, timestamp, _ => false);
        Assert.Equal("会议录制_20260829_140509.mp4", Path.GetFileName(paths.FinalPath));
        Assert.Equal("会议录制_20260829_140509.partial.mp4", Path.GetFileName(paths.PartialPath));
        Assert.Null(paths.RecoveryMediaPath);
    }

    [Fact]
    public void AudioName_ReservesM4aAndRecoveryMp4()
    {
        var timestamp = new DateTimeOffset(2026, 8, 29, 14, 5, 9, TimeSpan.Zero);
        var occupied = Path.Combine("D:\\Records", "会议录制_20260829_140509.m4a");
        var paths = RecordingFileNameFactory.Create("D:\\Records", RecordingMode.AudioOnly, timestamp, path => path == occupied);
        Assert.Equal("会议录制_20260829_140509_1.m4a", Path.GetFileName(paths.FinalPath));
        Assert.EndsWith(".partial.m4a", paths.PartialPath, StringComparison.Ordinal);
        Assert.EndsWith(".recovery.partial.mp3", paths.RecoveryMediaPath, StringComparison.Ordinal);
    }
}
