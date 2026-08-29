using MeetingRecorder.Core.Audio;

namespace MeetingRecorder.Tests;

public sealed class BoundedPcmWaveProviderTests
{
    [Fact]
    public void BufferRejectsOverflowAndReleasesCapacityAsItIsRead()
    {
        using var provider = new BoundedPcmWaveProvider(capacity: TimeSpan.FromMilliseconds(1));
        var first = new byte[120];
        var second = new byte[100];
        Assert.True(provider.TryWrite(first));
        Assert.False(provider.TryWrite(second));

        var destination = new byte[64];
        Assert.Equal(64, provider.Read(destination));
        Assert.True(provider.TryWrite(second));
        Assert.InRange(provider.BufferedBytes, 1, provider.MaxBufferedBytes);
    }

    [Fact]
    public void CompletedAndDrainedProvider_ReturnsEndOfStream()
    {
        using var provider = new BoundedPcmWaveProvider(capacity: TimeSpan.FromSeconds(1));
        Assert.True(provider.TryWrite(new byte[32]));
        provider.Complete();
        Assert.Equal(32, provider.Read(new byte[64]));
        Assert.Equal(0, provider.Read(new byte[64]));
    }
}
