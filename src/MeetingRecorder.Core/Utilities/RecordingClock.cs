using System.Diagnostics;

namespace MeetingRecorder.Core.Utilities;

public sealed class RecordingClock(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long _started;
    private long _pausedAt;
    private long _pausedTicks;
    private bool _running;
    private bool _paused;

    public TimeSpan Elapsed
    {
        get
        {
            if (!_running) return TimeSpan.Zero;
            var now = _paused ? _pausedAt : _timeProvider.GetTimestamp();
            return _timeProvider.GetElapsedTime(_started, now - _pausedTicks);
        }
    }

    public void Start()
    {
        _started = _timeProvider.GetTimestamp();
        _pausedAt = 0;
        _pausedTicks = 0;
        _running = true;
        _paused = false;
    }

    public void Pause()
    {
        if (!_running || _paused) return;
        _pausedAt = _timeProvider.GetTimestamp();
        _paused = true;
    }

    public void Resume()
    {
        if (!_running || !_paused) return;
        _pausedTicks += _timeProvider.GetTimestamp() - _pausedAt;
        _paused = false;
    }

    public TimeSpan Stop()
    {
        var elapsed = Elapsed;
        _running = false;
        _paused = false;
        return elapsed;
    }
}
