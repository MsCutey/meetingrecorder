using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MeetingRecorder.Engine.Recording;

/// <summary>A process-owned request, safe across async thread switches and released on Worker exit.</summary>
public sealed class RecordingPowerRequest : IDisposable
{
    private readonly SafeFileHandle _handle;

    public RecordingPowerRequest()
    {
        var context = new ReasonContext
        {
            Version = 0,
            Flags = 1, // POWER_REQUEST_CONTEXT_SIMPLE_STRING
            Reason = "MeetingRecorder 正在录制或保存会议"
        };
        _handle = PowerCreateRequest(ref context);
        if (_handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error);
        }
        if (!PowerSetRequest(_handle, 1)) // PowerRequestSystemRequired; do not keep the display on.
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error);
        }
    }

    public void Dispose() => _handle.Dispose(); // Closing the last handle releases all requests.

    // REASON_CONTEXT includes a union whose full x64 size is 24 bytes (32 with the header).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Size = 32)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string Reason;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeFileHandle handle, int requestType);
}
