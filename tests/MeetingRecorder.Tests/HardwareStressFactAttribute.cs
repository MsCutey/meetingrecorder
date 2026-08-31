using Xunit;

namespace MeetingRecorder.Tests;

public sealed class HardwareStressFactAttribute : FactAttribute
{
    public HardwareStressFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MEETINGRECORDER_HARDWARE_STRESS"), "1", StringComparison.Ordinal))
        {
            Skip = "设置 MEETINGRECORDER_HARDWARE_STRESS=1 后运行真实设备连续启停矩阵。";
        }
    }
}
