using System.Runtime.InteropServices;
using MeetingRecorder.Core.Protocol;

namespace MeetingRecorder.Engine.Diagnostics;

public static class WorkerPreflight
{
    public static PreflightResult Check(string outputPath)
    {
        var issues = CheckEnvironment().Issues.ToList();
        CheckOutputDirectory(outputPath, issues);
        return new PreflightResult(issues);
    }

    public static PreflightResult CheckEnvironment()
    {
        var issues = new List<PreflightIssue>();
        if (!Environment.Is64BitProcess)
        {
            issues.Add(new PreflightIssue("process-x64", "录制 Worker 必须以 x64 进程运行。", true));
        }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            issues.Add(new PreflightIssue("windows-version", "需要 Windows 10 2004（19041）或更高版本。", true));
        }

        CheckNativeLibrary("vcruntime140.dll", "vc-runtime", "缺少 Microsoft Visual C++ x64 运行库。请修复或重新运行安装程序。", issues);
        CheckNativeLibrary("mfplat.dll", "media-foundation", "缺少 Windows Media Foundation。Windows N/KN 版本请安装媒体功能包。", issues);
        CheckNativeLibrary("mfreadwrite.dll", "media-foundation-readwrite", "Media Foundation 编码组件不可用。", issues);
        return new PreflightResult(issues);
    }

    private static void CheckOutputDirectory(string outputPath, ICollection<PreflightIssue> issues)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("输出目录无效。");
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".meetingrecorder-write-test-{Guid.NewGuid():N}.tmp");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            issues.Add(new PreflightIssue("output-writable", $"无法写入录制目录：{error.Message}", true));
        }
    }

    private static void CheckNativeLibrary(string name, string code, string message, ICollection<PreflightIssue> issues)
    {
        if (!NativeLibrary.TryLoad(name, out var handle))
        {
            issues.Add(new PreflightIssue(code, message, true));
            return;
        }
        NativeLibrary.Free(handle);
    }
}
