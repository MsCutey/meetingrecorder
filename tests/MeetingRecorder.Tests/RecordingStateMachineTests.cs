using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Utilities;

namespace MeetingRecorder.Tests;

public sealed class RecordingStateMachineTests
{
    [Fact]
    public void ValidRecordingLifecycle_IsAccepted()
    {
        var machine = new RecordingStateMachine();
        machine.TransitionTo(RecordingState.Countdown);
        machine.TransitionTo(RecordingState.Recording);
        machine.TransitionTo(RecordingState.Paused);
        machine.TransitionTo(RecordingState.Recording);
        machine.TransitionTo(RecordingState.Finalizing);
        machine.TransitionTo(RecordingState.Idle);
        Assert.Equal(RecordingState.Idle, machine.State);
    }

    [Fact]
    public void InvalidTransition_IsRejected()
    {
        var machine = new RecordingStateMachine();
        Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(RecordingState.Paused));
        Assert.Equal(RecordingState.Idle, machine.State);
    }
}
