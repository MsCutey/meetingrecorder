using System.Diagnostics;
using ScreenRecorderLib;

namespace MeetingRecorder.Engine.Recording;

internal static class NativeMicrophoneControl
{
    // ScreenRecorderLib 6.6.0 rejects dynamic options while Paused. The caller mutes
    // before Pause, remembers changes while paused, then applies them after Resume.
    public static async Task ApplyAfterResumeAsync(Recorder recorder, bool muted, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (recorder.GetDynamicOptionsBuilder()
                .SetDynamicAudioOptions(new DynamicAudioOptions { InputVolume = muted ? 0f : 1f }).Apply()) return;
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(2))
                throw new InvalidOperationException("恢复后无法应用麦克风状态，已停止录制并保留恢复文件。");
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }
}
