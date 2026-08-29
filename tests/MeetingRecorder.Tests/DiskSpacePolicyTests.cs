using MeetingRecorder.Core.Utilities;

namespace MeetingRecorder.Tests;

public sealed class DiskSpacePolicyTests
{
    [Theory]
    [InlineData(6L * 1024 * 1024 * 1024, DiskSpaceAction.Continue)]
    [InlineData(4L * 1024 * 1024 * 1024, DiskSpaceAction.Warn)]
    [InlineData(511L * 1024 * 1024, DiskSpaceAction.Stop)]
    public void ThresholdsChooseExpectedAction(long bytes, DiskSpaceAction expected) =>
        Assert.Equal(expected, DiskSpacePolicy.Evaluate(bytes));
}
