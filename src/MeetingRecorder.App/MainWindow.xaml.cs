using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using MeetingRecorder.App.Interop;
using MeetingRecorder.App.Services;
using MeetingRecorder.App.ViewModels;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Utilities;

namespace MeetingRecorder.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IDesktopDialogService _dialogs;
    private readonly RollingFileLogger _logger;
    private readonly WindowCapturePresentationCoordinator _presentationCoordinator;
    private GlobalHotKeyManager? _hotKeys;
    private TrayIconController? _trayIcon;
    private bool _exitRequested;
    private bool _shownTrayNotice;
    private bool _contentRenderedLogged;
    private WindowState _restoreWindowState = WindowState.Normal;

    public MainWindow(
        MainViewModel viewModel,
        IDesktopDialogService dialogs,
        RollingFileLogger logger,
        WindowCapturePresentationCoordinator presentationCoordinator)
    {
        _viewModel = viewModel;
        _dialogs = dialogs;
        _logger = logger;
        _presentationCoordinator = presentationCoordinator;
        DataContext = viewModel;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
        _viewModel.HotKeysChanged += OnHotKeysChanged;
        _viewModel.RecordingActivityChanged += OnRecordingActivityChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _presentationCoordinator.SetWindowHandle(handle);
        _logger.Info("capture-protection", "启动阶段不启用界面防捕获。");
        _hotKeys = new GlobalHotKeyManager(handle);
        _hotKeys.StartStopPressed += async (_, _) => await _viewModel.ExecutePrimaryAsync();
        _hotKeys.PauseResumePressed += async (_, _) => await _viewModel.ExecutePauseResumeAsync();
        _trayIcon = new TrayIconController(ShowFromTray, _viewModel.ExecutePrimaryAsync, _viewModel.ExecutePauseResumeAsync, ExitAsync);
    }

    private void OnContentRendered(object? sender, EventArgs eventArgs)
    {
        if (_contentRenderedLogged) return;
        _contentRenderedLogged = true;
        _logger.Info("main-window-rendered", $"visible={IsVisible}; state={WindowState}; width={ActualWidth:0}; height={ActualHeight:0}");
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        await _viewModel.InitializeAsync();
        TryRegisterHotKeys(showError: true);
        if (!await _viewModel.EnsureRecordingNoticeAsync()) await ExitAsync();
    }

    private void OnHotKeysChanged(object? sender, EventArgs eventArgs) => TryRegisterHotKeys(showError: true);

    private void OpenSettingsClick(object sender, RoutedEventArgs eventArgs) =>
        new SettingsWindow(_viewModel) { Owner = this }.ShowDialog();

    private void TryRegisterHotKeys(bool showError)
    {
        if (_hotKeys is null) return;
        try
        {
            _hotKeys.Register(_viewModel.StartStopHotKey, _viewModel.PauseResumeHotKey);
        }
        catch (Exception error)
        {
            if (showError) _dialogs.ShowError("全局热键不可用", error.Message + "\n\n请在“保存与控制”中更换热键。");
        }
    }

    private void OnRecordingActivityChanged(object? sender, RecordingState state) =>
        _trayIcon?.SetRecording(state is RecordingState.Recording or RecordingState.Paused or RecordingState.Finalizing);

    private void OnWindowStateChanged(object? sender, EventArgs eventArgs)
    {
        if (WindowState != WindowState.Minimized)
        {
            _restoreWindowState = WindowState;
            return;
        }
        if (!RecordingWindowPolicy.ShouldEnterTray(WindowTrayAction.UserMinimized)) return;
        Hide();
        if (_shownTrayNotice) return;
        _trayIcon?.ShowMinimizedNotice();
        _shownTrayNotice = true;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = _restoreWindowState;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_exitRequested) return;
        if (!RecordingWindowPolicy.ShouldEnterTray(WindowTrayAction.UserClosed)) return;
        eventArgs.Cancel = true;
        Hide();
        if (_shownTrayNotice) return;
        _trayIcon?.ShowMinimizedNotice();
        _shownTrayNotice = true;
    }

    private async Task ExitAsync()
    {
        if (_exitRequested) return;
        if (!await _viewModel.PrepareForExitAsync()) return;
        _exitRequested = true;
        _viewModel.HotKeysChanged -= OnHotKeysChanged;
        _viewModel.RecordingActivityChanged -= OnRecordingActivityChanged;
        _hotKeys?.Dispose();
        _trayIcon?.Dispose();
        await _viewModel.DisposeAsync();
        try { await _presentationCoordinator.RestoreAsync(); }
        catch (Exception error) { _logger.Warning("capture-protection", $"退出前恢复窗口捕获状态失败：{error.Message}"); }
        System.Windows.Application.Current.Shutdown();
    }
}
