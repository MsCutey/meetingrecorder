using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Utilities;

public sealed class RecordingStateMachine
{
    private readonly object _gate = new();
    private RecordingState _state = RecordingState.Idle;

    public RecordingState State
    {
        get { lock (_gate) return _state; }
    }

    public (RecordingState Previous, RecordingState Current) TransitionTo(RecordingState next)
    {
        lock (_gate)
        {
            if (!IsAllowed(_state, next))
            {
                throw new InvalidOperationException($"不允许从 {_state} 切换到 {next}。");
            }

            var previous = _state;
            _state = next;
            return (previous, next);
        }
    }

    private static bool IsAllowed(RecordingState current, RecordingState next) =>
        current == next || (current, next) switch
        {
            (RecordingState.Idle, RecordingState.Countdown or RecordingState.Failed) => true,
            (RecordingState.Countdown, RecordingState.Recording or RecordingState.Idle or RecordingState.Failed) => true,
            (RecordingState.Recording, RecordingState.Paused or RecordingState.Finalizing or RecordingState.Failed) => true,
            (RecordingState.Paused, RecordingState.Recording or RecordingState.Finalizing or RecordingState.Failed) => true,
            (RecordingState.Finalizing, RecordingState.Idle or RecordingState.Failed) => true,
            (RecordingState.Failed, RecordingState.Idle or RecordingState.Countdown) => true,
            _ => false
        };
}
