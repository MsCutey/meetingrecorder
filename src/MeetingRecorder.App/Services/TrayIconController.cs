using System.Drawing;
using WinForms = System.Windows.Forms;

namespace MeetingRecorder.App.Services;

public sealed class TrayIconController : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;

    public TrayIconController(Action show, Func<Task> startStop, Func<Task> pauseResume, Func<Task> exit)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => show());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("开始 / 停止录制", null, async (_, _) => await startStop());
        menu.Items.Add("暂停 / 恢复", null, async (_, _) => await pauseResume());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await exit());

        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "MeetingRecorder 会议录屏",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => show();
    }

    public void ShowMinimizedNotice() =>
        _notifyIcon.ShowBalloonTip(2500, "MeetingRecorder", "软件仍在托盘运行，可用全局热键控制录制。", WinForms.ToolTipIcon.Info);

    public void SetRecording(bool recording)
    {
        _notifyIcon.Text = recording ? "MeetingRecorder · 正在录制" : "MeetingRecorder 会议录屏";
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
