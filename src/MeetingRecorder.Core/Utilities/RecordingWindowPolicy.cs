namespace MeetingRecorder.Core.Utilities;

public enum WindowPresentationState
{
    Normal,
    Maximized,
    Minimized,
    Hidden
}

public enum RecordingStartOrigin
{
    VisibleButton,
    Tray,
    HotKey
}

public enum WindowTrayAction
{
    UserMinimized,
    UserClosed,
    RecordingStarted,
    CountdownStarted
}

public static class RecordingWindowPolicy
{
    public static WindowPresentationState StateAfterRecordingStart(
        WindowPresentationState current,
        RecordingStartOrigin origin)
    {
        _ = origin;
        return current;
    }

    public static bool ShouldEnterTray(WindowTrayAction action) =>
        action is WindowTrayAction.UserMinimized or WindowTrayAction.UserClosed;
}
