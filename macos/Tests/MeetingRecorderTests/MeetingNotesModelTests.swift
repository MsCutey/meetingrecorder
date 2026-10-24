import Foundation
import Testing
import RecorderSupport
@testable import MeetingRecorder

@MainActor private final class ModelFixture {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    let suite = "MeetingRecorderTests." + UUID().uuidString
    let defaults: UserDefaults
    var reads: [String] = []
    var writes: [String] = []
    var readFails = false
    lazy var model = MeetingNotesModel(defaults: defaults, readKey: { [unowned self] role, _ in
        self.reads.append(role)
        if self.readFails { throw MeetingNotesError("测试钥匙串读取失败") }
        return "stored-" + role
    }, writeKey: { [unowned self] _, role, _ in self.writes.append(role) })

    init() throws {
        defaults = UserDefaults(suiteName: suite)!
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }
    func cleanup() {
        defaults.removePersistentDomain(forName: suite)
        try? FileManager.default.removeItem(at: directory)
    }
    func recording(_ name: String = "meeting.m4a") throws -> URL {
        let source = directory.appendingPathComponent(name)
        try Data("fixture media".utf8).write(to: source)
        var document = try MeetingNotesDocument(source: source, duration: 10,
            transcriptionService: AIServiceConfiguration(baseURL: "https://api.openai.com/v1", model: "gpt-transcribe"))
        document.segments = [.init(id: 1, start: 0, end: 10, text: "决定周五交付。")]
        document.transcriptComplete = true; document.summary = "原稿 [S1]"
        try MeetingNotesStore.save(document, for: source)
        return source
    }
}

@Suite @MainActor struct MeetingNotesModelTests {
    @Test func existingAPISettingsKeepTheirProviderWhenSubscriptionSupportIsAdded() throws {
        let data = Data(#"{"transcription":{"baseURL":"https://speech.example/v1","model":"speech"},"summary":{"baseURL":"https://api.deepseek.com","model":"deepseek-flash"}}"#.utf8)
        let settings = try JSONDecoder().decode(MeetingAISettings.self, from: data)
        #expect(!settings.useChatGPTSubscription)
        #expect(settings.transcription.baseURL == "https://speech.example/v1")
        #expect(MeetingAISettings().useChatGPTSubscription)
    }

    @Test func subscriptionSettingsDoNotRequireOrDeleteTheUnusedSummaryAPIKey() throws {
        let f = try ModelFixture(); defer { f.cleanup() }
        f.model.show()
        f.model.settings.summary.baseURL = "unfinished-api-address"
        f.model.settings.useChatGPTSubscription = true
        f.model.saveSettings()
        #expect(f.writes == ["transcription"])
        let stored = try #require(f.defaults.data(forKey: "meetingAI.v1"))
        #expect(try JSONDecoder().decode(MeetingAISettings.self, from: stored).useChatGPTSubscription)
    }

    @Test func signOutRemovesAllTokensAndKeepsVerifiedAccountMapping() {
        let credentials = ChatGPTCredentials(subject: "account-1", clientID: "oaiapp_registered",
            name: "Test User", email: "test@example.com", accessToken: "test-access", refreshToken: "test-refresh",
            idToken: "test-id-token", expiresAt: Date().addingTimeInterval(100),
            scopes: ["resource.invoke", "chatgpt.tokens.use.direct"], identityNonce: "test-nonce")
        let registration = credentials.signedOutRegistration
        #expect(registration.subject == credentials.subject && registration.clientID == credentials.clientID)
        #expect(registration.accessToken.isEmpty && registration.refreshToken.isEmpty && registration.idToken.isEmpty)
        #expect(registration.scopes.isEmpty && !registration.canUseChatGPTPlan)
        #expect(registration.identityNonce == nil)
    }

    @Test func choosingAnotherRecordingKeepsUnsavedKeys() throws {
        let f = try ModelFixture(); defer { f.cleanup() }
        f.model.show(try f.recording())
        f.model.transcriptionKey = "unsaved-speech"; f.model.summaryKey = "unsaved-summary"
        f.model.show(try f.recording("second.m4a"))
        f.model.show()
        #expect(f.model.transcriptionKey == "unsaved-speech")
        #expect(f.model.summaryKey == "unsaved-summary")
        #expect(f.reads.count == 2)
    }

    @Test func typingProviderURLDoesNotReadKeychainOrForwardOldKey() throws {
        let f = try ModelFixture(); defer { f.cleanup() }
        f.model.show()
        let original = f.model.settings.summary.baseURL
        f.model.summaryKey = "original-draft"
        for address in ["https://new.example", "https://new.example/v", "https://new.example/v1"] {
            f.model.settings.summary.baseURL = address
            #expect(f.model.summaryKey.isEmpty)
        }
        #expect(f.reads.count == 2)
        f.model.summaryKey = "second-draft"
        f.model.settings.summary.baseURL = original
        #expect(f.model.summaryKey == "original-draft")
        f.model.settings.summary.baseURL = "https://new.example/v1"
        #expect(f.model.summaryKey == "second-draft")
    }

    @Test func cosmeticAddressChangesAndSamePresetPreserveDraft() throws {
        let f = try ModelFixture(); defer { f.cleanup() }
        f.model.show(); f.model.summaryKey = "unsaved-summary"
        f.model.settings.summary.baseURL = "https://API.DEEPSEEK.COM/"
        f.model.loadKeyIfNeeded(role: "summary")
        #expect(f.model.summaryKey == "unsaved-summary")
        #expect(f.reads.count == 2)
    }

    @Test func failedKeyReadCannotSilentlyDeleteStoredCredentials() throws {
        let f = try ModelFixture(); defer { f.cleanup() }
        f.readFails = true
        f.model.show(); f.model.saveSettings()
        #expect(f.writes.isEmpty)
        #expect(f.model.message.contains("避免误删"))
    }

    @Test func viewingNotesNeverRewritesOrResurrectsSidecar() throws {
        let f = try ModelFixture(); defer { f.cleanup() }
        let source = try f.recording()
        f.model.show(source)
        let notes = MeetingNotesStore.url(for: source)
        let original = try Data(contentsOf: notes)
        #expect(f.model.saveEdits())
        #expect(try Data(contentsOf: notes) == original)
        try FileManager.default.removeItem(at: notes)
        #expect(f.model.preserveEditsBeforeLeaving())
        f.model.close()
        #expect(!FileManager.default.fileExists(atPath: notes.path))
    }

    @Test func editingDoesNotOverwriteExternalChanges() throws {
        let f = try ModelFixture(); defer { f.cleanup() }
        let source = try f.recording()
        f.model.show(source); f.model.summaryDraft = "我的修改"
        var changed = try #require(try MeetingNotesStore.load(for: source))
        changed.summary = "外部修改"
        try MeetingNotesStore.save(changed, for: source)
        #expect(!f.model.saveEdits())
        #expect(f.model.hasUnprotectedEdits)
        #expect(f.model.summaryDraft == "我的修改")
        #expect(try MeetingNotesStore.load(for: source)?.summary == "外部修改")
    }

    @Test func successfulExportAllowsLeavingAfterSaveFailureButProtectsNewEdits() throws {
        let f = try ModelFixture(); defer { f.cleanup() }
        let source = try f.recording()
        f.model.show(source); f.model.summaryDraft = "需保留的修改"
        try FileManager.default.removeItem(at: MeetingNotesStore.url(for: source))
        #expect(!f.model.preserveEditsBeforeLeaving())
        let output = f.directory.appendingPathComponent("export.md")
        try f.model.writeMarkdown(to: output)
        #expect(f.model.hasUnsavedEdits)
        #expect(!f.model.hasUnprotectedEdits)
        #expect(f.model.preserveEditsBeforeLeaving())
        #expect(try String(contentsOf: output, encoding: .utf8).contains("需保留的修改"))
        f.model.summaryDraft += "后来又修改"
        #expect(f.model.hasUnprotectedEdits)
    }

    @Test func switchingFilesSavesEditedDraftBeforeReloading() throws {
        let f = try ModelFixture(); defer { f.cleanup() }
        let first = try f.recording(), second = try f.recording("second.m4a")
        f.model.show(first); f.model.summaryDraft = "保存这条修改"
        f.model.show(second)
        #expect(try MeetingNotesStore.load(for: first)?.summary == "保存这条修改")
        #expect(f.model.source == second)
        #expect(f.model.summaryDraft == "原稿 [S1]")
    }
}
