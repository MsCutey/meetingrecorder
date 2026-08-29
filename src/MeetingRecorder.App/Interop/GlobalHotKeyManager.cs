using System.Runtime.InteropServices;
using System.Windows.Interop;
using MeetingRecorder.Core.Utilities;

namespace MeetingRecorder.App.Interop;

public sealed partial class GlobalHotKeyManager : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const int StartStopId = 0x4D31;
    private const int PauseResumeId = 0x4D32;
    private readonly nint _windowHandle;
    private readonly HwndSource _source;
    private bool _startRegistered;
    private bool _pauseRegistered;

    public GlobalHotKeyManager(nint windowHandle)
    {
        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle) ?? throw new InvalidOperationException("无法连接窗口消息循环。");
        _source.AddHook(WindowHook);
    }

    public event EventHandler? StartStopPressed;
    public event EventHandler? PauseResumePressed;

    public void Register(string startStop, string pauseResume)
    {
        Unregister();
        var startGesture = HotKeyGesture.Parse(startStop);
        var pauseGesture = HotKeyGesture.Parse(pauseResume);
        if (startGesture == pauseGesture) throw new InvalidOperationException("两个全局热键不能相同。");

        _startRegistered = RegisterHotKey(_windowHandle, StartStopId, (uint)startGesture.Modifiers, startGesture.VirtualKey);
        if (!_startRegistered)
        {
            throw new InvalidOperationException($"全局热键 {startStop} 已被其他程序占用。");
        }

        _pauseRegistered = RegisterHotKey(_windowHandle, PauseResumeId, (uint)pauseGesture.Modifiers, pauseGesture.VirtualKey);
        if (!_pauseRegistered)
        {
            Unregister();
            throw new InvalidOperationException($"全局热键 {pauseResume} 已被其他程序占用。");
        }
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmHotKey) return nint.Zero;
        if (wParam.ToInt32() == StartStopId) StartStopPressed?.Invoke(this, EventArgs.Empty);
        if (wParam.ToInt32() == PauseResumeId) PauseResumePressed?.Invoke(this, EventArgs.Empty);
        handled = true;
        return nint.Zero;
    }

    private void Unregister()
    {
        if (_startRegistered) UnregisterHotKey(_windowHandle, StartStopId);
        if (_pauseRegistered) UnregisterHotKey(_windowHandle, PauseResumeId);
        _startRegistered = false;
        _pauseRegistered = false;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WindowHook);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint windowHandle, int id);
}
