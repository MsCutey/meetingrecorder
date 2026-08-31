using MeetingRecorder.Core.Utilities;

namespace MeetingRecorder.Tests;

public sealed class RecordingWindowPolicyTests
{
    [Theory]
    [InlineData(WindowPresentationState.Normal)]
    [InlineData(WindowPresentationState.Maximized)]
    [InlineData(WindowPresentationState.Minimized)]
    [InlineData(WindowPresentationState.Hidden)]
    public void RecordingStart_NeverChangesCurrentWindowPresentation(WindowPresentationState current)
    {
        foreach (var origin in Enum.GetValues<RecordingStartOrigin>())
        {
            Assert.Equal(current, RecordingWindowPolicy.StateAfterRecordingStart(current, origin));
        }
    }

    [Fact]
    public void OnlyExplicitUserWindowActions_EnterTray()
    {
        Assert.True(RecordingWindowPolicy.ShouldEnterTray(WindowTrayAction.UserMinimized));
        Assert.True(RecordingWindowPolicy.ShouldEnterTray(WindowTrayAction.UserClosed));
        Assert.False(RecordingWindowPolicy.ShouldEnterTray(WindowTrayAction.RecordingStarted));
        Assert.False(RecordingWindowPolicy.ShouldEnterTray(WindowTrayAction.CountdownStarted));
    }
}
