using MeetingRecorder.Core.Audio;

namespace MeetingRecorder.Tests;

public sealed class Pcm16StereoMixerTests
{
    [Fact]
    public void TwoSources_AreSummedWithoutUnconditionalSixDecibelLoss()
    {
        var system = Samples(20_000, -20_000);
        var microphone = Samples(10_000, 10_000);
        var destination = new byte[4];

        Pcm16StereoMixer.Mix(system, microphone, destination, true, true);

        Assert.Equal(30_000, BitConverter.ToInt16(destination, 0));
        Assert.Equal(-10_000, BitConverter.ToInt16(destination, 2));
    }

    [Fact]
    public void TwoSources_AreBlockLimitedWhenTheirSumWouldClip()
    {
        var system = Samples(30_000, -30_000);
        var microphone = Samples(30_000, -30_000);
        var destination = new byte[4];

        Pcm16StereoMixer.Mix(system, microphone, destination, true, true);

        Assert.Equal(short.MaxValue, BitConverter.ToInt16(destination, 0));
        Assert.Equal(-short.MaxValue, BitConverter.ToInt16(destination, 2));
    }

    [Fact]
    public void MissingSource_IsReplacedBySilenceWithoutReducingPresentSource()
    {
        var microphone = Samples(12_345, -12_345);
        var destination = new byte[4];

        Pcm16StereoMixer.Mix(new byte[4], microphone, destination, false, true);

        Assert.Equal(microphone, destination);
    }

    private static byte[] Samples(params short[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++) BitConverter.TryWriteBytes(bytes.AsSpan(index * 2, 2), values[index]);
        return bytes;
    }
}
