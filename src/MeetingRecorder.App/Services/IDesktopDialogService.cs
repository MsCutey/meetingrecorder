namespace MeetingRecorder.App.Services;

public interface IDesktopDialogService
{
    bool ShowRecordingNotice();
    bool Confirm(string title, string message);
    void ShowError(string title, string message);
    void ShowInformation(string title, string message);
    string? SelectFolder(string initialDirectory);
    void OpenFolder(string path);
    void ShowInFolder(string path);
}
