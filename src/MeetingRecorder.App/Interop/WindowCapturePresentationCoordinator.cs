using System.Windows.Threading;
using MeetingRecorder.Core.Abstractions;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Utilities;

namespace MeetingRecorder.App.Interop;

public sealed class WindowCapturePresentationCoordinator(
    Dispatcher dispatcher,
    RollingFileLogger logger) : IRecordingPresentationCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private nint _windowHandle;
    private bool _isApplied;

    public void SetWindowHandle(nint windowHandle)
    {
        if (windowHandle == nint.Zero) throw new ArgumentException("窗口句柄无效。", nameof(windowHandle));
        Interlocked.Exchange(ref _windowHandle, windowHandle);
    }

    public async Task<RecordingPresentationDecision> PrepareAsync(
        RecordingRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isApplied) await RestoreCoreAsync().ConfigureAwait(false);
            if (!RecordingPresentationPolicy.ShouldAttemptCaptureProtection(request))
            {
                var preview = RecordingPresentationPolicy.ShouldEnablePreview(request, captureProtectionApplied: false);
                logger.Info("capture-protection", $"本次录制不启用界面防捕获；mode={request.Mode}; target={request.CaptureTarget?.Kind}; preview={preview}");
                return new RecordingPresentationDecision(false, preview);
            }

            var handle = Interlocked.CompareExchange(ref _windowHandle, nint.Zero, nint.Zero);
            if (handle == nint.Zero)
            {
                const string warning = "主窗口尚未完成初始化，无法启用界面防捕获。";
                logger.Warning("capture-protection", warning);
                return new RecordingPresentationDecision(false, RecordingPresentationPolicy.ShouldEnablePreview(request, captureProtectionApplied: false), warning);
            }

            var operation = dispatcher.InvokeAsync(
                () => WindowCaptureProtection.Apply(handle),
                DispatcherPriority.Send,
                cancellationToken);
            var result = await operation.Task.ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                var warning = $"无法启用界面防捕获并继续录制：{result.Warning}";
                logger.Warning("capture-protection", warning);
                return new RecordingPresentationDecision(false, RecordingPresentationPolicy.ShouldEnablePreview(request, captureProtectionApplied: false), warning);
            }

            _isApplied = true;
            var previewEnabled = RecordingPresentationPolicy.ShouldEnablePreview(request, captureProtectionApplied: true);
            var compatibilityWarning = result.UsedCompatibilityMode
                ? "界面防捕获使用兼容模式；录制期间任务栏预览可能变黑。"
                : null;
            logger.Info("capture-protection", $"界面防捕获已为本次整屏录制启用；compatibility={result.UsedCompatibilityMode}; preview={previewEnabled}");
            return new RecordingPresentationDecision(true, previewEnabled, compatibilityWarning);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await RestoreCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RestoreCoreAsync()
    {
        if (!_isApplied) return;
        var handle = Interlocked.CompareExchange(ref _windowHandle, nint.Zero, nint.Zero);
        if (handle == nint.Zero)
        {
            _isApplied = false;
            return;
        }

        var operation = dispatcher.InvokeAsync(
            () => WindowCaptureProtection.Remove(handle),
            DispatcherPriority.Send);
        var result = await operation.Task.ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"无法恢复主窗口捕获状态：{result.Warning}");
        }
        _isApplied = false;
        logger.Info("capture-protection", "主窗口已恢复为正常显示和捕获状态。");
    }
}
