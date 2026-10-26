using MeetingRecorder.Engine.Recording;

namespace MeetingRecorder.Tests;

public sealed class RecordingPowerRequestTests
{
    [Fact]
    public async Task NativeRequest_CanBeReleasedOnAnotherThreadAndDisposedTwice()
    {
        using var request = new RecordingPowerRequest();
        await Task.Run(request.Dispose);
        request.Dispose();
    }
}
