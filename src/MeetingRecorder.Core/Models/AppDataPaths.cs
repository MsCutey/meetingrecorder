namespace MeetingRecorder.Core.Models;

public sealed record AppDataPaths(
    string RootDirectory,
    string SettingsPath,
    string HistoryPath,
    string ActiveSessionPath,
    string AssetsDirectory,
    bool IsPortable)
{
    public string LogsDirectory => Path.Combine(RootDirectory, "Logs");

    public static AppDataPaths Resolve(string? executableDirectory = null)
    {
        executableDirectory ??= AppContext.BaseDirectory;
        var portable = File.Exists(Path.Combine(executableDirectory, "portable.marker"));
        var root = portable
            ? Path.Combine(executableDirectory, "UserData")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingRecorder");

        return new AppDataPaths(
            root,
            Path.Combine(root, "settings.json"),
            Path.Combine(root, "history.json"),
            Path.Combine(root, "active-session.json"),
            Path.Combine(root, "Assets"),
            portable);
    }
}
