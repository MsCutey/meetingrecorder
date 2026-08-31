using System.Windows;
using System.Reflection;
using MeetingRecorder.App.Services;
using MeetingRecorder.App.ViewModels;
using MeetingRecorder.App.Interop;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Recording;

namespace MeetingRecorder.App;

public partial class App : System.Windows.Application
{
    private MainViewModel? _viewModel;
    private RollingFileLogger? _logger;

    private void OnStartup(object sender, StartupEventArgs eventArgs)
    {
        try
        {
            var repository = new AppRepository();
            _logger = new RollingFileLogger(System.IO.Path.Combine(repository.Paths.LogsDirectory, "app.log"));
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
            _logger.Info("app-start", $"version={version}; os={Environment.OSVersion.VersionString}; x64={Environment.Is64BitProcess}");
            DispatcherUnhandledException += (_, unhandled) =>
            {
                _logger.Error("dispatcher-unhandled", "界面线程发生未处理异常。", unhandled.Exception);
                System.Windows.MessageBox.Show($"MeetingRecorder 遇到错误，但主界面将保持运行：\n\n{unhandled.Exception.Message}", "运行错误", MessageBoxButton.OK, MessageBoxImage.Error);
                unhandled.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, unobserved) =>
            {
                _logger.Error("task-unobserved", "发生未观察到的后台任务异常。", unobserved.Exception);
                unobserved.SetObserved();
            };
            MainWindow? window = null;
            var dialogs = new DesktopDialogService(() => window);
            var presentationCoordinator = new WindowCapturePresentationCoordinator(Dispatcher, _logger);
            var recordingService = new WorkerRecordingService(
                repository,
                logger: _logger,
                presentationCoordinator: presentationCoordinator);
            _viewModel = new MainViewModel(recordingService, repository, dialogs);
            window = new MainWindow(_viewModel, dialogs, _logger, presentationCoordinator);
            MainWindow = window;
            window.Show();
        }
        catch (Exception error)
        {
            _logger?.Error("app-start-failed", "主界面初始化失败。", error);
            System.Windows.MessageBox.Show($"MeetingRecorder 无法启动：\n\n{error.Message}", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
