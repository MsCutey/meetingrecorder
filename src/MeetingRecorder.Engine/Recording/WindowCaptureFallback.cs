namespace MeetingRecorder.Engine.Recording;

/// <summary>
/// 窗口捕获失败时的降级几何计算。纯函数，无 P/Invoke，便于单元测试。
/// </summary>
internal static class WindowCaptureFallback
{
    private const double FullDisplayCoverageThreshold = 0.85;
    private const int MinimumCroppedSize = 64;

    /// <summary>
    /// 计算窗口矩形与显示器矩形的交集，并换算为显示器左上角相对的裁剪区域（偶数化）。
    /// 交集过小或已覆盖显示器大部分面积时返回 null，表示应直接录制整个显示器。
    /// </summary>
    public static ScreenCrop? ComputeDisplayCrop(
        int windowLeft, int windowTop, int windowRight, int windowBottom,
        int monitorLeft, int monitorTop, int monitorRight, int monitorBottom)
    {
        var left = Math.Max(windowLeft, monitorLeft);
        var top = Math.Max(windowTop, monitorTop);
        var right = Math.Min(windowRight, monitorRight);
        var bottom = Math.Min(windowBottom, monitorBottom);
        var width = right - left;
        var height = bottom - top;
        var monitorWidth = monitorRight - monitorLeft;
        var monitorHeight = monitorBottom - monitorTop;
        if (monitorWidth <= 0 || monitorHeight <= 0) return null;
        if (width < MinimumCroppedSize || height < MinimumCroppedSize) return null;
        if ((double)width * height >= (double)monitorWidth * monitorHeight * FullDisplayCoverageThreshold) return null;

        var cropLeft = FloorToEven(left - monitorLeft);
        var cropTop = FloorToEven(top - monitorTop);
        var cropWidth = FloorToEven(width);
        var cropHeight = FloorToEven(height);
        if (cropWidth < 2 || cropHeight < 2) return null;
        return new ScreenCrop(cropLeft, cropTop, cropWidth, cropHeight);
    }

    private static int FloorToEven(int value) => value & ~1;

    internal sealed record ScreenCrop(int Left, int Top, int Width, int Height);
}
