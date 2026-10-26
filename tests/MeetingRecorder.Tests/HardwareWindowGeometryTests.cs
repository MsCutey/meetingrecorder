using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Recording;
using Xunit.Abstractions;

namespace MeetingRecorder.Tests;

[Collection("Hardware")]
public sealed class HardwareWindowGeometryTests(ITestOutputHelper output)
{
    [HardwareFact]
    public async Task WindowPreviewAndSourceVideo_PreserveSquareAndNativeDimensions()
    {
        using var window = await GeometryWindow.OpenAsync();
        var root = Path.Combine(Path.GetTempPath(), "MeetingRecorderGeometry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var (portrait, quality, previewEnabled) in new[]
            {
                (false, VideoQualityPreset.Source, true),
                (true, VideoQualityPreset.Source, true),
                (true, VideoQualityPreset.Hd, true),
                (true, VideoQualityPreset.FullHd, true),
                (false, VideoQualityPreset.Source, false)
            })
            {
                // Simulate a source selected before the user resized its window.
                var selectedTarget = window.Target;
                await window.SetSizeAsync(portrait ? 420 : 600, portrait ? 600 : 420);
                var target = window.Target;
                await using var service = CreateService(root);
                string? warning = null;
                service.ProgressChanged += (_, args) => warning = args.Progress.Warning;
                PreviewFrame? preview = null;
                service.PreviewFrameAvailable += (_, args) => preview = args.Frame;
                await service.StartAsync(new RecordingRequest
                {
                    Mode = RecordingMode.VideoAndAudio, CaptureTarget = selectedTarget,
                    CaptureSystemAudio = false, CaptureMicrophone = false,
                    Quality = quality, CountdownSeconds = 0,
                    PreviewEnabled = previewEnabled, OutputDirectory = root
                });
                await Task.Delay(500);
                await service.PauseAsync();
                await Task.Delay(150);
                await service.ResumeAsync();
                await Task.Delay(1800);
                await service.StopAsync();
                output.WriteLine($"Case {portrait}/{quality}/preview={previewEnabled}; capture warning: {warning}");
                if (previewEnabled)
                {
                    Assert.NotNull(preview);
                    Assert.InRange(MeasureSquare(preview.Bgra32, preview.Width, preview.Height, preview.Stride, "preview"), 0.95, 1.05);
                    SaveDiagnostic(preview.Bgra32, preview.Width, preview.Height, preview.Stride, $"{portrait}-{quality}-preview.png");
                }
                else Assert.Null(preview);
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(service.CurrentOutputPath!);
                var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
                var properties = clip.GetVideoEncodingProperties();
                var width = (int)properties.Width;
                var height = (int)properties.Height;
                output.WriteLine($"Source {target.Width}x{target.Height}, video {width}x{height}");
                var pixels = await DecodeFrameAsync(service.CurrentOutputPath!);
                Assert.Equal(width * height * 4, pixels.Length);
                var maxHeight = quality == VideoQualityPreset.Source ? target.Height : quality == VideoQualityPreset.Hd ? 720 : 1080;
                var scale = Math.Min(1, (double)maxHeight / (target.Height & ~1));
                var videoRatio = MeasureSquare(pixels, width, height, width * 4, "video", window.SquareSize * scale);
                SaveDiagnostic(pixels, width, height, width * 4, $"{portrait}-{quality}-{previewEnabled}-video.png");
                Assert.InRange(videoRatio, 0.95, 1.05);
                Assert.Equal((int)((target.Width & ~1) * scale) & ~1, width);
                Assert.Equal((int)((target.Height & ~1) * scale) & ~1, height);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [HardwareFact]
    public async Task MinimizedSelectedWindow_StartsByRestoringItWithoutActivation()
    {
        using var window = await GeometryWindow.OpenAsync();
        var target = window.Target;
        await window.MinimizeAsync();
        var foreground = GetForegroundWindow();
        var root = Path.Combine(Path.GetTempPath(), "MeetingRecorderRestore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var service = CreateService(root);
            PreviewFrame? preview = null;
            service.PreviewFrameAvailable += (_, args) => preview = args.Frame;
            await service.StartAsync(new RecordingRequest
            {
                Mode = RecordingMode.VideoAndAudio, CaptureTarget = target,
                CaptureSystemAudio = false, CaptureMicrophone = false,
                Quality = VideoQualityPreset.Source, CountdownSeconds = 0,
                PreviewEnabled = true, OutputDirectory = root
            });
            Assert.False(IsIconic(new nint(target.NativeHandle)));
            Assert.Equal(foreground, GetForegroundWindow());
            await Task.Delay(1000);
            await service.StopAsync();
            Assert.NotNull(preview);
            Assert.InRange(MeasureSquare(preview.Bgra32, preview.Width, preview.Height, preview.Stride, "restored preview"), 0.95, 1.05);
        }
        finally { Directory.Delete(root, true); }
    }

    private double MeasureSquare(byte[] pixels, int width, int height, int stride, string label, double? expectedEdge = null)
    {
        var visited = new bool[width * height];
        var largest = 0;
        var bounds = (Left: 0, Right: 0, Top: 0, Bottom: 0);
        bool IsRed(int x, int y)
        {
            var p = y * stride + x * 4;
            return pixels[p + 2] >= 180 && pixels[p + 1] <= 80 && pixels[p] <= 80;
        }
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (visited[y * width + x] || !IsRed(x, y)) continue;
                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue((x,y)); visited[y * width + x] = true;
                var left = x; var right = x; var top = y; var bottom = y; var count = 0;
                while (queue.TryDequeue(out var point))
                {
                    count++;
                    left = Math.Min(left, point.X); right = Math.Max(right, point.X);
                    top = Math.Min(top, point.Y); bottom = Math.Max(bottom, point.Y);
                    foreach (var (dx,dy) in new[] {(-1,0),(1,0),(0,-1),(0,1)})
                    {
                        var xx = point.X + dx; var yy = point.Y + dy;
                        if (xx < 0 || yy < 0 || xx >= width || yy >= height || visited[yy * width + xx] || !IsRed(xx,yy)) continue;
                        visited[yy * width + xx] = true; queue.Enqueue((xx,yy));
                    }
                }
                if (count > largest) { largest = count; bounds = (left,right,top,bottom); }
            }
        Assert.True(largest > 20, $"{label}: red calibration square missing");
        var ratio = (double)(bounds.Right - bounds.Left + 1) / (bounds.Bottom - bounds.Top + 1);
        output.WriteLine($"{label}: square {bounds.Right-bounds.Left+1}x{bounds.Bottom-bounds.Top+1}, ratio={ratio:F4}");
        if (expectedEdge is { } edge)
        {
            Assert.InRange((double)(bounds.Right - bounds.Left + 1), edge - 3, edge + 3);
            Assert.InRange((double)(bounds.Bottom - bounds.Top + 1), edge - 3, edge + 3);
        }
        return ratio;
    }

    private static void SaveDiagnostic(byte[] pixels, int width, int height, int stride, string name)
    {
        var destination = Environment.GetEnvironmentVariable("MEETINGRECORDER_GEOMETRY_OUTPUT");
        if (string.IsNullOrWhiteSpace(destination)) return;
        Directory.CreateDirectory(destination);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride)));
        using var file = File.Create(Path.Combine(destination, name));
        encoder.Save(file);
    }

    private static async Task<byte[]> DecodeFrameAsync(string path)
    {
        var decoder = Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_FFMPEG");
        Assert.False(string.IsNullOrWhiteSpace(decoder), "Set MEETINGRECORDER_TEST_FFMPEG for independent geometry validation.");
        var start = new ProcessStartInfo(decoder!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-ss", "1", "-i", path, "-frames:v", "1", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        using var result = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(result);
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return result.ToArray();
    }

    private static WorkerRecordingService CreateService(string root)
    {
        var data = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var repository = new AppRepository(new AppDataPaths(data, Path.Combine(data,"settings.json"),
            Path.Combine(data,"history.json"), Path.Combine(data,"active-session.json"), Path.Combine(data,"assets"), true));
        return new WorkerRecordingService(repository, launcher: new WorkerProcessLauncher(Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_WORKER")));
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint handle);

    internal sealed class GeometryWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Window _window = null!;
        private nint _handle;
        private GeometryWindow()
        {
            _thread = new Thread(() =>
            {
                try
                {
                    _window = new Window { Title = "MeetingRecorder geometry calibration", Width = 600, Height = 420,
                        Left = 20, Top = 20, Content = new Calibration(), Topmost = true };
                    _window.Show();
                    _handle = new WindowInteropHelper(_window).Handle;
                    _ready.SetResult();
                    Dispatcher.Run();
                }
                catch (Exception ex) { _ready.TrySetException(ex); }
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
        public static async Task<GeometryWindow> OpenAsync()
        {
            var result = new GeometryWindow();
            await result._ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(300);
            return result;
        }
        public CaptureTargetInfo Target
        {
            get
            {
                var hr = DwmGetWindowAttribute(_handle, 9, out var rect, Marshal.SizeOf<NativeRect>());
                Marshal.ThrowExceptionForHR(hr);
                return new CaptureTargetInfo("calibration", "calibration", CaptureTargetKind.Window, null,
                    _handle.ToInt64(), rect.Right - rect.Left, rect.Bottom - rect.Top);
            }
        }
        public double SquareSize => _window.Dispatcher.Invoke(() => 100 * VisualTreeHelper.GetDpi(_window).DpiScaleX);
        public async Task SetSizeAsync(double width, double height)
        {
            await _window.Dispatcher.InvokeAsync(() => { _window.Width = width; _window.Height = height; });
            await Task.Delay(300);
        }
        public async Task MinimizeAsync()
        {
            await _window.Dispatcher.InvokeAsync(() => _window.WindowState = WindowState.Minimized);
            await Task.Delay(300);
        }
        public void Dispose()
        {
            _window.Dispatcher.Invoke(() => { _window.Close(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); });
            _thread.Join(5000);
        }
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out NativeRect rect, int size);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    }

    private sealed class Calibration : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawing)
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
            drawing.DrawRectangle(Brushes.Red, null, new Rect(60, 60, 100, 100));
            drawing.DrawEllipse(Brushes.Blue, null, new Point(230, 110), 50, 50);
        }
    }
}
