using System.Diagnostics;

namespace MeetingRecorder.Core.Utilities;

public sealed class RecordingClock(TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long _started;
    private long _pausedAt;
    private long _pausedTicks;
    private TimeSpan _stoppedElapsed;
    private bool _running;
    private bool _paused;

    public TimeSpan Elapsed
    {
        get { lock (_gate) return GetElapsedCore(); }
    }

    public void Start()
    {
        lock (_gate)
        {
            _started = _timeProvider.GetTimestamp();
            _pausedAt = 0;
            _pausedTicks = 0;
            _stoppedElapsed = TimeSpan.Zero;
            _running = true;
            _paused = false;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_running || _paused) return;
            _pausedAt = _timeProvider.GetTimestamp();
            _paused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_running || !_paused) return;
            _pausedTicks += _timeProvider.GetTimestamp() - _pausedAt;
            _paused = false;
        }
    }

    public TimeSpan Stop()
    {
        lock (_gate)
        {
            if (!_running) return _stoppedElapsed;
            _stoppedElapsed = GetElapsedCore();
            _running = false;
            _paused = false;
            return _stoppedElapsed;
        }
    }

    private TimeSpan GetElapsedCore()
    {
        if (!_running) return _stoppedElapsed;
        var now = _paused ? _pausedAt : _timeProvider.GetTimestamp();
        return _timeProvider.GetElapsedTime(_started, now - _pausedTicks);
    }
}
