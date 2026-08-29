using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Abstractions;

public interface IAppRepository
{
    AppDataPaths Paths { get; }
    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecordingHistoryItem>> LoadHistoryAsync(CancellationToken cancellationToken = default);
    Task AddHistoryAsync(RecordingHistoryItem item, CancellationToken cancellationToken = default);
    Task SaveActiveSessionAsync(ActiveSession session, CancellationToken cancellationToken = default);
    Task ClearActiveSessionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecoveryCandidate>> ScanRecoveryAsync(CancellationToken cancellationToken = default);
    Task<string> RecoverAsync(RecoveryCandidate candidate, CancellationToken cancellationToken = default);
}
