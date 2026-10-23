import AVFoundation
import Foundation
import Testing
@testable import RecorderSupport

private final class PipelineURLProtocol: URLProtocol, @unchecked Sendable {
    enum Reply {
        case response(Int, Data)
        case unfinished(headers: [String: String], data: Data)
        case pending
    }
    private static let lock = NSLock()
    private static var handler: ((URLRequest) throws -> Reply)?

    static func configure(_ handler: @escaping (URLRequest) throws -> Reply) {
        lock.lock(); defer { lock.unlock() }
        self.handler = handler
    }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock(); let handler = Self.handler; Self.lock.unlock()
        do {
            guard let reply = try handler?(request) else { throw URLError(.badServerResponse) }
            switch reply {
            case .response(let status, let data):
                respond(status: status, headers: [:], data: data)
                client?.urlProtocolDidFinishLoading(self)
            case .unfinished(let headers, let data): respond(status: 200, headers: headers, data: data)
            case .pending: break
            }
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    private func respond(status: Int, headers: [String: String], data: Data) {
        let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: headers)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        if !data.isEmpty { client?.urlProtocol(self, didLoad: data) }
    }
    override func stopLoading() {}
}

private struct PipelineFixture {
    let directory: URL
    let source: URL
    init(duration: Double = 1) throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("MeetingPipeline-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        source = directory.appendingPathComponent("合成会议.wav")
        let format = try #require(AVAudioFormat(standardFormatWithSampleRate: 16_000, channels: 1))
        let audio = try AVAudioFile(forWriting: source, settings: format.settings)
        let buffer = try #require(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 16_000))
        let samples = try #require(buffer.floatChannelData?[0])
        for frame in 0..<16_000 { samples[frame] = Float(0.15 * sin(2 * .pi * 440 * Double(frame) / 16_000)) }
        var remaining = Int(duration * 16_000)
        while remaining > 0 {
            let count = min(16_000, remaining)
            buffer.frameLength = AVAudioFrameCount(count)
            try audio.write(from: buffer)
            remaining -= count
        }
    }
    func clean() {
        try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: directory.path)
        try? FileManager.default.removeItem(at: directory)
    }
    func completedDocument() throws -> MeetingNotesDocument {
        var document = try MeetingNotesDocument(source: source, duration: 1, transcriptionService: pipelineSpeech)
        document.segments = [.init(id: 1, start: 0, end: 1, text: "保留原始发言。")]
        document.transcriptComplete = true
        document.summary = "人工校对稿"
        return document
    }
}

private let pipelineSpeech = AIServiceConfiguration(baseURL: "https://speech.example/v1", model: "test-transcribe")
private let pipelineSummary = AIServiceConfiguration(baseURL: "https://api.deepseek.com", model: "deepseek-flash")

private func pipelineClient(_ handler: @escaping (URLRequest) throws -> PipelineURLProtocol.Reply) -> (MeetingAIClient, URLSession) {
    PipelineURLProtocol.configure(handler)
    let configuration = URLSessionConfiguration.ephemeral
    configuration.protocolClasses = [PipelineURLProtocol.self]
    let session = URLSession(configuration: configuration)
    return (MeetingAIClient(session: session), session)
}

private func pipelineBody(_ request: URLRequest) throws -> Data {
    if let data = request.httpBody { return data }
    let stream = try #require(request.httpBodyStream)
    stream.open(); defer { stream.close() }
    var result = Data()
    var buffer = [UInt8](repeating: 0, count: 8192)
    while stream.hasBytesAvailable {
        let count = stream.read(&buffer, maxLength: buffer.count)
        guard count >= 0 else { throw URLError(.cannotDecodeRawData) }
        if count == 0 { break }
        result.append(buffer, count: count)
    }
    return result
}

private func pipelineChat(_ text: String) throws -> Data {
    try JSONSerialization.data(withJSONObject: ["choices": [["message": ["content": text], "finish_reason": "stop"]]])
}

/// A broken byte-limit implementation must fail the test instead of waiting forever for EOF.
private func pipelineDeadline<T>(_ operation: @escaping () async throws -> T) async throws -> T {
    try await withThrowingTaskGroup(of: T.self) { group in
        group.addTask { try await operation() }
        group.addTask {
            try await Task.sleep(for: .seconds(5))
            throw MeetingNotesError("测试请求超过截止时间")
        }
        defer { group.cancelAll() }
        return try await group.next()!
    }
}

@Suite(.serialized)
struct MeetingNotesIntegrationTests {
    @Test func realAudioResumesAfterHTTPFailureAndCancellationThenSummarizes() async throws {
        let fixture = try PipelineFixture(duration: 301); defer { fixture.clean() }
        let original = try Data(contentsOf: fixture.source)
        let requestStarted = AsyncStream<Void>.makeStream()
        var audioRequests = 0
        var summaryRequests = 0
        var exports: [URL] = []
        let (client, session) = pipelineClient { request in
            let body = try pipelineBody(request)
            if request.url?.path.hasSuffix("/audio/transcriptions") == true {
                audioRequests += 1
                #expect(request.url?.host == "speech.example")
                let boundary = try #require(request.value(forHTTPHeaderField: "Content-Type")?.components(separatedBy: "boundary=").last)
                let start = try #require(body.range(of: Data("Content-Type: audio/mp4\r\n\r\n".utf8))).upperBound
                let end = try #require(body.range(of: Data("\r\n--\(boundary)--\r\n".utf8))).lowerBound
                let uploadedAudio = fixture.directory.appendingPathComponent("uploaded-\(audioRequests).m4a")
                try body[start..<end].write(to: uploadedAudio)
                let decoded = try AVAudioFile(forReading: uploadedAudio)
                let seconds = Double(decoded.length) / decoded.processingFormat.sampleRate
                #expect(abs(seconds - (audioRequests == 1 ? 300 : 1)) < 0.2)
                #expect(!String(decoding: body, as: UTF8.self).contains(fixture.source.lastPathComponent))
                switch audioRequests {
                case 1: return .response(200, Data(#"{"text":"第一段：团队讨论是否周五发布。"}"#.utf8))
                case 2: return .response(429, Data("quota".utf8))
                case 3:
                    requestStarted.continuation.yield(())
                    requestStarted.continuation.finish()
                    return .pending
                default: return .response(200, Data(#"{"text":"最终决定推迟发布；负责人未明确。"}"#.utf8))
                }
            }
            summaryRequests += 1
            #expect(request.url?.absoluteString == "https://api.deepseek.com/chat/completions")
            let object = try #require(try JSONSerialization.jsonObject(with: body) as? [String: Any])
            let messages = try #require(object["messages"] as? [[String: String]])
            #expect(messages[1]["content"]!.contains("第一段"))
            #expect(messages[1]["content"]!.contains("最终决定推迟"))
            if summaryRequests == 1 { return .response(429, Data()) }
            return .response(200, try pipelineChat("## 明确决策\n讨论了周五发布。[S1]\n最终决定推迟发布，负责人未明确。[S2]"))
        }
        defer { session.invalidateAndCancel() }
        var processor = MeetingNotesProcessor(client: client)
        processor.export = { source, range, destination in
            exports.append(destination)
            try await MeetingAudio.export(source: source, range: range, to: destination)
        }
        await #expect(throws: MeetingAIError.rateLimited) {
            try await processor.transcribe(source: fixture.source, configuration: pipelineSpeech, apiKey: "fixture-key", progress: { _ in })
        }
        let checkpoint = try #require(try MeetingNotesStore.load(for: fixture.source))
        #expect(checkpoint.segments.count == 1)
        #expect(!checkpoint.transcriptComplete)
        let savedBytes = try Data(contentsOf: MeetingNotesStore.url(for: fixture.source))
        let cancelledAttempt = Task {
            try await processor.transcribe(source: fixture.source, configuration: pipelineSpeech, apiKey: "fixture-key", progress: { _ in })
        }
        for await _ in requestStarted.stream { break }
        cancelledAttempt.cancel()
        await #expect(throws: CancellationError.self) { try await cancelledAttempt.value }
        #expect(try Data(contentsOf: MeetingNotesStore.url(for: fixture.source)) == savedBytes)
        var complete = try await processor.transcribe(source: fixture.source, configuration: pipelineSpeech, apiKey: "fixture-key", progress: { _ in })
        #expect(complete.transcriptComplete && complete.segments.count == 2)
        #expect(complete.segments[0] == checkpoint.segments[0])
        #expect(complete.segments[1].start == 300 && complete.segments[1].end == 301)
        #expect(exports.allSatisfy { !FileManager.default.fileExists(atPath: $0.deletingLastPathComponent().path) })
        complete.summary = "人工修订旧稿"
        try MeetingNotesStore.save(complete, for: fixture.source)
        let oldNotes = try Data(contentsOf: MeetingNotesStore.url(for: fixture.source))
        await #expect(throws: MeetingAIError.rateLimited) {
            try await processor.summarize(source: fixture.source, configuration: pipelineSummary, apiKey: "fixture-key")
        }
        #expect(try Data(contentsOf: MeetingNotesStore.url(for: fixture.source)) == oldNotes)
        let summary = try await processor.summarize(source: fixture.source, configuration: pipelineSummary, apiKey: "fixture-key")
        _ = try await processor.transcribe(source: fixture.source, configuration: pipelineSpeech, apiKey: "fixture-key", progress: { _ in })
        #expect(audioRequests == 4 && summaryRequests == 2)
        #expect(try MeetingNotesStore.load(for: fixture.source)?.summary == summary.summary)
        #expect(summary.markdown.contains("00:05:00–00:05:01"))
        #expect(!summary.markdown.contains("fixture-key"))
        #expect(try Data(contentsOf: fixture.source) == original)
    }

    @Test func unwritableRecordingFolderFailsBeforeAnyUpload() async throws {
        let fixture = try PipelineFixture(); defer { fixture.clean() }
        var calls = 0
        let (client, session) = pipelineClient { _ in calls += 1; return .response(200, Data(#"{"text":"不可上传"}"#.utf8)) }
        defer { session.invalidateAndCancel() }
        try FileManager.default.setAttributes([.posixPermissions: 0o555], ofItemAtPath: fixture.directory.path)
        await #expect(throws: CocoaError.self) {
            try await MeetingNotesProcessor(client: client).transcribe(source: fixture.source, configuration: pipelineSpeech, apiKey: "fixture-key", progress: { _ in })
        }
        #expect(calls == 0)
        #expect(!FileManager.default.fileExists(atPath: MeetingNotesStore.url(for: fixture.source).path))
    }

    @Test func unwritableNotesFailBeforeSummaryRequestAndKeepOriginalBytes() async throws {
        let fixture = try PipelineFixture(); defer { fixture.clean() }
        try MeetingNotesStore.save(fixture.completedDocument(), for: fixture.source)
        let original = try Data(contentsOf: MeetingNotesStore.url(for: fixture.source))
        var calls = 0
        let (client, session) = pipelineClient { _ in calls += 1; return .response(200, try pipelineChat("不应请求。[S1]")) }
        defer { session.invalidateAndCancel() }
        try FileManager.default.setAttributes([.posixPermissions: 0o555], ofItemAtPath: fixture.directory.path)
        await #expect(throws: CocoaError.self) {
            try await MeetingNotesProcessor(client: client).summarize(source: fixture.source, configuration: pipelineSummary, apiKey: "fixture-key")
        }
        #expect(calls == 0)
        #expect(try Data(contentsOf: MeetingNotesStore.url(for: fixture.source)) == original)
    }

    @Test func cancelledRestartPreservesPreviousDraftAndBackup() async throws {
        let fixture = try PipelineFixture(); defer { fixture.clean() }
        try MeetingNotesStore.save(fixture.completedDocument(), for: fixture.source)
        let original = try Data(contentsOf: MeetingNotesStore.url(for: fixture.source))
        var processor = MeetingNotesProcessor()
        processor.export = { _, _, _ in throw CancellationError() }
        await #expect(throws: CancellationError.self) {
            try await processor.transcribe(source: fixture.source, configuration: pipelineSpeech, apiKey: "fixture-key", restart: true, progress: { _ in })
        }
        #expect(try Data(contentsOf: MeetingNotesStore.url(for: fixture.source)) == original)
        let backup = try #require(try FileManager.default.contentsOfDirectory(at: fixture.directory, includingPropertiesForKeys: nil).first { $0.lastPathComponent.contains("backup-") })
        #expect(try Data(contentsOf: backup) == original)
    }

    @Test func recordingChangedDuringExportStopsBeforeUpload() async throws {
        let fixture = try PipelineFixture(); defer { fixture.clean() }
        var calls = 0
        let (client, session) = pipelineClient { _ in calls += 1; return .response(200, Data(#"{"text":"不可上传"}"#.utf8)) }
        defer { session.invalidateAndCancel() }
        var processor = MeetingNotesProcessor(client: client)
        processor.export = { source, range, destination in
            try await MeetingAudio.export(source: source, range: range, to: destination)
            let writer = try FileHandle(forWritingTo: source)
            defer { try? writer.close() }
            try writer.seekToEnd()
            try writer.write(contentsOf: Data("changed".utf8))
        }
        await #expect(throws: MeetingNotesError.self) {
            try await processor.transcribe(source: fixture.source, configuration: pipelineSpeech, apiKey: "fixture-key", progress: { _ in })
        }
        #expect(calls == 0)
        #expect(try MeetingNotesStore.load(for: fixture.source)?.segments.isEmpty == true)
    }

    @Test(arguments: [true, false])
    func oversizedHTTPResponseStopsWithoutWaitingForEOF(advertiseLength: Bool) async throws {
        let (client, session) = pipelineClient { _ in
            .unfinished(headers: advertiseLength ? ["Content-Length": "2000001"] : [:],
                        // Fill the transport's initial buffer, then leave the body unfinished.
                        // The advertised size must reject it before the remaining body arrives.
                        data: Data(repeating: 32, count: advertiseLength ? 32_768 : 2_000_001))
        }
        defer { session.invalidateAndCancel() }
        await #expect(throws: MeetingAIError.responseTooLarge) {
            try await pipelineDeadline {
                try await client.summarize(transcript: "[S1]\n测试响应大小。", configuration: pipelineSummary, apiKey: "fixture-key")
            }
        }
    }
}
