namespace MeetingRecorder.Core.Utilities;

public enum DiskSpaceAction
{
    Continue,
    Warn,
    Stop
}

public static class DiskSpacePolicy
{
    public const long WarningThresholdBytes = 5L * 1024 * 1024 * 1024;
    public const long StopThresholdBytes = 512L * 1024 * 1024;

    public static DiskSpaceAction Evaluate(long availableBytes) => availableBytes switch
    {
        < StopThresholdBytes => DiskSpaceAction.Stop,
        < WarningThresholdBytes => DiskSpaceAction.Warn,
        _ => DiskSpaceAction.Continue
    };
}
