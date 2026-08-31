using System.Diagnostics;
using System.IO;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace MeetingRecorder.App.Services;

public sealed class DesktopDialogService : IDesktopDialogService
{
    private readonly Func<Window?> _owner;

    public DesktopDialogService(Func<Window?> owner)
    {
        _owner = owner;
    }

    public bool ShowRecordingNotice()
    {
        var dialog = new RecordingNoticeWindow { Owner = _owner() };
        return dialog.ShowDialog() == true;
    }

    public HistoryDeleteChoice AskDeleteHistoryItem(string fileName)
    {
        var dialog = new DeleteRecordingDialog(fileName) { Owner = _owner() };
        var confirmed = dialog.ShowDialog() == true;
        return new HistoryDeleteChoice(confirmed, confirmed && dialog.DeleteLocalFile, confirmed && dialog.RememberChoice);
    }

    public bool Confirm(string title, string message) =>
        System.Windows.MessageBox.Show(_owner(), message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        System.Windows.MessageBox.Show(_owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInformation(string title, string message) =>
        System.Windows.MessageBox.Show(_owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public string? SelectFolder(string initialDirectory)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择会议录制文件保存目录",
            SelectedPath = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };
        return dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public void ShowInFolder(string path)
    {
        if (!File.Exists(path))
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null) OpenFolder(directory);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}
