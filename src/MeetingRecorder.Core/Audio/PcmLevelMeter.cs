namespace MeetingRecorder.Core.Audio;

public static class PcmLevelMeter
{
    public static double CalculatePeak(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length < 2) return 0;
        var peak = 0;
        for (var index = 0; index + 1 < pcm16.Length; index += 2)
        {
            var sample = Math.Abs((int)BitConverter.ToInt16(pcm16.Slice(index, 2)));
            if (sample > peak) peak = sample;
        }
        return Math.Clamp(peak / 32768d, 0, 1);
    }
}
