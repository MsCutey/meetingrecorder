import Foundation
import Testing
@testable import RecorderSupport

private let speechService = AIServiceConfiguration(baseURL: "https://speech.example/v1", model: "speech")
private let notesService = AIServiceConfiguration(baseURL: "https://api.deepseek.com", model: "deepseek-flash")

private struct NotesFixture {
    let directory: URL
    let source: URL
    init() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        source = directory.appendingPathComponent("中文会议.m4a")
        try Data("audio-fixture".utf8).write(to: source)
    }
    func clean() { try? FileManager.default.removeItem(at: directory) }
    func document() throws -> MeetingNotesDocument {
        var document = try MeetingNotesDocument(source: source, duration: 600, transcriptionService: speechService)
        document.segments = [.init(id: 1, start: 0, end: 300, text: "周五交付；负责人未明确。"),
                             .init(id: 2, start: 300, end: 600, text: "第二阶段暂不决定。")]
        document.transcriptComplete = true
        return document
    }
}

@Test func notesPersistWithoutCredentialsAndDetectChangedSource() throws {
    let fixture = try NotesFixture(); defer { fixture.clean() }
    let document = try fixture.document()
    try MeetingNotesStore.save(document, for: fixture.source)
    let restored = try #require(try MeetingNotesStore.load(for: fixture.source))
    #expect(try restored.matches(source: fixture.source))
    #expect(restored.transcript.contains("[S2] 00:05:00–00:10:00"))
    #expect(restored.chatGPTPrompt.contains("负责人、期限"))
    #expect(restored.markdown.contains("非逐句时间戳"))
    let json = try String(contentsOf: MeetingNotesStore.url(for: fixture.source), encoding: .utf8)
    #expect(!json.contains("apiKey"))
    try Data("replacement-recording".utf8).write(to: fixture.source)
    #expect(try !restored.matches(source: fixture.source))
}

@Test func corruptCheckpointCannotSkipAudio() throws {
    let fixture = try NotesFixture(); defer { fixture.clean() }
    var document = try fixture.document()
    document.segments[1].start = 301
    try MeetingNotesStore.save(document, for: fixture.source)
    #expect(throws: MeetingNotesError.self) { try MeetingNotesStore.load(for: fixture.source) }
}

@Test func summaryFailurePreservesOldMinutesAndTranscript() async throws {
    let fixture = try NotesFixture(); defer { fixture.clean() }
    var document = try fixture.document()
    document.summary = "人工校正过的纪要"
    try MeetingNotesStore.save(document, for: fixture.source)
    var processor = MeetingNotesProcessor()
    processor.summarizeText = { _, _, _ in throw MeetingNotesError("模拟 429") }
    await #expect(throws: MeetingNotesError.self) {
        try await processor.summarize(source: fixture.source, configuration: notesService, apiKey: "fake-key")
    }
    let restored = try #require(try MeetingNotesStore.load(for: fixture.source))
    #expect(restored.summary == "人工校正过的纪要")
    #expect(restored.segments == document.segments)
}

@Test func summaryUsesFullTranscriptWithoutUploadingAudioAgain() async throws {
    let fixture = try NotesFixture(); defer { fixture.clean() }
    try MeetingNotesStore.save(fixture.document(), for: fixture.source)
    var processor = MeetingNotesProcessor()
    processor.recognize = { _, _, _ in Issue.record("总结重试不应调用转写"); return "" }
    processor.summarizeText = { text, configuration, _ in
        #expect(text.contains("周五交付")); #expect(text.contains("第二阶段"))
        #expect(configuration == notesService)
        return "## 待办\n周五交付，负责人未明确 [S1]"
    }
    let result = try await processor.summarize(source: fixture.source, configuration: notesService, apiKey: "fake-key")
    #expect(result.summary.contains("[S1]"))
    #expect(result.summaryService == notesService)
}

@Test func completedChunksResumeWithoutBeingBilledAgain() async throws {
    let fixture = try NotesFixture(); defer { fixture.clean() }
    var document = try fixture.document()
    document.segments.removeLast(); document.transcriptComplete = false
    try MeetingNotesStore.save(document, for: fixture.source)
    var processor = MeetingNotesProcessor()
    processor.plan = { _ in try MeetingAudio.makePlan(duration: 600) }
    processor.export = { _, range, destination in
        #expect(range.start == 300)
        try Data("fake-audio".utf8).write(to: destination)
    }
    processor.recognize = { _, _, _ in "后半段内容" }
    let result = try await processor.transcribe(source: fixture.source, configuration: speechService, apiKey: "fake-key", progress: { _ in })
    #expect(result.transcriptComplete)
    #expect(result.segments.count == 2)
    #expect(result.segments[0].text == document.segments[0].text)
    #expect(result.segments[1].text == "后半段内容")
}

@Test func failedSecondChunkKeepsFirstCheckpoint() async throws {
    let fixture = try NotesFixture(); defer { fixture.clean() }
    var processor = MeetingNotesProcessor()
    processor.plan = { _ in try MeetingAudio.makePlan(duration: 600) }
    processor.export = { _, range, destination in
        if range.start > 0 { throw MeetingNotesError("模拟导出失败") }
        try Data("fake-audio".utf8).write(to: destination)
    }
    processor.recognize = { _, _, _ in "第一段成功" }
    await #expect(throws: MeetingNotesError.self) {
        try await processor.transcribe(source: fixture.source, configuration: speechService, apiKey: "fake-key", progress: { _ in })
    }
    let checkpoint = try #require(try MeetingNotesStore.load(for: fixture.source))
    #expect(checkpoint.segments.count == 1)
    #expect(!checkpoint.transcriptComplete)
}

@Test func silentTranscriptionDoesNotCallSummaryModel() async throws {
    let fixture = try NotesFixture(); defer { fixture.clean() }
    var document = try fixture.document()
    document.segments = document.segments.map { .init(id: $0.id, start: $0.start, end: $0.end, text: "") }
    try MeetingNotesStore.save(document, for: fixture.source)
    var processor = MeetingNotesProcessor()
    processor.summarizeText = { _, _, _ in Issue.record("纯静音不应总结"); return "" }
    await #expect(throws: MeetingNotesError.self) {
        try await processor.summarize(source: fixture.source, configuration: notesService, apiKey: "fake-key")
    }
}

@Test func cancelledSummaryDoesNotReplacePreviousDraft() async throws {
    let fixture = try NotesFixture(); defer { fixture.clean() }
    var document = try fixture.document(); document.summary = "保留原稿"
    try MeetingNotesStore.save(document, for: fixture.source)
    var processor = MeetingNotesProcessor()
    processor.summarizeText = { _, _, _ in throw CancellationError() }
    await #expect(throws: CancellationError.self) {
        try await processor.summarize(source: fixture.source, configuration: notesService, apiKey: "fake-key")
    }
    #expect(try MeetingNotesStore.load(for: fixture.source)?.summary == "保留原稿")
}

@Test func restartingBacksUpCorruptNotesBeforeReplacingThem() async throws {
    let fixture = try NotesFixture(); defer { fixture.clean() }
    let damaged = Data("invalid notes data".utf8)
    try damaged.write(to: MeetingNotesStore.url(for: fixture.source))
    var processor = MeetingNotesProcessor()
    processor.plan = { _ in try MeetingAudio.makePlan(duration: 30) }
    processor.export = { _, _, file in try Data("audio".utf8).write(to: file) }
    processor.recognize = { _, _, _ in "新的转写" }
    let result = try await processor.transcribe(source: fixture.source, configuration: speechService, apiKey: "fake-key",
                                               restart: true, progress: { _ in })
    #expect(result.transcriptComplete)
    let backups = try FileManager.default.contentsOfDirectory(at: fixture.directory, includingPropertiesForKeys: nil)
        .filter { $0.lastPathComponent.contains(".backup-") }
    #expect(backups.count == 1)
    #expect(try Data(contentsOf: #require(backups.first)) == damaged)
}

@Test func changingSpeechProviderDoesNotMixCheckpoints() async throws {
    let fixture = try NotesFixture(); defer { fixture.clean() }
    var document = try fixture.document(); document.transcriptComplete = false; document.segments.removeLast()
    try MeetingNotesStore.save(document, for: fixture.source)
    var processor = MeetingNotesProcessor()
    processor.plan = { _ in try MeetingAudio.makePlan(duration: 600) }
    processor.recognize = { _, _, _ in Issue.record("服务变化时不得继续调用"); return "" }
    let changed = AIServiceConfiguration(baseURL: "https://different.example/v1", model: "speech")
    await #expect(throws: MeetingNotesError.self) {
        try await processor.transcribe(source: fixture.source, configuration: changed, apiKey: "fake-key", progress: { _ in })
    }
    #expect(try MeetingNotesStore.load(for: fixture.source)?.segments == document.segments)
}
