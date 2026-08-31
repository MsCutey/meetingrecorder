using System.Runtime.InteropServices;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Protocol;
using NAudio.CoreAudioApi;
using ScreenRecorderLib;

namespace MeetingRecorder.Engine.Discovery;

public static partial class DeviceDiscovery
{
    public static WorkerDiscoveryResult DiscoverAll(int parentProcessId, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var targets = DiscoverCaptureTargets(parentProcessId, warnings, cancellationToken);
        var systemAudio = DiscoverAudioDevices(AudioSourceKind.SystemAudio, warnings, cancellationToken);
        var microphones = DiscoverAudioDevices(AudioSourceKind.Microphone, warnings, cancellationToken);
        return new WorkerDiscoveryResult(targets, systemAudio, microphones, warnings);
    }

    private static IReadOnlyList<CaptureTargetInfo> DiscoverCaptureTargets(
        int parentProcessId,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var targets = new List<CaptureTargetInfo>();
        try
        {
            var displayNumber = 1;
            foreach (var display in Recorder.GetDisplays())
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var measured = TryMeasureSource(display);
                    var width = PositiveDimension(display.OutputSize?.Width ?? 0, display.SourceRect?.Width ?? measured?.Width ?? 0);
                    var height = PositiveDimension(display.OutputSize?.Height ?? 0, display.SourceRect?.Height ?? measured?.Height ?? 0);
                    var name = string.IsNullOrWhiteSpace(display.FriendlyName) ? $"显示器 {displayNumber}" : display.FriendlyName;
                    targets.Add(new CaptureTargetInfo(
                        display.DeviceName ?? $"display-{displayNumber}",
                        $"🖥 {name}  {FormatDimensions(width, height)}",
                        CaptureTargetKind.Display,
                        display.DeviceName,
                        0,
                        width,
                        height));
                    displayNumber++;
                }
                finally { display.Dispose(); }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            warnings.Add($"显示器枚举失败：{error.Message}");
        }

        try
        {
            foreach (var window in Recorder.GetWindows())
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (window.Handle == nint.Zero || string.IsNullOrWhiteSpace(window.Title)) continue;
                    _ = GetWindowThreadProcessId(window.Handle, out var processId);
                    if (processId == (uint)parentProcessId || processId == (uint)Environment.ProcessId) continue;
                    var measured = TryMeasureSource(window);
                    var width = PositiveDimension(window.OutputSize?.Width ?? 0, window.SourceRect?.Width ?? measured?.Width ?? 0);
                    var height = PositiveDimension(window.OutputSize?.Height ?? 0, window.SourceRect?.Height ?? measured?.Height ?? 0);
                    targets.Add(new CaptureTargetInfo(
                        $"window-{window.Handle.ToInt64():X}",
                        $"▣ {window.Title}  {FormatDimensions(width, height)}",
                        CaptureTargetKind.Window,
                        null,
                        window.Handle.ToInt64(),
                        width,
                        height,
                        (int)processId));
                }
                finally { window.Dispose(); }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            warnings.Add($"窗口枚举失败：{error.Message}");
        }
        return targets;
    }

    private static IReadOnlyList<AudioDeviceInfo> DiscoverAudioDevices(
        AudioSourceKind kind,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var flow = kind == AudioSourceKind.SystemAudio ? DataFlow.Render : DataFlow.Capture;
            string? defaultId = null;
            try
            {
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = defaultDevice.ID;
            }
            catch { }

            var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            var result = new List<AudioDeviceInfo>();
            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (device)
                {
                    result.Add(new AudioDeviceInfo(
                        device.ID,
                        device.ID,
                        device.FriendlyName,
                        kind,
                        string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
            }
            return result
                .OrderByDescending(device => device.IsDefault)
                .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            warnings.Add($"{(kind == AudioSourceKind.SystemAudio ? "系统声音" : "麦克风")}设备枚举失败：{error.Message}");
            return [];
        }
    }

    private static ScreenSize? TryMeasureSource(RecordingSourceBase source)
    {
        try { return Recorder.GetOutputDimensionsForRecordingSources([source]).CombinedOutputSize; }
        catch { return null; }
    }

    private static int PositiveDimension(double primary, double fallback)
    {
        var value = primary > 0 ? primary : fallback;
        return value > 0 ? (int)Math.Round(value) : 0;
    }

    private static string FormatDimensions(int width, int height) => width > 0 && height > 0 ? $"({width}×{height})" : string.Empty;

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}
