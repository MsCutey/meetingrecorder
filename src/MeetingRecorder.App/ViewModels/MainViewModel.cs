using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeetingRecorder.App.Services;
using MeetingRecorder.Core.Abstractions;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IRecordingService _recordingService;
    private readonly IAppRepository _repository;
    private readonly IDesktopDialogService _dialogs;
    private readonly SynchronizationContext _uiContext;
    private AppSettings _loadedSettings = new();
    private RecordingMode _selectedMode = RecordingMode.VideoAndAudio;
    private VideoQualityPreset _selectedQuality = VideoQualityPreset.FullHd;
    private CaptureTargetInfo? _selectedTarget;
    private AudioDeviceInfo? _selectedSystemAudioDevice;
    private AudioDeviceInfo? _selectedMicrophoneDevice;
    private bool _captureSystemAudio = true;
    private bool _captureMicrophone = true;
    private bool _excludeMainWindowFromDisplayCapture;
    private bool _hasAcceptedCaptureProtectionWarning;
    private string _outputDirectory = AppSettings.DefaultOutputDirectory();
    private int _countdownSeconds = 3;
    private string _startStopHotKey = "Ctrl+Shift+F9";
    private string _pauseResumeHotKey = "Ctrl+Shift+F10";
    private string _micToggleHotKey = "Ctrl+Shift+F11";
    private bool _startMicrophoneMuted;
    private RecordingState _state = RecordingState.Idle;
    private string _statusText = "准备就绪";
    private string? _warningText;
    private string _elapsedText = "00:00:00";
    private string _fileSizeText = "0 B";
    private string _diskTimeText = "—";
    private double _systemAudioLevel;
    private double _microphoneLevel;
    private ImageSource? _previewImage;
    private bool _isLoading = true;
    private bool _isInitialized;
    private bool _isSaving;
    private bool _disposed;
    private bool _previewEnabled = true;
    private bool _rememberHistoryDeleteChoice;
    private bool _deleteLocalFileWhenDeletingHistory;
    private int _previewUpdatePending;
    private bool _microphoneToggleAvailable;
    private bool _microphoneLive = true;
    private readonly HashSet<string> _dismissedRecoveryIds = new(StringComparer.Ordinal);

    public MainViewModel(IRecordingService recordingService, IAppRepository repository, IDesktopDialogService dialogs)
    {
        _recordingService = recordingService;
        _repository = repository;
        _dialogs = dialogs;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _recordingService.StateChanged += OnRecordingStateChanged;
        _recordingService.ProgressChanged += OnRecordingProgressChanged;
        _recordingService.PreviewFrameAvailable += OnPreviewFrameAvailable;
        _recordingService.RecordingCompleted += OnRecordingCompleted;

        PrimaryCommand = new AsyncRelayCommand(_ => ExecutePrimaryAsync(), _ => State != RecordingState.Finalizing && !IsLoading);
        PauseResumeCommand = new AsyncRelayCommand(_ => ExecutePauseResumeAsync(), _ => State is RecordingState.Recording or RecordingState.Paused);
        MicToggleCommand = new AsyncRelayCommand(_ => ToggleMicrophoneAsync(), _ => MicrophoneToggleEnabled);
        RefreshDevicesCommand = new AsyncRelayCommand(_ => RefreshDevicesAsync(), _ => IsEditingEnabled && !IsLoading);
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutput(), _ => IsEditingEnabled);
        OpenOutputCommand = new RelayCommand(_ => _dialogs.OpenFolder(OutputDirectory));
        OpenHistoryItemCommand = new RelayCommand(OpenHistoryItem);
        RecoverCommand = new AsyncRelayCommand(RecoverAsync, _ => IsEditingEnabled);
        DeleteHistoryItemCommand = new AsyncRelayCommand(DeleteHistoryItemAsync);
        DismissRecoveryCommand = new RelayCommand(_ => DismissRecovery(), _ => RecoveryCandidates.Count > 0);
    }

    public ObservableCollection<CaptureTargetInfo> CaptureTargets { get; } = [];
    public ObservableCollection<AudioDeviceInfo> SystemAudioDevices { get; } = [];
    public ObservableCollection<AudioDeviceInfo> MicrophoneDevices { get; } = [];
    public ObservableCollection<RecordingHistoryRow> History { get; } = [];
    public ObservableCollection<RecoveryCandidate> RecoveryCandidates { get; } = [];
    public IReadOnlyList<Choice<VideoQualityPreset>> QualityChoices { get; } =
    [
        new(VideoQualityPreset.FullHd, "最高 1080p · 保持来源比例"),
        new(VideoQualityPreset.Hd, "最高 720p · 保持来源比例"),
        new(VideoQualityPreset.Source, "源分辨率")
    ];
    public IReadOnlyList<int> CountdownChoices { get; } = [0, 3, 5, 10];

    public AsyncRelayCommand PrimaryCommand { get; }
    public AsyncRelayCommand PauseResumeCommand { get; }
    public AsyncRelayCommand MicToggleCommand { get; }
    public AsyncRelayCommand RefreshDevicesCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public RelayCommand OpenHistoryItemCommand { get; }
    public AsyncRelayCommand RecoverCommand { get; }
    public AsyncRelayCommand DeleteHistoryItemCommand { get; }
    public RelayCommand DismissRecoveryCommand { get; }

    public RecordingMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!SetProperty(ref _selectedMode, value)) return;
            OnPropertyChanged(nameof(IsVideoMode));
            OnPropertyChanged(nameof(IsAudioOnlyMode));
            OnPropertyChanged(nameof(ModeDescription));
            OnPropertyChanged(nameof(IsDisplayCaptureSelected));
            RefreshCommandStates();
            ScheduleSettingsSave();
        }
    }

    public bool IsVideoMode => SelectedMode == RecordingMode.VideoAndAudio;
    public bool IsAudioOnlyMode => SelectedMode == RecordingMode.AudioOnly;
    public string ModeDescription => IsVideoMode
        ? "捕获指定显示器或窗口，同时收录所选声音"
        : "只生成 AAC/M4A 音频，不访问真实屏幕";

    public VideoQualityPreset SelectedQuality
    {
        get => _selectedQuality;
        set { if (SetProperty(ref _selectedQuality, value)) ScheduleSettingsSave(); }
    }

    public CaptureTargetInfo? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value)) return;
            OnPropertyChanged(nameof(IsDisplayCaptureSelected));
            RefreshCommandStates();
        }
    }

    public bool IsDisplayCaptureSelected =>
        IsVideoMode && SelectedTarget?.Kind == CaptureTargetKind.Display;

    public bool ExcludeMainWindowFromDisplayCapture
    {
        get => _excludeMainWindowFromDisplayCapture;
        set
        {
            if (value && !_hasAcceptedCaptureProtectionWarning)
            {
                var accepted = _dialogs.Confirm(
                    "启用实验性防捕获功能",
                    "启用后，MeetingRecorder 会在整屏录制期间尝试从录制画面中隐藏。\n\n" +
                    "任务栏预览可能变黑，部分 Windows 或显卡环境可能暂时看不到主窗口。停止录制或重新启动应用后会恢复。\n\n" +
                    "是否继续启用？");
                if (!accepted)
                {
                    OnPropertyChanged(nameof(ExcludeMainWindowFromDisplayCapture));
                    return;
                }
                _hasAcceptedCaptureProtectionWarning = true;
            }

            if (!SetProperty(ref _excludeMainWindowFromDisplayCapture, value)) return;
            ScheduleSettingsSave();
        }
    }

    public AudioDeviceInfo? SelectedSystemAudioDevice
    {
        get => _selectedSystemAudioDevice;
        set { if (SetProperty(ref _selectedSystemAudioDevice, value)) ScheduleSettingsSave(); }
    }

    public AudioDeviceInfo? SelectedMicrophoneDevice
    {
        get => _selectedMicrophoneDevice;
        set { if (SetProperty(ref _selectedMicrophoneDevice, value)) ScheduleSettingsSave(); }
    }

    public bool CaptureSystemAudio
    {
        get => _captureSystemAudio;
        set
        {
            if (!SetProperty(ref _captureSystemAudio, value)) return;
            RefreshCommandStates();
            ScheduleSettingsSave();
        }
    }

    public bool CaptureMicrophone
    {
        get => _captureMicrophone;
        set
        {
            if (!SetProperty(ref _captureMicrophone, value)) return;
            RefreshCommandStates();
            ScheduleSettingsSave();
        }
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (!SetProperty(ref _outputDirectory, value)) return;
            RefreshCommandStates();
            ScheduleSettingsSave();
        }
    }

    public int CountdownSeconds
    {
        get => _countdownSeconds;
        set { if (SetProperty(ref _countdownSeconds, value)) ScheduleSettingsSave(); }
    }

    public string StartStopHotKey
    {
        get => _startStopHotKey;
        set
        {
            if (!SetProperty(ref _startStopHotKey, value)) return;
            ScheduleSettingsSave();
            HotKeysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string PauseResumeHotKey
    {
        get => _pauseResumeHotKey;
        set
        {
            if (!SetProperty(ref _pauseResumeHotKey, value)) return;
            ScheduleSettingsSave();
            HotKeysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string MicToggleHotKey
    {
        get => _micToggleHotKey;
        set
        {
            if (!SetProperty(ref _micToggleHotKey, value)) return;
            ScheduleSettingsSave();
            HotKeysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool StartMicrophoneMuted
    {
        get => _startMicrophoneMuted;
        set { if (SetProperty(ref _startMicrophoneMuted, value)) ScheduleSettingsSave(); }
    }

    public bool PreviewEnabled
    {
        get => _previewEnabled;
        set
        {
            if (!SetProperty(ref _previewEnabled, value)) return;
            if (!value) PreviewImage = null;
            OnPropertyChanged(nameof(PreviewPlaceholderText));
            ScheduleSettingsSave();
        }
    }

    public bool RememberHistoryDeleteChoice
    {
        get => _rememberHistoryDeleteChoice;
        set
        {
            if (!SetProperty(ref _rememberHistoryDeleteChoice, value)) return;
            if (!value) _deleteLocalFileWhenDeletingHistory = false;
            OnPropertyChanged(nameof(RememberedDeleteChoiceText));
            ScheduleSettingsSave();
        }
    }

    public bool DeleteLocalFileWhenDeletingHistory
    {
        get => _deleteLocalFileWhenDeletingHistory;
        set
        {
            if (!SetProperty(ref _deleteLocalFileWhenDeletingHistory, value)) return;
            OnPropertyChanged(nameof(RememberedDeleteChoiceText));
            ScheduleSettingsSave();
        }
    }

    public string RememberedDeleteChoiceText => RememberHistoryDeleteChoice
        ? DeleteLocalFileWhenDeletingHistory
            ? "已记住：删除记录时同时删除本地文件"
            : "已记住：仅移除记录，保留本地文件"
        : "未记住：每次删除前都会弹窗确认";

    public RecordingState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsEditingEnabled));
            OnPropertyChanged(nameof(IsActivelyRecording));
            OnPropertyChanged(nameof(PrimaryButtonText));
            OnPropertyChanged(nameof(PauseButtonText));
            OnPropertyChanged(nameof(PreviewPlaceholderText));
            RefreshCommandStates();
        }
    }

    public bool IsEditingEnabled => State is RecordingState.Idle or RecordingState.Failed;
    public bool IsActivelyRecording => State is RecordingState.Recording or RecordingState.Paused or RecordingState.Finalizing;
    public bool MicrophoneToggleEnabled => State is RecordingState.Recording or RecordingState.Paused && _microphoneToggleAvailable;

    public bool IsMicrophoneLive
    {
        get => _microphoneLive;
        private set
        {
            if (!SetProperty(ref _microphoneLive, value)) return;
            OnPropertyChanged(nameof(MicToggleButtonText));
        }
    }

    public string MicToggleButtonText => IsMicrophoneLive ? "🎤  麦克风：开" : "🎤  麦克风：关";

    public string PrimaryButtonText => State switch
    {
        RecordingState.Idle or RecordingState.Failed => "●  开始录制",
        RecordingState.Countdown => "取消倒计时",
        RecordingState.Recording or RecordingState.Paused => "■  停止录制",
        RecordingState.Finalizing => "正在安全收尾…",
        _ => "开始录制"
    };
    public string PauseButtonText => State == RecordingState.Paused ? "▶  恢复" : "Ⅱ  暂停";

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string? WarningText { get => _warningText; private set { if (SetProperty(ref _warningText, value)) OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);
    public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }
    public string FileSizeText { get => _fileSizeText; private set => SetProperty(ref _fileSizeText, value); }
    public string DiskTimeText { get => _diskTimeText; private set => SetProperty(ref _diskTimeText, value); }
    public double SystemAudioLevel { get => _systemAudioLevel; private set => SetProperty(ref _systemAudioLevel, value); }
    public double MicrophoneLevel { get => _microphoneLevel; private set => SetProperty(ref _microphoneLevel, value); }
    public ImageSource? PreviewImage { get => _previewImage; private set { if (SetProperty(ref _previewImage, value)) OnPropertyChanged(nameof(HasPreview)); } }
    public bool HasPreview => PreviewImage is not null;
    public string PreviewPlaceholderText =>
        _previewEnabled ? "录制开始后显示低分辨率预览" : "实时预览已关闭 · 可在设置中开启";
    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) RefreshCommandStates(); } }
    public bool HasRecoveries => RecoveryCandidates.Count > 0;
    public string RecoveryBannerText => $"发现 {RecoveryCandidates.Count} 个异常中断的录制，可尝试恢复。";
    public bool IsPortable => _repository.Paths.IsPortable;
    public string StorageModeText => IsPortable ? "便携模式 · 数据保存在程序旁" : "本机模式 · 数据保存在 LocalAppData";

    public event EventHandler? HotKeysChanged;
    public event EventHandler<RecordingState>? RecordingActivityChanged;

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        IsLoading = true;
        try
        {
            _loadedSettings = await _repository.LoadSettingsAsync();
            ApplySettings(_loadedSettings);
            await Task.WhenAll(RefreshHistoryAsync(), RefreshRecoveryAsync());
            var deviceWarning = await RefreshDevicesCoreAsync();
            _isInitialized = true;
            _loadedSettings = BuildSettings();
            await _repository.SaveSettingsAsync(_loadedSettings);
            StatusText = deviceWarning is null ? "准备就绪" : "设备检查完成 · 部分功能不可用";
            WarningText = deviceWarning;
        }
        catch (Exception error)
        {
            _isInitialized = true;
            StatusText = "设备检查失败 · 主界面仍可使用";
            WarningText = error.Message;
            _dialogs.ShowError("设备检查失败", error.Message + "\n\n主界面不会退出。请检查提示后点击“刷新设备”重试。");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> EnsureRecordingNoticeAsync()
    {
        if (_loadedSettings.HasAcceptedRecordingNotice) return true;
        if (!_dialogs.ShowRecordingNotice()) return false;
        _loadedSettings = BuildSettings() with { HasAcceptedRecordingNotice = true };
        await _repository.SaveSettingsAsync(_loadedSettings);
        return true;
    }

    public async Task ExecutePrimaryAsync()
    {
        try
        {
            if (State is RecordingState.Countdown or RecordingState.Recording or RecordingState.Paused)
            {
                await _recordingService.StopAsync();
                return;
            }
            if (State == RecordingState.Finalizing) return;
            if (!await EnsureRecordingNoticeAsync()) return;

            WarningText = null;
            var request = new RecordingRequest
            {
                Mode = SelectedMode,
                CaptureTarget = SelectedTarget,
                SystemAudioDevice = SelectedSystemAudioDevice,
                MicrophoneDevice = SelectedMicrophoneDevice,
                CaptureSystemAudio = CaptureSystemAudio,
                CaptureMicrophone = CaptureMicrophone,
                Quality = SelectedQuality,
                OutputDirectory = OutputDirectory,
                CountdownSeconds = CountdownSeconds,
                PreferHardwareEncoding = true,
                ExcludeMainWindowFromDisplayCapture = ExcludeMainWindowFromDisplayCapture,
                PreviewEnabled = PreviewEnabled
            };
            request = request with
            {
                StartMicrophoneMuted = CaptureMicrophone && StartMicrophoneMuted
            };
            _microphoneToggleAvailable = CaptureMicrophone;
            IsMicrophoneLive = !request.StartMicrophoneMuted;
            RefreshCommandStates();
            await _recordingService.StartAsync(request);
        }
        catch (Exception error)
        {
            WarningText = error.Message;
            _dialogs.ShowError("录制失败", error.Message);
        }
    }

    public async Task ExecutePauseResumeAsync()
    {
        try
        {
            if (State == RecordingState.Recording) await _recordingService.PauseAsync();
            else if (State == RecordingState.Paused) await _recordingService.ResumeAsync();
        }
        catch (Exception error)
        {
            WarningText = error.Message;
            _dialogs.ShowError("操作失败", error.Message);
        }
    }

    public async Task ToggleMicrophoneAsync()
    {
        if (!MicrophoneToggleEnabled) return;
        try
        {
            await _recordingService.SetMicrophoneEnabledAsync(!IsMicrophoneLive);
            IsMicrophoneLive = !IsMicrophoneLive;
        }
        catch (Exception error)
        {
            WarningText = error.Message;
            _dialogs.ShowError("麦克风切换失败", error.Message);
        }
    }

    public async Task<bool> PrepareForExitAsync()
    {
        if (State is RecordingState.Countdown or RecordingState.Recording or RecordingState.Paused)
        {
            if (!_dialogs.Confirm("退出 MeetingRecorder", "当前录制尚未结束。是否先安全停止录制并退出？")) return false;
            try { await _recordingService.StopAsync(); }
            catch (Exception error) { _dialogs.ShowError("安全收尾失败", error.Message); }
        }
        await SaveSettingsAsync();
        return true;
    }

    public Task SaveSettingsAsync() => _repository.SaveSettingsAsync(BuildSettings());

    private async Task RefreshDevicesAsync()
    {
        IsLoading = true;
        try
        {
            var deviceWarning = await RefreshDevicesCoreAsync();
            _loadedSettings = BuildSettings();
            await _repository.SaveSettingsAsync(_loadedSettings);
            StatusText = deviceWarning is null ? "设备已刷新" : "设备已刷新 · 部分功能不可用";
            WarningText = deviceWarning;
        }
        catch (Exception error)
        {
            StatusText = "设备检查失败 · 主界面仍可使用";
            WarningText = error.Message;
            _dialogs.ShowError("刷新设备失败", error.Message);
        }
        finally { IsLoading = false; }
    }

    private async Task<string?> RefreshDevicesCoreAsync()
    {
        var previousTarget = SelectedTarget?.Id;
        var previousSystem = SelectedSystemAudioDevice?.Id ?? _loadedSettings.SystemAudioDeviceId;
        var previousMicrophone = SelectedMicrophoneDevice?.Id ?? _loadedSettings.MicrophoneDeviceId;
        var targetsTask = _recordingService.GetCaptureTargetsAsync();
        var systemTask = _recordingService.GetAudioDevicesAsync(AudioSourceKind.SystemAudio);
        var microphoneTask = _recordingService.GetAudioDevicesAsync(AudioSourceKind.Microphone);
        await Task.WhenAll(targetsTask, systemTask, microphoneTask);

        Replace(CaptureTargets, await targetsTask);
        Replace(SystemAudioDevices, await systemTask);
        Replace(MicrophoneDevices, await microphoneTask);
        SelectedTarget = CaptureTargets.FirstOrDefault(item => item.Id == previousTarget) ?? CaptureTargets.FirstOrDefault();
        SelectedSystemAudioDevice = SystemAudioDevices.FirstOrDefault(item => item.Id == previousSystem)
                                    ?? SystemAudioDevices.FirstOrDefault(item => item.IsDefault)
                                    ?? SystemAudioDevices.FirstOrDefault();
        SelectedMicrophoneDevice = MicrophoneDevices.FirstOrDefault(item => item.Id == previousMicrophone)
                                   ?? MicrophoneDevices.FirstOrDefault(item => item.IsDefault)
                                   ?? MicrophoneDevices.FirstOrDefault();
        if (MicrophoneDevices.Count == 0) CaptureMicrophone = false;
        if (SystemAudioDevices.Count == 0) CaptureSystemAudio = false;
        var warnings = new List<string>();
        if (CaptureTargets.Count == 0) warnings.Add("未发现可录制的显示器或窗口，视频模式暂不可用。");
        if (SystemAudioDevices.Count == 0) warnings.Add("未发现系统声音输出设备。");
        if (MicrophoneDevices.Count == 0) warnings.Add("未发现可用麦克风。");
        return warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
    }

    private async Task RefreshHistoryAsync()
    {
        var items = await _repository.LoadHistoryAsync();
        History.Clear();
        foreach (var item in items) History.Add(new RecordingHistoryRow(item));
    }

    private async Task RefreshRecoveryAsync()
    {
        var items = await _repository.ScanRecoveryAsync();
        RecoveryCandidates.Clear();
        foreach (var item in items)
        {
            if (_dismissedRecoveryIds.Contains(item.Id)) continue;
            RecoveryCandidates.Add(item);
        }
        OnPropertyChanged(nameof(HasRecoveries));
        OnPropertyChanged(nameof(RecoveryBannerText));
    }

    private async Task RecoverAsync(object? parameter)
    {
        if (parameter is not RecoveryCandidate candidate) return;
        try
        {
            var recovered = await _repository.RecoverAsync(candidate);
            RecoveryCandidates.Remove(candidate);
            OnPropertyChanged(nameof(HasRecoveries));
            OnPropertyChanged(nameof(RecoveryBannerText));
            _dialogs.ShowInformation("恢复完成", $"已保留可恢复媒体：\n{recovered}\n\n异常中断文件可能仍需播放器或媒体修复工具重建索引。");
        }
        catch (Exception error)
        {
            _dialogs.ShowError("恢复失败", error.Message);
        }
    }

    private void DismissRecovery()
    {
        foreach (var candidate in RecoveryCandidates) _dismissedRecoveryIds.Add(candidate.Id);
        RecoveryCandidates.Clear();
        OnPropertyChanged(nameof(HasRecoveries));
        OnPropertyChanged(nameof(RecoveryBannerText));
        DismissRecoveryCommand.RaiseCanExecuteChanged();
    }

    private async Task DeleteHistoryItemAsync(object? parameter)
    {
        if (parameter is not RecordingHistoryRow row) return;
        var deleteLocalFile = DeleteLocalFileWhenDeletingHistory;
        if (!RememberHistoryDeleteChoice)
        {
            var choice = _dialogs.AskDeleteHistoryItem(row.FileName);
            if (!choice.Confirmed) return;
            deleteLocalFile = choice.DeleteLocalFile;
            if (choice.RememberChoice)
            {
                RememberHistoryDeleteChoice = true;
                DeleteLocalFileWhenDeletingHistory = deleteLocalFile;
            }
        }

        try
        {
            await _repository.RemoveHistoryAsync(row.Item.Id);
            History.Remove(row);
            if (deleteLocalFile && File.Exists(row.Item.FilePath))
            {
                try { File.Delete(row.Item.FilePath); }
                catch (Exception error) { _dialogs.ShowError("删除本地文件失败", $"记录已移除，但文件仍在：\n{row.Item.FilePath}\n\n{error.Message}"); }
            }
        }
        catch (Exception error)
        {
            _dialogs.ShowError("删除记录失败", error.Message);
        }
    }

    private void BrowseOutput()
    {
        var selected = _dialogs.SelectFolder(OutputDirectory);
        if (!string.IsNullOrWhiteSpace(selected)) OutputDirectory = selected;
    }

    private void OpenHistoryItem(object? parameter)
    {
        if (parameter is RecordingHistoryRow row) _dialogs.ShowInFolder(row.Item.FilePath);
    }

    private void OnRecordingStateChanged(object? sender, RecordingStateChangedEventArgs eventArgs) => RunOnUi(() =>
    {
        State = eventArgs.Current;
        StatusText = eventArgs.Current switch
        {
            RecordingState.Idle => "准备就绪",
            RecordingState.Countdown => "录制即将开始",
            RecordingState.Recording => "正在录制",
            RecordingState.Paused => "已暂停",
            RecordingState.Finalizing => "正在写入媒体索引，请勿关机",
            RecordingState.Failed => "录制异常 · 已保留恢复文件",
            _ => eventArgs.Current.ToString()
        };
        if (eventArgs.Current is RecordingState.Idle or RecordingState.Failed)
        {
            PreviewImage = null;
            OnPropertyChanged(nameof(PreviewPlaceholderText));
            _microphoneToggleAvailable = false;
            IsMicrophoneLive = true;
        }
        if (eventArgs.Current == RecordingState.Failed) _ = RefreshRecoveryAsync();
        RecordingActivityChanged?.Invoke(this, eventArgs.Current);
    });

    private void OnRecordingProgressChanged(object? sender, RecordingProgressEventArgs eventArgs) => RunOnUi(() =>
    {
        var progress = eventArgs.Progress;
        ElapsedText = FormatDuration(progress.Elapsed);
        FileSizeText = FormatBytes(progress.FileSizeBytes);
        DiskTimeText = progress.EstimatedDiskTimeRemaining is null
            ? "—"
            : progress.EstimatedDiskTimeRemaining.Value.TotalHours >= 100
                ? "> 99 小时"
                : FormatDuration(progress.EstimatedDiskTimeRemaining.Value);
        SystemAudioLevel = progress.SystemAudioLevel * 100;
        MicrophoneLevel = progress.MicrophoneLevel * 100;
        WarningText = progress.Warning;
    });

    private void OnPreviewFrameAvailable(object? sender, PreviewFrameEventArgs eventArgs)
    {
        if (!_previewEnabled) return;
        if (Interlocked.Exchange(ref _previewUpdatePending, 1) != 0) return;
        RunOnUi(() =>
        {
            try
            {
                var frame = eventArgs.Frame;
                var stride = Math.Abs(frame.Stride);
                var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra32, stride);
                image.Freeze();
                PreviewImage = image;
            }
            catch (ArgumentException)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _previewUpdatePending, 0);
            }
        });
    }

    private void OnRecordingCompleted(object? sender, RecordingCompletedEventArgs eventArgs) => RunOnUi(async () =>
    {
        await RefreshHistoryAsync();
        WarningText = null;
    });

    private void ApplySettings(AppSettings settings)
    {
        _selectedMode = settings.Mode;
        _selectedQuality = settings.Quality;
        _captureSystemAudio = settings.CaptureSystemAudio;
        _captureMicrophone = settings.CaptureMicrophone;
        _excludeMainWindowFromDisplayCapture = settings.ExcludeMainWindowFromDisplayCapture;
        _hasAcceptedCaptureProtectionWarning = settings.HasAcceptedCaptureProtectionWarning;
        _outputDirectory = settings.OutputDirectory;
        _countdownSeconds = settings.CountdownSeconds;
        _startStopHotKey = settings.StartStopHotKey;
        _pauseResumeHotKey = settings.PauseResumeHotKey;
        _micToggleHotKey = settings.MicToggleHotKey;
        _startMicrophoneMuted = settings.StartMicrophoneMuted;
        _previewEnabled = settings.PreviewEnabled;
        _rememberHistoryDeleteChoice = settings.RememberHistoryDeleteChoice;
        _deleteLocalFileWhenDeletingHistory = settings.DeleteLocalFileWhenDeletingHistory;
        OnPropertyChanged(string.Empty);
    }

    private AppSettings BuildSettings() => _loadedSettings with
    {
        Mode = SelectedMode,
        Quality = SelectedQuality,
        CaptureSystemAudio = CaptureSystemAudio,
        CaptureMicrophone = CaptureMicrophone,
        SystemAudioDeviceId = SelectedSystemAudioDevice?.Id,
        MicrophoneDeviceId = SelectedMicrophoneDevice?.Id,
        OutputDirectory = OutputDirectory,
        CountdownSeconds = CountdownSeconds,
        StartStopHotKey = StartStopHotKey,
        PauseResumeHotKey = PauseResumeHotKey,
        MicToggleHotKey = MicToggleHotKey,
        StartMicrophoneMuted = StartMicrophoneMuted,
        ExcludeMainWindowFromDisplayCapture = ExcludeMainWindowFromDisplayCapture,
        HasAcceptedCaptureProtectionWarning = _hasAcceptedCaptureProtectionWarning,
        PreviewEnabled = PreviewEnabled,
        RememberHistoryDeleteChoice = RememberHistoryDeleteChoice,
        DeleteLocalFileWhenDeletingHistory = DeleteLocalFileWhenDeletingHistory
    };

    private void ScheduleSettingsSave()
    {
        if (!_isInitialized || _isSaving) return;
        _ = PersistSettingsSafeAsync();
    }

    private async Task PersistSettingsSafeAsync()
    {
        _isSaving = true;
        try
        {
            await Task.Delay(150);
            _loadedSettings = BuildSettings();
            await _repository.SaveSettingsAsync(_loadedSettings);
        }
        catch (Exception error)
        {
            RunOnUi(() => WarningText = $"设置未保存：{error.Message}");
        }
        finally
        {
            _isSaving = false;
        }
    }

    private void RefreshCommandStates()
    {
        PrimaryCommand.RaiseCanExecuteChanged();
        PauseResumeCommand.RaiseCanExecuteChanged();
        MicToggleCommand.RaiseCanExecuteChanged();
        RefreshDevicesCommand.RaiseCanExecuteChanged();
        BrowseOutputCommand.RaiseCanExecuteChanged();
        RecoverCommand.RaiseCanExecuteChanged();
        DismissRecoveryCommand.RaiseCanExecuteChanged();
    }

    private void RunOnUi(Action action)
    {
        if (SynchronizationContext.Current == _uiContext) action();
        else _uiContext.Post(_ => action(), null);
    }

    private void RunOnUi(Func<Task> action) => _uiContext.Post(async _ => await action(), null);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.00} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024L => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B"
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _recordingService.StateChanged -= OnRecordingStateChanged;
        _recordingService.ProgressChanged -= OnRecordingProgressChanged;
        _recordingService.PreviewFrameAvailable -= OnPreviewFrameAvailable;
        _recordingService.RecordingCompleted -= OnRecordingCompleted;
        await _recordingService.DisposeAsync();
        _disposed = true;
    }
}
