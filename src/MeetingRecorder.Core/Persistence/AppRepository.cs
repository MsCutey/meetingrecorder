using MeetingRecorder.Core.Abstractions;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Utilities;
using System.Text.Json;

namespace MeetingRecorder.Core.Persistence;

public sealed class AppRepository : IAppRepository
{
    private readonly AtomicJsonFile _json = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public AppRepository(AppDataPaths? paths = null)
    {
        Paths = paths ?? AppDataPaths.Resolve();
        Directory.CreateDirectory(Paths.RootDirectory);
        Directory.CreateDirectory(Paths.AssetsDirectory);
    }

    public AppDataPaths Paths { get; }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = AppSettingsMigrator.Migrate(
                await _json.ReadAsync<AppSettings>(Paths.SettingsPath, cancellationToken).ConfigureAwait(false) ?? new AppSettings());
            settings.Validate();
            return settings;
        }
        catch (JsonException)
        {
            PreserveCorruptFile(Paths.SettingsPath);
            return new AppSettings();
        }
        catch (InvalidDataException)
        {
            PreserveCorruptFile(Paths.SettingsPath);
            return new AppSettings();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _json.WriteAsync(Paths.SettingsPath, settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<RecordingHistoryItem>> LoadHistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _json.ReadAsync<List<RecordingHistoryItem>>(Paths.HistoryPath, cancellationToken).ConfigureAwait(false)
                   ?? [];
        }
        catch (JsonException)
        {
            PreserveCorruptFile(Paths.HistoryPath);
            return [];
        }
    }

    public async Task AddHistoryAsync(RecordingHistoryItem item, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<RecordingHistoryItem> history;
            try
            {
                history = await _json.ReadAsync<List<RecordingHistoryItem>>(Paths.HistoryPath, cancellationToken).ConfigureAwait(false) ?? [];
            }
            catch (JsonException)
            {
                PreserveCorruptFile(Paths.HistoryPath);
                history = [];
            }

            history.Insert(0, item);
            if (history.Count > 200) history.RemoveRange(200, history.Count - 200);
            await _json.WriteAsync(Paths.HistoryPath, history, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RemoveHistoryAsync(string historyId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyId);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<RecordingHistoryItem> history;
            try
            {
                history = await _json.ReadAsync<List<RecordingHistoryItem>>(Paths.HistoryPath, cancellationToken).ConfigureAwait(false) ?? [];
            }
            catch (JsonException)
            {
                PreserveCorruptFile(Paths.HistoryPath);
                history = [];
            }

            if (history.RemoveAll(item => item.Id == historyId) > 0)
            {
                await _json.WriteAsync(Paths.HistoryPath, history, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SaveActiveSessionAsync(ActiveSession session, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _json.WriteAsync(Paths.ActiveSessionPath, session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task ClearActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(Paths.ActiveSessionPath)) File.Delete(Paths.ActiveSessionPath);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RecoveryCandidate>> ScanRecoveryAsync(CancellationToken cancellationToken = default)
    {
        var candidates = new Dictionary<string, RecoveryCandidate>(StringComparer.OrdinalIgnoreCase);
        var registeredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ActiveSession? active = null;
        try
        {
            active = await _json.ReadAsync<ActiveSession>(Paths.ActiveSessionPath, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            PreserveCorruptFile(Paths.ActiveSessionPath);
        }

        var activeRecoveryPath = active?.EffectiveRecoveryMediaPath;
        if (active is not null &&
            (File.Exists(active.PartialPath) || (activeRecoveryPath is not null && File.Exists(activeRecoveryPath))))
        {
            candidates[active.Id] = new RecoveryCandidate(
                active.Id,
                active.Mode,
                active.FinalPath,
                File.Exists(active.PartialPath) ? active.PartialPath : null,
                activeRecoveryPath is not null && File.Exists(activeRecoveryPath) ? activeRecoveryPath : null,
                active.StartedAt);
            registeredPaths.Add(Path.GetFullPath(active.PartialPath));
            if (activeRecoveryPath is not null) registeredPaths.Add(Path.GetFullPath(activeRecoveryPath));
        }

        var settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(settings.OutputDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(settings.OutputDirectory, "会议录制_*.partial.*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (registeredPaths.Contains(Path.GetFullPath(path))) continue;
                if (path.Contains(".recovery.partial.", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(path);
                if (info.Length == 0) continue;
                var mode = path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ? RecordingMode.AudioOnly : RecordingMode.VideoAndAudio;
                var id = Path.GetFileNameWithoutExtension(path).Replace(".partial", string.Empty, StringComparison.OrdinalIgnoreCase);
                if (candidates.ContainsKey(id)) continue;
                var extension = mode == RecordingMode.AudioOnly ? ".m4a" : ".mp4";
                var suggested = Path.Combine(info.DirectoryName!, id + extension);
                string? recovery = null;
                if (mode == RecordingMode.AudioOnly)
                {
                    var recoveryMp3 = Path.Combine(info.DirectoryName!, id + ".recovery.partial.mp3");
                    var legacyRecoveryMp4 = Path.Combine(info.DirectoryName!, id + ".recovery.partial.mp4");
                    recovery = File.Exists(recoveryMp3) ? recoveryMp3 : File.Exists(legacyRecoveryMp4) ? legacyRecoveryMp4 : null;
                }
                candidates[id] = new RecoveryCandidate(id, mode, suggested, path, File.Exists(recovery) ? recovery : null, info.CreationTimeUtc);
            }

            var recoverySidecars = Directory.EnumerateFiles(settings.OutputDirectory, "会议录制_*.recovery.partial.mp3", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(settings.OutputDirectory, "会议录制_*.recovery.partial.mp4", SearchOption.TopDirectoryOnly));
            foreach (var path in recoverySidecars)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (registeredPaths.Contains(Path.GetFullPath(path))) continue;
                var info = new FileInfo(path);
                if (info.Length == 0) continue;
                var fileName = Path.GetFileName(path);
                var suffix = path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                    ? ".recovery.partial.mp3"
                    : ".recovery.partial.mp4";
                var id = fileName[..^suffix.Length];
                if (candidates.ContainsKey(id)) continue;
                var partial = Path.Combine(info.DirectoryName!, id + ".partial.m4a");
                var suggested = Path.Combine(info.DirectoryName!, id + ".m4a");
                candidates[id] = new RecoveryCandidate(
                    id,
                    RecordingMode.AudioOnly,
                    suggested,
                    File.Exists(partial) ? partial : null,
                    path,
                    info.CreationTimeUtc);
            }
        }

        return candidates.Values.OrderByDescending(candidate => candidate.StartedAt).ToArray();
    }

    public async Task<string> RecoverAsync(RecoveryCandidate candidate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = SelectRecoverySource(candidate);
        if (source is null)
        {
            throw new FileNotFoundException("没有可恢复的媒体文件。");
        }

        var desired = candidate.SuggestedPath;
        if (source.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) && !desired.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            desired = Path.ChangeExtension(desired, ".mp3");
        }
        else if (source.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && !desired.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            // 兼容 V1.0.0 的音频恢复视频。
            desired = Path.ChangeExtension(desired, ".mp4");
        }
        var recovered = RecordingFileNameFactory.MakeRecoveredPath(desired);
        File.Move(source, recovered);
        if (candidate.Mode == RecordingMode.AudioOnly &&
            candidate.PartialMediaPath is { } unfinalized &&
            !string.Equals(unfinalized, source, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(unfinalized))
        {
            var preserved = unfinalized.Replace(".partial.m4a", ".unfinalized.m4a", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(preserved, unfinalized, StringComparison.OrdinalIgnoreCase)) preserved += ".unfinalized";
            if (File.Exists(preserved)) preserved = RecordingFileNameFactory.MakeRecoveredPath(preserved);
            File.Move(unfinalized, preserved);
        }
        try
        {
            var active = await _json.ReadAsync<ActiveSession>(Paths.ActiveSessionPath, cancellationToken).ConfigureAwait(false);
            if (active?.Id == candidate.Id && File.Exists(Paths.ActiveSessionPath)) File.Delete(Paths.ActiveSessionPath);
        }
        catch (JsonException)
        {
            PreserveCorruptFile(Paths.ActiveSessionPath);
        }
        return recovered;
    }

    private static string? SelectRecoverySource(RecoveryCandidate candidate)
    {
        if (candidate.Mode == RecordingMode.AudioOnly &&
            candidate.RecoveryMediaPath is { } audioRecovery &&
            File.Exists(audioRecovery) &&
            new FileInfo(audioRecovery).Length > 0)
        {
            return audioRecovery;
        }
        if (candidate.PartialMediaPath is { } partial && File.Exists(partial) && new FileInfo(partial).Length > 0)
        {
            return partial;
        }
        if (candidate.RecoveryMediaPath is { } recovery && File.Exists(recovery) && new FileInfo(recovery).Length > 0)
        {
            return recovery;
        }
        return null;
    }

    private static void PreserveCorruptFile(string path)
    {
        if (!File.Exists(path)) return;
        var preserved = path + $".corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}";
        File.Move(path, preserved, false);
    }
}
