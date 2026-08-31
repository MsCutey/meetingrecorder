using System.Buffers.Binary;

namespace MeetingRecorder.Core.Audio;

public static class Pcm16StereoMixer
{
    public static void Mix(
        ReadOnlySpan<byte> systemAudio,
        ReadOnlySpan<byte> microphone,
        Span<byte> destination,
        bool hasSystemAudio,
        bool hasMicrophone)
    {
        if (destination.Length % 2 != 0) throw new ArgumentException("PCM16 缓冲区长度必须是偶数。", nameof(destination));
        if (hasSystemAudio && systemAudio.Length < destination.Length) throw new ArgumentException("系统声音缓冲区过短。", nameof(systemAudio));
        if (hasMicrophone && microphone.Length < destination.Length) throw new ArgumentException("麦克风缓冲区过短。", nameof(microphone));

        if (!hasSystemAudio && !hasMicrophone)
        {
            destination.Clear();
            return;
        }

        if (hasSystemAudio && !hasMicrophone)
        {
            systemAudio[..destination.Length].CopyTo(destination);
            return;
        }

        if (!hasSystemAudio)
        {
            microphone[..destination.Length].CopyTo(destination);
            return;
        }

        var absolutePeak = 0L;
        for (var offset = 0; offset < destination.Length; offset += 2)
        {
            var mixed = (long)BinaryPrimitives.ReadInt16LittleEndian(systemAudio[offset..]) +
                        BinaryPrimitives.ReadInt16LittleEndian(microphone[offset..]);
            absolutePeak = Math.Max(absolutePeak, Math.Abs(mixed));
        }

        // 两路都存在时直接相加，避免旧实现无条件 /2 导致每路都衰减 6 dB。
        // 仅在确实会削波的 20 ms 音频块上等比限幅，保持两路的相对音量。
        var limiter = absolutePeak > short.MaxValue ? short.MaxValue / (double)absolutePeak : 1d;
        for (var offset = 0; offset < destination.Length; offset += 2)
        {
            var mixed = BinaryPrimitives.ReadInt16LittleEndian(systemAudio[offset..]) +
                        BinaryPrimitives.ReadInt16LittleEndian(microphone[offset..]);
            var limited = (int)Math.Round(mixed * limiter, MidpointRounding.AwayFromZero);
            BinaryPrimitives.WriteInt16LittleEndian(destination[offset..], (short)Math.Clamp(limited, -short.MaxValue, short.MaxValue));
        }
    }
}
