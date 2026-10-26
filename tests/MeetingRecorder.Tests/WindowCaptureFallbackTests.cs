using MeetingRecorder.Engine.Recording;

namespace MeetingRecorder.Tests;

public sealed class WindowCaptureFallbackTests
{
    [Fact]
    public void WindowInsideMonitor_ProducesRelativeEvenCrop()
    {
        var crop = WindowCaptureFallback.ComputeDisplayCrop(
            windowLeft: 101, windowTop: 202, windowRight: 1481, windowBottom: 1003,
            monitorLeft: 0, monitorTop: 0, monitorRight: 1920, monitorBottom: 1080);

        Assert.NotNull(crop);
        Assert.Equal(100, crop!.Left);
        Assert.Equal(202, crop.Top);
        Assert.Equal(1380, crop.Width);
        Assert.Equal(800, crop.Height);
    }

    [Fact]
    public void WindowSpillingOutOfMonitor_IsClampedToMonitorBounds()
    {
        var crop = WindowCaptureFallback.ComputeDisplayCrop(
            windowLeft: -100, windowTop: -50, windowRight: 900, windowBottom: 700,
            monitorLeft: 0, monitorTop: 0, monitorRight: 1920, monitorBottom: 1080);

        Assert.NotNull(crop);
        Assert.Equal(0, crop!.Left);
        Assert.Equal(0, crop.Top);
        Assert.Equal(900, crop.Width);
        Assert.Equal(700, crop.Height);
    }

    [Fact]
    public void WindowOnSecondaryMonitor_IsRelativeToThatMonitorOrigin()
    {
        var crop = WindowCaptureFallback.ComputeDisplayCrop(
            windowLeft: 1931, windowTop: 11, windowRight: 2549, windowBottom: 411,
            monitorLeft: 1920, monitorTop: 0, monitorRight: 3840, monitorBottom: 1080);

        Assert.NotNull(crop);
        Assert.Equal(10, crop!.Left);
        Assert.Equal(10, crop.Top);
        Assert.Equal(618, crop.Width);
        Assert.Equal(400, crop.Height);
    }

    [Fact]
    public void WindowCoveringMostOfMonitor_StillExcludesTaskbar()
    {
        var crop = WindowCaptureFallback.ComputeDisplayCrop(
            windowLeft: 0, windowTop: 0, windowRight: 1920, windowBottom: 1040,
            monitorLeft: 0, monitorTop: 0, monitorRight: 1920, monitorBottom: 1080);

        Assert.NotNull(crop);
        Assert.Equal(1920, crop.Width);
        Assert.Equal(1040, crop.Height);
    }

    [Fact]
    public void TinyIntersection_FallsBackToFullDisplay()
    {
        var crop = WindowCaptureFallback.ComputeDisplayCrop(
            windowLeft: -500, windowTop: -500, windowRight: 30, windowBottom: 30,
            monitorLeft: 0, monitorTop: 0, monitorRight: 1920, monitorBottom: 1080);

        Assert.Null(crop);
    }

    [Fact]
    public void WindowFullyOffScreen_FallsBackToFullDisplay()
    {
        var crop = WindowCaptureFallback.ComputeDisplayCrop(
            windowLeft: -800, windowTop: -600, windowRight: -100, windowBottom: -100,
            monitorLeft: 0, monitorTop: 0, monitorRight: 1920, monitorBottom: 1080);

        Assert.Null(crop);
    }

    [Fact]
    public void DegenerateMonitor_FallsBackToFullDisplay()
    {
        var crop = WindowCaptureFallback.ComputeDisplayCrop(
            windowLeft: 10, windowTop: 10, windowRight: 900, windowBottom: 700,
            monitorLeft: 100, monitorTop: 100, monitorRight: 100, monitorBottom: 100);

        Assert.Null(crop);
    }
}
