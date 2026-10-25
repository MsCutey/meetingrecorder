import Darwin
import Foundation

enum StageManagerSessionError: LocalizedError {
    case busy
    case invalidRecoveryRecord
    case invalidStorageLocation
    case lockUnavailable(Int32)
    case verificationFailed(expected: Bool)
    case rollbackFailed(change: String, recovery: String)

    var errorDescription: String? {
        switch self {
        case .busy:
            return "另一个 MeetingRecorder 实例正在管理台前调度，请先结束那次录制。"
        case .invalidRecoveryRecord:
            return "台前调度恢复记录无法识别，未修改系统设置。请检查恢复记录后重试。"
        case .invalidStorageLocation:
            return "台前调度恢复记录必须保存在本机文件中。"
        case .lockUnavailable(let code):
            return "无法锁定台前调度恢复记录（\(code)），未修改系统设置。"
        case .verificationFailed(let expected):
            return expected ? "未能确认台前调度已恢复开启。" : "未能确认台前调度已关闭。"
        case .rollbackFailed(let change, let recovery):
            return "切换台前调度失败：\(change) 恢复也未完成：\(recovery) 已保留恢复记录，可重试。"
        }
    }
}

/// Temporarily changes only an enabled Stage Manager setting, with a durable undo record.
/// The system-specific preferences backend is injected; tests never touch macOS settings.
@MainActor final class StageManagerSession {
    private struct RecoveryRecord: Codable {
        let version: Int
        let originalEnabled: Bool
        let temporaryEnabled: Bool

        static let disabling = RecoveryRecord(version: 1, originalEnabled: true, temporaryEnabled: false)

        var isSupported: Bool { version == 1 && originalEnabled && !temporaryEnabled }
    }

    private let readEnabled: () throws -> Bool
    private let writeEnabled: (Bool) throws -> Void
    private let recoveryURL: URL
    private let files = FileManager.default
    private var lockDescriptor: Int32?
    private var isManaging = false

    var hasPendingRecovery: Bool { isManaging || files.fileExists(atPath: recoveryURL.path) }

    init(readEnabled: @escaping () throws -> Bool,
         writeEnabled: @escaping (Bool) throws -> Void,
         recoveryURL: URL) {
        self.readEnabled = readEnabled
        self.writeEnabled = writeEnabled
        self.recoveryURL = recoveryURL
    }

    deinit {
        // A crash or forced exit cannot run asynchronous restoration. Releasing this
        // lock lets the next process recover from the record, without treating a
        // different live recording as an abandoned session.
        if let descriptor = lockDescriptor {
            _ = flock(descriptor, LOCK_UN)
            _ = close(descriptor)
        }
    }

    /// Returns true only when this call changes Stage Manager from on to off.
    @discardableResult func begin() throws -> Bool {
        guard !isManaging else { return false }
        try acquireLock()
        defer { if !isManaging { releaseLock() } }

        // Resolve a previous interrupted session before taking a new snapshot.
        _ = try restoreUnderLock()
        guard try readEnabled() else { return false }

        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        try encoder.encode(RecoveryRecord.disabling).write(to: recoveryURL, options: .atomic)
        do {
            try writeEnabled(false)
            guard try !readEnabled() else { throw StageManagerSessionError.verificationFailed(expected: false) }
            isManaging = true
            return true
        } catch {
            let changeError = error
            // A backend can change the setting and then fail. Always inspect and
            // restore, even when the attempted write itself threw an error.
            do { _ = try restoreUnderLock() }
            catch {
                throw StageManagerSessionError.rollbackFailed(change: changeError.localizedDescription,
                                                               recovery: error.localizedDescription)
            }
            throw changeError
        }
    }

    /// Releases the current session, or retries a retained recovery record.
    /// A setting already restored by the user is not written again.
    @discardableResult func restore() throws -> Bool {
        guard hasPendingRecovery else { return false }
        try acquireLock()
        defer { isManaging = false; releaseLock() }
        return try restoreUnderLock()
    }

    /// Called on launch. A live recording in another process keeps its lock, so its
    /// temporary setting must never be mistaken for an interrupted recording.
    @discardableResult func recoverIfNeeded() throws -> Bool {
        guard !isManaging else { return false }
        return try restore()
    }

    private func restoreUnderLock() throws -> Bool {
        precondition(lockDescriptor != nil)
        guard files.fileExists(atPath: recoveryURL.path) else { return false }
        let record: RecoveryRecord
        do {
            record = try JSONDecoder().decode(RecoveryRecord.self, from: Data(contentsOf: recoveryURL))
            guard record.isSupported else { throw StageManagerSessionError.invalidRecoveryRecord }
        } catch { throw StageManagerSessionError.invalidRecoveryRecord }

        var changed = false
        if try readEnabled() != record.originalEnabled {
            try writeEnabled(record.originalEnabled)
            guard try readEnabled() == record.originalEnabled else {
                throw StageManagerSessionError.verificationFailed(expected: record.originalEnabled)
            }
            changed = true
        }
        // Keep the record on any read, write, verification or cleanup failure.
        // If deletion fails after restoration, a retry sees the original value
        // and only retries deletion, without changing the user's setting again.
        try files.removeItem(at: recoveryURL)
        return changed
    }

    private func acquireLock() throws {
        guard lockDescriptor == nil else { return }
        guard recoveryURL.isFileURL else { throw StageManagerSessionError.invalidStorageLocation }
        try files.createDirectory(at: recoveryURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let lockURL = recoveryURL.appendingPathExtension("lock")
        let descriptor = open(lockURL.path, O_CREAT | O_RDWR | O_CLOEXEC, S_IRUSR | S_IWUSR)
        guard descriptor >= 0 else { throw StageManagerSessionError.lockUnavailable(errno) }
        guard flock(descriptor, LOCK_EX | LOCK_NB) == 0 else {
            let code = errno
            _ = close(descriptor)
            if code == EWOULDBLOCK || code == EAGAIN { throw StageManagerSessionError.busy }
            throw StageManagerSessionError.lockUnavailable(code)
        }
        lockDescriptor = descriptor
        // Keep the lock file in place after releasing it. Unlinking it could let
        // another process lock a different inode while this session still owns it.
    }

    private func releaseLock() {
        guard let descriptor = lockDescriptor else { return }
        lockDescriptor = nil
        _ = flock(descriptor, LOCK_UN)
        _ = close(descriptor)
    }
}
