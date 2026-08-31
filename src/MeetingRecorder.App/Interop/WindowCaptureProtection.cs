using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MeetingRecorder.App.Interop;

public static partial class WindowCaptureProtection
{
    private const uint NoRestrictions = 0x00000000;
    private const uint ExcludeFromCapture = 0x00000011;
    private const uint MonitorOnly = 0x00000001;

    public static WindowCaptureProtectionResult Apply(nint windowHandle)
    {
        if (windowHandle == nint.Zero) throw new ArgumentException("窗口句柄无效。", nameof(windowHandle));
        if (SetWindowDisplayAffinity(windowHandle, ExcludeFromCapture))
        {
            return new WindowCaptureProtectionResult(true, false, null);
        }
        var firstError = Marshal.GetLastWin32Error();
        if (SetWindowDisplayAffinity(windowHandle, MonitorOnly))
        {
            return new WindowCaptureProtectionResult(true, true, $"WDA_EXCLUDEFROMCAPTURE 不可用，已使用兼容模式（Win32 {firstError}）。");
        }
        var error = Marshal.GetLastWin32Error();
        return new WindowCaptureProtectionResult(false, false, new Win32Exception(error).Message);
    }

    public static WindowCaptureProtectionResult Remove(nint windowHandle)
    {
        if (windowHandle == nint.Zero) throw new ArgumentException("窗口句柄无效。", nameof(windowHandle));
        if (SetWindowDisplayAffinity(windowHandle, NoRestrictions))
        {
            return new WindowCaptureProtectionResult(true, false, null);
        }
        var error = Marshal.GetLastWin32Error();
        return new WindowCaptureProtectionResult(false, false, new Win32Exception(error).Message);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint windowHandle, uint affinity);
}

public sealed record WindowCaptureProtectionResult(bool IsSuccess, bool UsedCompatibilityMode, string? Warning);
