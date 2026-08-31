using MeetingRecorder.Core.Audio;

namespace MeetingRecorder.Tests;

public sealed class Pcm16AutomaticGainControlTests
{
    [Fact]
    public void QuietSystemAudio_IsBoostedUpToBoundedMaximum()
    {
        var audio = Samples(1_000, -1_000);
        var gain = new Pcm16AutomaticGainControl();

        gain.ProcessInPlace(audio);

        Assert.Equal(16d, gain.CurrentGain);
        Assert.Equal(16_000, BitConverter.ToInt16(audio, 0));
        Assert.Equal(-16_000, BitConverter.ToInt16(audio, 2));
    }

    [Fact]
    public void SilenceAndBottomNoise_AreNotAmplified()
    {
        var audio = Samples(20, -20, 0);
        var original = audio.ToArray();
        var gain = new Pcm16AutomaticGainControl();

        gain.ProcessInPlace(audio);

        Assert.Equal(1d, gain.CurrentGain);
        Assert.Equal(original, audio);
    }

    [Fact]
    public void SuddenLoudBlock_ImmediatelyDropsPreviousGainAndDoesNotClip()
    {
        var gain = new Pcm16AutomaticGainControl();
        var quiet = Samples(1_000);
        gain.ProcessInPlace(quiet);
        var loud = Samples(30_000, -30_000);

        gain.ProcessInPlace(loud);

        Assert.Equal(1d, gain.CurrentGain);
        Assert.Equal(30_000, BitConverter.ToInt16(loud, 0));
        Assert.Equal(-30_000, BitConverter.ToInt16(loud, 2));
    }

    [Fact]
    public void Reset_DropsRememberedGainBeforeResume()
    {
        var gain = new Pcm16AutomaticGainControl();
        var quiet = Samples(1_000);
        gain.ProcessInPlace(quiet);

        gain.Reset();

        Assert.Equal(1d, gain.CurrentGain);
    }

    private static byte[] Samples(params short[] values)
    {
        var bytes = new byte[values.Length * sizeof(short)];
        for (var index = 0; index < values.Length; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(short), sizeof(short)), values[index]);
        }
        return bytes;
    }
}
