namespace MeetingRecorder.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MEETINGRECORDER_HARDWARE_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "设置 MEETINGRECORDER_HARDWARE_TESTS=1 后在真实 Windows 音频/显示设备上运行。";
        }
    }
}
