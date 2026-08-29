using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using MeetingRecorder.App.Interop;
using MeetingRecorder.App.Services;
using MeetingRecorder.App.ViewModels;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IDesktopDialogService _dialogs;
    private GlobalHotKeyManager? _hotKeys;
    private TrayIconController? _trayIcon;
    private bool _exitRequested;
    private bool _shownTrayNotice;

    public MainWindow(MainViewModel viewModel, IDesktopDialogService dialogs)
    {
        _viewModel = viewModel;
        _dialogs = dialogs;
        DataContext = viewModel;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
        _viewModel.HotKeysChanged += OnHotKeysChanged;
        _viewModel.RecordingActivityChanged += OnRecordingActivityChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _viewModel.WindowToExclude = handle;
        _hotKeys = new GlobalHotKeyManager(handle);
        _hotKeys.StartStopPressed += async (_, _) => await _viewModel.ExecutePrimaryAsync();
        _hotKeys.PauseResumePressed += async (_, _) => await _viewModel.ExecutePauseResumeAsync();
        _trayIcon = new TrayIconController(ShowFromTray, _viewModel.ExecutePrimaryAsync, _viewModel.ExecutePauseResumeAsync, ExitAsync);
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        await _viewModel.InitializeAsync();
        TryRegisterHotKeys(showError: true);
        if (!await _viewModel.EnsureRecordingNoticeAsync()) await ExitAsync();
    }

    private void OnHotKeysChanged(object? sender, EventArgs eventArgs) => TryRegisterHotKeys(showError: true);

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
        if (WindowState != WindowState.Minimized) return;
        Hide();
        if (_shownTrayNotice) return;
        _trayIcon?.ShowMinimizedNotice();
        _shownTrayNotice = true;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_exitRequested) return;
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
        System.Windows.Application.Current.Shutdown();
    }
}
