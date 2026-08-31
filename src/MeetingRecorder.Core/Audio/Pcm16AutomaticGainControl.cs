using System.Buffers.Binary;

namespace MeetingRecorder.Core.Audio;

/// <summary>
/// 为系统回放声提供轻量的块级自动增益。它不修改 Windows 端点音量，
/// 并在应用增益前检查当前块峰值，避免突然出现的大音量造成削波。
/// </summary>
public sealed class Pcm16AutomaticGainControl
{
    private readonly double _targetPeak;
    private readonly double _maximumGain;
    private readonly double _noiseFloor;
    private readonly double _gainRiseSmoothing;
    private double _currentGain = 1d;
    private bool _hasObservedSignal;

    public Pcm16AutomaticGainControl(
        double targetPeak = 0.72d,
        double maximumGain = 16d,
        double noiseFloor = 0.0015d,
        double gainRiseSmoothing = 0.10d)
    {
        if (targetPeak is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(targetPeak));
        if (maximumGain < 1) throw new ArgumentOutOfRangeException(nameof(maximumGain));
        if (noiseFloor is < 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(noiseFloor));
        if (gainRiseSmoothing is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(gainRiseSmoothing));
        _targetPeak = targetPeak;
        _maximumGain = maximumGain;
        _noiseFloor = noiseFloor;
        _gainRiseSmoothing = gainRiseSmoothing;
    }

    public double CurrentGain => _currentGain;

    public double ProcessInPlace(Span<byte> pcm16)
    {
        if (pcm16.Length % sizeof(short) != 0)
        {
            throw new ArgumentException("PCM16 缓冲区长度必须是偶数。", nameof(pcm16));
        }
        if (pcm16.IsEmpty) return _currentGain;

        var peak = 0;
        for (var offset = 0; offset < pcm16.Length; offset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm16[offset..]);
            peak = Math.Max(peak, sample == short.MinValue ? 32_768 : Math.Abs((int)sample));
        }

        var normalizedPeak = peak / 32_768d;
        if (normalizedPeak < _noiseFloor)
        {
            // 不把静音和底噪放大；真实信号再次出现时会根据该块重新计算安全增益。
            return _currentGain;
        }

        var desiredGain = Math.Clamp(_targetPeak / normalizedPeak, 1d, _maximumGain);
        if (!_hasObservedSignal || desiredGain <= _currentGain)
        {
            // 音量突然升高时立即降低增益，当前块也不会削波。
            _currentGain = desiredGain;
            _hasObservedSignal = true;
        }
        else
        {
            // 音量降低时平滑增加增益，避免每个 20 ms 块之间产生明显抽吸感。
            _currentGain += (desiredGain - _currentGain) * _gainRiseSmoothing;
        }

        for (var offset = 0; offset < pcm16.Length; offset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm16[offset..]);
            var amplified = (int)Math.Round(sample * _currentGain, MidpointRounding.AwayFromZero);
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm16[offset..],
                (short)Math.Clamp(amplified, short.MinValue, short.MaxValue));
        }
        return _currentGain;
    }

    public void Reset()
    {
        _currentGain = 1d;
        _hasObservedSignal = false;
    }
}
