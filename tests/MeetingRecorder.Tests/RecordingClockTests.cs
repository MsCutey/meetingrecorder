using MeetingRecorder.Core.Utilities;

namespace MeetingRecorder.Tests;

public sealed class RecordingClockTests
{
    [Fact]
    public void PausedTime_IsExcludedFromElapsedDuration()
    {
        var time = new ManualTimeProvider();
        var clock = new RecordingClock(time);
        clock.Start();
        time.Advance(TimeSpan.FromSeconds(10));
        clock.Pause();
        time.Advance(TimeSpan.FromSeconds(5));
        clock.Resume();
        time.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(17), clock.Stop());
        Assert.Equal(TimeSpan.FromSeconds(17), clock.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(17), clock.Stop());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(_ticks);
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
}
