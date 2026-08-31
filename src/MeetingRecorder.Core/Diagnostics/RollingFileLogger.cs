using System.Globalization;
using System.Text;

namespace MeetingRecorder.Core.Diagnostics;

public sealed class RollingFileLogger
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;

    public RollingFileLogger(string path, long maximumBytes = 5 * 1024 * 1024, int retainedFiles = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (retainedFiles < 1) throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        _path = System.IO.Path.GetFullPath(path);
        _maximumBytes = maximumBytes;
        _retainedFiles = retainedFiles;
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!); }
        catch (Exception error) when (IsStorageFailure(error)) { }
    }

    public string Path => _path;

    public void Info(string eventName, string message) => Write("INFO", eventName, message, null);
    public void Warning(string eventName, string message) => Write("WARN", eventName, message, null);
    public void Error(string eventName, string message, Exception? error = null) => Write("ERROR", eventName, message, error);

    private void Write(string level, string eventName, string message, Exception? error)
    {
        try
        {
            lock (_gate)
            {
                RollIfNeeded();
                var line = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(level).Append(' ')
                    .Append('[').Append(Sanitize(eventName)).Append("] ")
                    .Append(Sanitize(message));
                if (error is not null)
                {
                    var detail = error.ToString();
                    if (detail.Length > 16_384) detail = detail[..16_384] + "…";
                    line.Append(" | ").Append(Sanitize(detail));
                }
                line.AppendLine();
                File.AppendAllText(_path, line.ToString(), new UTF8Encoding(false));
            }
        }
        catch (Exception storageError) when (IsStorageFailure(storageError)) { }
    }

    private void RollIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < _maximumBytes) return;
        var oldest = $"{_path}.{_retainedFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = $"{_path}.{index}";
            if (File.Exists(source)) File.Move(source, $"{_path}.{index + 1}", true);
        }
        File.Move(_path, $"{_path}.1", true);
    }

    private static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    private static bool IsStorageFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
