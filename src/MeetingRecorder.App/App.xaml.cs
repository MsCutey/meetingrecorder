using System.Windows;
using MeetingRecorder.App.Services;
using MeetingRecorder.App.ViewModels;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Recording;

namespace MeetingRecorder.App;

public partial class App : System.Windows.Application
{
    private MainViewModel? _viewModel;

    private void OnStartup(object sender, StartupEventArgs eventArgs)
    {
        try
        {
            var repository = new AppRepository();
            MainWindow? window = null;
            var dialogs = new DesktopDialogService(() => window);
            var recordingService = new ScreenRecordingService(repository);
            _viewModel = new MainViewModel(recordingService, repository, dialogs);
            window = new MainWindow(_viewModel, dialogs);
            MainWindow = window;
            window.Show();
        }
        catch (Exception error)
        {
            System.Windows.MessageBox.Show($"MeetingRecorder 无法启动：\n\n{error.Message}", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
