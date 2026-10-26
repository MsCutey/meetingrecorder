import Foundation
import Testing
@testable import MeetingRecorder

@MainActor private final class StageManagerFixture {
    enum Failure: Error { case change, recovery, read }

    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    var recoveryURL: URL { directory.appendingPathComponent("stage-manager-recovery.json") }
    var enabled: Bool
    var writes: [Bool] = []
    var beforeWrite: ((Bool) throws -> Void)?
    var afterWrite: ((Bool) throws -> Void)?
    var ignoreWrites = false
    var readFails = false

    init(enabled: Bool = true) throws {
        self.enabled = enabled
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    func makeSession(recoveryURL: URL? = nil) -> StageManagerSession {
        StageManagerSession(readEnabled: { [unowned self] in
            if readFails { throw Failure.read }
            return enabled
        }, writeEnabled: { [unowned self] value in
            writes.append(value)
            try beforeWrite?(value)
            if !ignoreWrites { enabled = value }
            try afterWrite?(value)
        }, recoveryURL: recoveryURL ?? self.recoveryURL)
    }

    func cleanup() { try? FileManager.default.removeItem(at: directory) }
}

@Suite @MainActor struct StageManagerSessionTests {
    @Test func originallyDisabledIsNeverChanged() throws {
        let f = try StageManagerFixture(enabled: false); defer { f.cleanup() }
        let session = f.makeSession()
        #expect(try !session.begin())
        #expect(!session.hasPendingRecovery)
        #expect(try !session.restore())
        #expect(f.writes.isEmpty && !f.enabled)
        #expect(!FileManager.default.fileExists(atPath: f.recoveryURL.path))
    }

    @Test func enabledSettingIsRestoredAndRepeatedCallsAreIdempotent() throws {
        let f = try StageManagerFixture(); defer { f.cleanup() }
        let session = f.makeSession()
        #expect(try session.begin())
        #expect(!f.enabled && session.hasPendingRecovery)
        #expect(try !session.begin())
        #expect(try !session.recoverIfNeeded()) // This process still owns its active recording.
        #expect(f.writes == [false])
        #expect(try session.restore())
        #expect(f.enabled && !session.hasPendingRecovery)
        #expect(try !session.restore())
        #expect(f.writes == [false, true])
    }

    @Test func undoRecordExistsBeforeTheSystemSettingChanges() throws {
        let f = try StageManagerFixture(); defer { f.cleanup() }
        f.beforeWrite = { [unowned f] enabled in
            if !enabled {
                let data = try Data(contentsOf: f.recoveryURL)
                let record = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
                #expect(record["version"] as? Int == 1)
                #expect(record["originalEnabled"] as? Bool == true)
                #expect(record["temporaryEnabled"] as? Bool == false)
                #expect(f.enabled)
            }
        }
        let session = f.makeSession()
        #expect(try session.begin())
        #expect(try session.restore())
    }

    @Test func unavailableRecoveryStoragePreventsAnySettingChange() throws {
        let f = try StageManagerFixture(); defer { f.cleanup() }
        let nonDirectory = f.directory.appendingPathComponent("not-a-directory")
        try Data("file".utf8).write(to: nonDirectory)
        let session = f.makeSession(recoveryURL: nonDirectory.appendingPathComponent("recovery.json"))
        #expect(throws: (any Error).self) { try session.begin() }
        #expect(f.enabled && f.writes.isEmpty)
    }

    @Test func disableFailureBeforeMutationDoesNotLeaveAnUndoRecord() throws {
        let f = try StageManagerFixture(); defer { f.cleanup() }
        f.beforeWrite = { value in if !value { throw StageManagerFixture.Failure.change } }
        let session = f.makeSession()
        #expect(throws: StageManagerFixture.Failure.self) { try session.begin() }
        #expect(f.enabled && !session.hasPendingRecovery)
        #expect(f.writes == [false])
    }

    @Test func disableFailureAfterMutationRollsBack() throws {
        let f = try StageManagerFixture(); defer { f.cleanup() }
        f.afterWrite = { value in if !value { throw StageManagerFixture.Failure.change } }
        let session = f.makeSession()
        #expect(throws: StageManagerFixture.Failure.self) { try session.begin() }
        #expect(f.enabled && !session.hasPendingRecovery)
        #expect(f.writes == [false, true])
    }

    @Test func disableMustBeVerifiedRatherThanTrustingTheWrite() throws {
        let f = try StageManagerFixture(); defer { f.cleanup() }
        f.ignoreWrites = true
        let session = f.makeSession()
        #expect(throws: StageManagerSessionError.self) { try session.begin() }
        #expect(f.enabled && !session.hasPendingRecovery)
        #expect(f.writes == [false])
    }

    @Test func failedRollbackRetainsRecoveryAndCanBeRetried() throws {
        let f = try StageManagerFixture(); defer { f.cleanup() }
        f.afterWrite = { value in if !value { throw StageManagerFixture.Failure.change } }
        f.beforeWrite = { value in if value { throw StageManagerFixture.Failure.recovery } }
        let session = f.makeSession()
        #expect(throws: StageManagerSessionError.self) { try session.begin() }
        #expect(!f.enabled && session.hasPendingRecovery)
        f.beforeWrite = nil; f.afterWrite = nil
        #expect(try session.restore())
        #expect(f.enabled && !session.hasPendingRecovery)
    }

    @Test func restoreFailureKeepsRecordAndReleasesTheSessionLock() throws {
        let f = try StageManagerFixture(); defer { f.cleanup() }
        let session = f.makeSession()
        #expect(try session.begin())
        f.beforeWrite = { value in if value { throw StageManagerFixture.Failure.recovery } }
        #expect(throws: StageManagerFixture.Failure.self) { try session.restore() }
        #expect(!f.enabled && session.hasPendingRecovery)
        f.beforeWrite = nil
        let retry = f.makeSession()
        #expect(try retry.recoverIfNeeded())
        #expect(f.enabled && !retry.hasPendingRecovery)
    }

    @Test func restoreVerificationFailureRetainsRecord() throws {
        let f = try StageManagerFixture(); defer { f.cleanup() }
        let session = f.makeSession()
        #expect(try session.begin())
        f.ignoreWrites = true
        #expect(throws: StageManagerSessionError.self) { try session.restore() }
        #expect(!f.enabled && session.hasPendingRecovery)
        f.ignoreWrites = false
        #expect(try session.restore())
        #expect(f.enabled && !session.hasPendingRecovery)
    }

    @Test func nextInstanceRecoversAfterTheOwnerDisappears() throws {
        let f = try StageManagerFixture(); defer { f.cleanup() }
        var interrupted: StageManagerSession? = f.makeSession()
        #expect(try interrupted?.begin() == true)
        interrupted = nil // Simulate process loss: the lock closes, but the undo record remains.
        #expect(!f.enabled)
        let reopened = f.makeSession()
        #expect(try reopened.recoverIfNeeded())
        #expect(f.enabled && !reopened.hasPendingRecovery)
        #expect(f.writes == [false, true])
    }

    @Test func userAlreadyRestoredTheSettingIsNotOverwritten() throws {
        let f = try StageManagerFixture(); defer { f.cleanup() }
        let session = f.makeSession()
        #expect(try session.begin())
        f.enabled = true
        #expect(try !session.restore())
        #expect(!session.hasPendingRecovery)
        #expect(f.writes == [false])
    }

    @Test func liveRecordingCannotBeRecoveredByAnotherInstance() throws {
        let f = try StageManagerFixture(); defer { f.cleanup() }
        let first = f.makeSession(), second = f.makeSession()
        #expect(try first.begin())
        #expect(throws: StageManagerSessionError.self) { try second.recoverIfNeeded() }
        #expect(throws: StageManagerSessionError.self) { try second.begin() }
        #expect(!f.enabled && f.writes == [false])
        #expect(try first.restore())
        #expect(try second.begin())
        #expect(try second.restore())
        #expect(f.enabled)
    }

    @Test func corruptedRecoveryRecordFailsClosed() throws {
        let f = try StageManagerFixture(enabled: false); defer { f.cleanup() }
        let damaged = Data("not a recovery record".utf8)
        try damaged.write(to: f.recoveryURL)
        let session = f.makeSession()
        #expect(throws: StageManagerSessionError.self) { try session.recoverIfNeeded() }
        #expect(throws: StageManagerSessionError.self) { try session.begin() }
        #expect(try Data(contentsOf: f.recoveryURL) == damaged)
        #expect(session.hasPendingRecovery && f.writes.isEmpty && !f.enabled)
    }

    @Test func unexpectedRecoverySchemaCannotModifyTheSystem() throws {
        let f = try StageManagerFixture(enabled: false); defer { f.cleanup() }
        let damaged = Data(#"{"version":1,"originalEnabled":false,"temporaryEnabled":true}"#.utf8)
        try damaged.write(to: f.recoveryURL)
        let session = f.makeSession()
        #expect(throws: StageManagerSessionError.self) { try session.recoverIfNeeded() }
        #expect(try Data(contentsOf: f.recoveryURL) == damaged)
        #expect(f.writes.isEmpty && !f.enabled)
    }

    @Test func failedReadRetainsTheRecoveryRecordWithoutWriting() throws {
        let f = try StageManagerFixture(); defer { f.cleanup() }
        var interrupted: StageManagerSession? = f.makeSession()
        #expect(try interrupted?.begin() == true)
        interrupted = nil
        f.readFails = true
        let reopened = f.makeSession()
        #expect(throws: StageManagerFixture.Failure.self) { try reopened.recoverIfNeeded() }
        #expect(reopened.hasPendingRecovery && f.writes == [false])
        f.readFails = false
        #expect(try reopened.recoverIfNeeded())
    }
}
