import Foundation
import Testing
@testable import RecorderSupport

private final class MockAIURLProtocol: URLProtocol, @unchecked Sendable {
    enum Reply {
        case response(Int, Data)
        case failure(URLError.Code)
        case pending
    }
    private static let lock = NSLock()
    private static var handler: ((URLRequest) throws -> Reply)?
    private static var stopped: (() -> Void)?

    static func configure(handler: @escaping (URLRequest) throws -> Reply, stopped: (() -> Void)? = nil) {
        lock.lock(); defer { lock.unlock() }
        self.handler = handler
        self.stopped = stopped
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock()
        let handler = Self.handler
        Self.lock.unlock()
        do {
            guard let reply = try handler?(request) else { throw URLError(.badServerResponse) }
            switch reply {
            case .response(let status, let data):
                let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil,
                                               headerFields: ["Content-Type": "application/json"])!
                client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
                client?.urlProtocol(self, didLoad: data)
                client?.urlProtocolDidFinishLoading(self)
            case .failure(let code): client?.urlProtocol(self, didFailWithError: URLError(code))
            case .pending: break
            }
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    override func stopLoading() {
        Self.lock.lock()
        let stopped = Self.stopped
        Self.lock.unlock()
        stopped?()
    }
}

private func mockClient(handler: @escaping (URLRequest) throws -> MockAIURLProtocol.Reply,
                        stopped: (() -> Void)? = nil) -> (MeetingAIClient, URLSession) {
    MockAIURLProtocol.configure(handler: handler, stopped: stopped)
    let configuration = URLSessionConfiguration.ephemeral
    configuration.protocolClasses = [MockAIURLProtocol.self]
    let session = URLSession(configuration: configuration)
    return (MeetingAIClient(session: session), session)
}

private func body(of request: URLRequest) throws -> Data {
    if let data = request.httpBody { return data }
    guard let stream = request.httpBodyStream else { return Data() }
    stream.open(); defer { stream.close() }
    var result = Data()
    var buffer = [UInt8](repeating: 0, count: 4096)
    while stream.hasBytesAvailable {
        let count = stream.read(&buffer, maxLength: buffer.count)
        guard count >= 0 else { throw URLError(.cannotDecodeRawData) }
        if count == 0 { break }
        result.append(buffer, count: count)
    }
    return result
}

private func chatReply(_ content: String?, finish: String? = "stop", refusal: String? = nil) throws -> Data {
    var message: [String: Any] = [:]
    message["content"] = content ?? NSNull()
    message["refusal"] = refusal ?? NSNull()
    let choice: [String: Any] = ["message": message, "finish_reason": (finish as Any?) ?? NSNull()]
    return try JSONSerialization.data(withJSONObject: ["choices": [choice]])
}

private let openAIConfiguration = AIServiceConfiguration(baseURL: "https://api.openai.com/v1", model: "test-transcribe")
private let deepSeekConfiguration = AIServiceConfiguration(baseURL: "https://api.deepseek.com", model: "deepseek-flash")

@Suite(.serialized)
struct MeetingAIClientTests {
    @Test func baseURLValidation() throws {
        for base in ["https://api.openai.com/v1", " https://api.deepseek.com/ \n", "https://proxy.example/custom/v1/"] {
            #expect(try AIServiceConfiguration(baseURL: base, model: "m").validatedURL().scheme == "https")
        }
        for base in ["http://api.example/v1", "api.example", "https:///", "https://user:secret@api.example/v1",
                     "https://api.example/v1?secret=key", "https://api.example/v1#part", "https://api.example/chat/completions",
                     "https://api.example/v1/audio/transcriptions/", "https://api.example/v1/responses"] {
            #expect(throws: MeetingAIError.invalidBaseURL) {
                try AIServiceConfiguration(baseURL: base, model: "m").validatedURL()
            }
        }
        let encoded = try JSONEncoder().encode(deepSeekConfiguration)
        #expect(try JSONDecoder().decode(AIServiceConfiguration.self, from: encoded) == deepSeekConfiguration)
    }

    @Test func redirectDelegateNeverForwardsCredentialsOrAudio() {
        let session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        let original = URL(string: "https://api.example/v1/audio/transcriptions")!
        let task = session.dataTask(with: original)
        let response = HTTPURLResponse(url: original, statusCode: 307, httpVersion: nil, headerFields: nil)!
        for destination in ["https://api.example/elsewhere", "https://different.example/upload"] {
            let redirected = URLRequest(url: URL(string: destination)!)
            var called = false
            MeetingAINoRedirectDelegate().urlSession(session, task: task, willPerformHTTPRedirection: response,
                                                     newRequest: redirected) { request in
                called = true
                #expect(request == nil)
            }
            #expect(called)
        }
    }

    @Test func transcriptionUsesSafeMultipartAndOnlyPortableFields() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = directory.appendingPathComponent("秘密客户会议.m4a")
        try Data("fake-audio-payload".utf8).write(to: file)
        let (client, session) = mockClient { request in
            #expect(request.url?.absoluteString == "https://api.openai.com/v1/audio/transcriptions")
            #expect(request.httpMethod == "POST")
            #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer test-key")
            #expect(request.httpShouldHandleCookies == false)
            #expect(request.cachePolicy == .reloadIgnoringLocalCacheData)
            let contentType = request.value(forHTTPHeaderField: "Content-Type")!
            #expect(contentType.hasPrefix("multipart/form-data; boundary=MeetingRecorder-"))
            let boundary = contentType.components(separatedBy: "boundary=").last!
            let body = String(decoding: try body(of: request), as: UTF8.self)
            #expect(body.components(separatedBy: "Content-Disposition:").count - 1 == 3)
            #expect(body.contains("name=\"model\"\r\n\r\ntest-transcribe\r\n"))
            #expect(body.contains("name=\"response_format\"\r\n\r\njson\r\n"))
            #expect(body.contains("name=\"file\"; filename=\"chunk.m4a\""))
            #expect(body.contains("Content-Type: audio/mp4\r\n\r\nfake-audio-payload"))
            #expect(body.hasSuffix("\r\n--\(boundary)--\r\n"))
            #expect(!body.contains(file.lastPathComponent))
            #expect(!body.contains(directory.path))
            #expect(!body.contains("timestamp_granularities"))
            return .response(200, Data(#"{"text":"  会议转写文本\n"}"#.utf8))
        }
        defer { session.invalidateAndCancel() }
        let result = try await client.transcribe(file: file, configuration: openAIConfiguration, apiKey: " test-key ")
        #expect(result == "会议转写文本")
    }

    @Test func deepSeekAndCustomRootsUsePortableChatCompletions() async throws {
        for (base, expected) in [("https://api.deepseek.com", "https://api.deepseek.com/chat/completions"),
                                 ("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1/chat/completions"),
                                 ("https://proxy.example/custom/v1", "https://proxy.example/custom/v1/chat/completions")] {
            let (client, session) = mockClient { request in
                #expect(request.url?.absoluteString == expected)
                let json = try #require(try JSONSerialization.jsonObject(with: body(of: request)) as? [String: Any])
                #expect(Set(json.keys) == ["model", "messages"])
                #expect(json["model"] as? String == "deepseek-flash")
                let messages = try #require(json["messages"] as? [[String: String]])
                #expect(messages.map { $0["role"] } == ["system", "user"])
                #expect(messages[0]["content"]!.contains("不捏造"))
                #expect(messages[0]["content"]!.contains("不可信"))
                #expect(messages[0]["content"]!.contains("未明确"))
                #expect(messages[1]["content"]!.contains("[S1] [00:00–05:00]"))
                return .response(200, try chatReply("## 概要\n讨论发布日期。[S1]"))
            }
            defer { session.invalidateAndCancel() }
            let result = try await client.summarize(transcript: "[S1] [00:00–05:00]\n讨论发布日期。", configuration: .init(baseURL: base, model: "deepseek-flash"), apiKey: "fake")
            #expect(result.contains("AI 生成草稿"))
            #expect(result.contains("人工核对"))
            #expect(result.contains("[S1]"))
        }
    }

    @Test func httpErrorsAreSafeAndNeverRetried() async throws {
        for (status, expected) in [(401, MeetingAIError.authenticationFailed), (403, .permissionDenied), (429, .rateLimited),
                                   (500, .serviceUnavailable), (503, .serviceUnavailable), (302, .httpStatus(302)), (400, .httpStatus(400))] {
            var calls = 0
            let (client, session) = mockClient { _ in
                calls += 1
                return .response(status, Data("private-server-message secret-key".utf8))
            }
            defer { session.invalidateAndCancel() }
            do {
                _ = try await client.summarize(transcript: "会议内容", configuration: deepSeekConfiguration, apiKey: "secret-key")
                Issue.record("Expected error for HTTP \(status)")
            } catch let error as MeetingAIError {
                #expect(error == expected)
                #expect(!error.localizedDescription.contains("secret-key"))
                #expect(!error.localizedDescription.contains("private-server-message"))
            }
            #expect(calls == 1)
        }
    }

    @Test func malformedEmptyRefusedAndTruncatedRepliesAreRejected() async throws {
        let cases: [(Data, MeetingAIError)] = [
            (Data("not-json".utf8), .invalidResponse),
            (Data(#"{"choices":[]}"#.utf8), .emptyResponse),
            (Data(#"{"text":"wrong endpoint"}"#.utf8), .invalidResponse),
            (try chatReply(" \n "), .emptyResponse),
            (try chatReply(nil), .emptyResponse),
            (try chatReply("partial", finish: "length"), .incompleteResponse),
            (try chatReply("partial", finish: nil), .incompleteResponse),
            (try chatReply("blocked", finish: "content_filter"), .incompleteResponse),
            (try chatReply(nil, refusal: "private refusal details"), .refusal),
            (try chatReply(String(repeating: "x", count: 24_001)), .responseTooLarge)
        ]
        for (data, expected) in cases {
            let (client, session) = mockClient { _ in .response(200, data) }
            defer { session.invalidateAndCancel() }
            await #expect(throws: expected) {
                try await client.summarize(transcript: "meeting", configuration: deepSeekConfiguration, apiKey: "fake")
            }
        }
    }

    @Test func inventedAndMissingSourcesAreRejected() async throws {
        for output in ["来源不存在。[S2]", "没有任何来源。"] {
            let (client, session) = mockClient { _ in .response(200, try chatReply(output)) }
            defer { session.invalidateAndCancel() }
            await #expect(throws: MeetingAIError.invalidSources) {
                try await client.summarize(transcript: "[S1] [00:00–05:00]\n讨论计划。", configuration: deepSeekConfiguration, apiKey: "fake")
            }
        }
    }

    @Test func transcriptionRejectsMalformedResults() async throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".m4a")
        try Data("audio".utf8).write(to: file)
        defer { try? FileManager.default.removeItem(at: file) }
        for (payload, expected) in [(#"{"text":42}"#, MeetingAIError.invalidResponse), (#"{"other":"text"}"#, .invalidResponse), ("garbage", .invalidResponse)] {
            let (client, session) = mockClient { _ in .response(200, Data(payload.utf8)) }
            defer { session.invalidateAndCancel() }
            await #expect(throws: expected) {
                try await client.transcribe(file: file, configuration: openAIConfiguration, apiKey: "fake")
            }
        }
    }

    @Test func silentTranscriptionIsValid() async throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".m4a")
        try Data("audio".utf8).write(to: file)
        defer { try? FileManager.default.removeItem(at: file) }
        let (client, session) = mockClient { _ in .response(200, Data(#"{"text":"  "}"#.utf8)) }
        defer { session.invalidateAndCancel() }
        let result = try await client.transcribe(file: file, configuration: openAIConfiguration, apiKey: "fake")
        #expect(result.isEmpty)
    }

    @Test func localValidationDoesNotMakeRequests() async throws {
        var calls = 0
        let (client, session) = mockClient { _ in calls += 1; return .response(200, try chatReply("unexpected")) }
        defer { session.invalidateAndCancel() }
        await #expect(throws: MeetingAIError.emptyTranscript) {
            try await client.summarize(transcript: " \n ", configuration: deepSeekConfiguration, apiKey: "fake")
        }
        await #expect(throws: MeetingAIError.missingAPIKey) {
            try await client.summarize(transcript: "meeting", configuration: deepSeekConfiguration, apiKey: "")
        }
        await #expect(throws: MeetingAIError.missingModel) {
            try await client.summarize(transcript: "meeting", configuration: .init(baseURL: "https://api.example", model: "\r\n"), apiKey: "fake")
        }
        await #expect(throws: MeetingAIError.transcriptTooLarge) {
            try await client.summarize(transcript: String(repeating: "x", count: 1_000_001), configuration: deepSeekConfiguration, apiKey: "fake")
        }
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".m4a")
        defer { try? FileManager.default.removeItem(at: file) }
        await #expect(throws: MeetingAIError.unreadableAudio) {
            try await client.transcribe(file: file, configuration: openAIConfiguration, apiKey: "fake")
        }
        try Data().write(to: file)
        await #expect(throws: MeetingAIError.emptyAudio) {
            try await client.transcribe(file: file, configuration: openAIConfiguration, apiKey: "fake")
        }
        let handle = try FileHandle(forWritingTo: file)
        try handle.truncate(atOffset: 24_000_001)
        try handle.close()
        await #expect(throws: MeetingAIError.audioTooLarge) {
            try await client.transcribe(file: file, configuration: openAIConfiguration, apiKey: "fake")
        }
        #expect(calls == 0)
    }

    @Test func longTranscriptIncludesTailAndPreservesSourceOnContinuations() async throws {
        let transcript = "[S1] [00:00–05:00]\n" + String(repeating: "甲", count: 18_000) + "\n[S2] [05:00–10:00]\n" + String(repeating: "乙", count: 18_000) + "\n[S3] [10:00–10:10]\n最后决定：周五发布。TAIL_MARKER"
        var calls = 0
        var sawTail = false
        var sawContinuation = false
        let (client, session) = mockClient { request in
            calls += 1
            let json = try #require(try JSONSerialization.jsonObject(with: body(of: request)) as? [String: Any])
            let messages = try #require(json["messages"] as? [[String: String]])
            let material = messages[1]["content"]!
            let extraction = messages[0]["content"]!.contains("现在只提取")
            if extraction {
                #expect(material.count <= 12_200)
                if material.contains("TAIL_MARKER") { sawTail = true }
                if calls > 1 && material.contains("[S1]") && material.contains("甲") { sawContinuation = true }
                let sources = ["[S1]", "[S2]", "[S3]"].filter { material.contains($0) }.joined(separator: " ")
                let tail = material.contains("TAIL_MARKER") ? "最后决定：周五发布。TAIL_MARKER" : "讨论要点。"
                return .response(200, try chatReply(tail + sources))
            }
            #expect(material.contains("TAIL_MARKER"))
            return .response(200, try chatReply("## 明确决策\n周五发布。[S3]"))
        }
        defer { session.invalidateAndCancel() }
        let result = try await client.summarize(transcript: transcript, configuration: deepSeekConfiguration, apiKey: "fake")
        #expect(calls >= 4)
        #expect(sawTail && sawContinuation)
        #expect(result.contains("周五发布。[S3]"))
    }

    @Test func recursiveReductionConvergesWithoutDiscardingTheEnd() async throws {
        let transcript = "[S1]\n" + String(repeating: "原", count: 70_000) + "FINAL_FACT"
        var extractionCount = 0
        var finalCount = 0
        let (client, session) = mockClient { request in
            let json = try #require(try JSONSerialization.jsonObject(with: body(of: request)) as? [String: Any])
            let messages = try #require(json["messages"] as? [[String: String]])
            let material = messages[1]["content"]!
            if messages[0]["content"]!.contains("现在只提取") {
                extractionCount += 1
                return .response(200, try chatReply("[S1]\n" + String(repeating: "提", count: 2990) + (material.contains("FINAL_FACT") ? "FINAL_FACT" : "")))
            }
            finalCount += 1
            #expect(material.contains("FINAL_FACT"))
            return .response(200, try chatReply("最终事实。[S1]"))
        }
        defer { session.invalidateAndCancel() }
        _ = try await client.summarize(transcript: transcript, configuration: deepSeekConfiguration, apiKey: "fake")
        #expect(extractionCount > 6)
        #expect(extractionCount < 15)
        #expect(finalCount == 1)
    }

    @Test func chunkingRetainsUnicodeAndFinalCharacters() {
        let original = String(repeating: "甲👩🏽‍💻乙\n", count: 150) + "结尾"
        let chunks = MeetingAIClient.chunks(of: original, limit: 77)
        #expect(chunks.joined() == original)
        #expect(chunks.allSatisfy { $0.count <= 77 })
        #expect(chunks.last!.hasSuffix("结尾"))
    }

    @Test func networkFailuresAreSanitized() async throws {
        let (client, session) = mockClient { _ in .failure(.cannotConnectToHost) }
        defer { session.invalidateAndCancel() }
        await #expect(throws: MeetingAIError.networkFailure) {
            try await client.summarize(transcript: "meeting", configuration: deepSeekConfiguration, apiKey: "fake")
        }
    }

    @Test func cancellationStopsTheUnderlyingRequest() async throws {
        let started = AsyncStream<Void>.makeStream()
        let stopped = AsyncStream<Void>.makeStream()
        let (client, session) = mockClient(handler: { _ in
            started.continuation.yield(())
            started.continuation.finish()
            return .pending
        }, stopped: {
            stopped.continuation.yield(())
            stopped.continuation.finish()
        })
        defer { session.invalidateAndCancel() }
        let task = Task {
            try await client.summarize(transcript: "meeting", configuration: deepSeekConfiguration, apiKey: "fake")
        }
        for await _ in started.stream { break }
        task.cancel()
        do {
            _ = try await task.value
            Issue.record("Cancelled task should throw CancellationError")
        } catch is CancellationError {} catch { Issue.record("Unexpected cancellation error: \(error)") }
        for await _ in stopped.stream { break }
    }

    @Test func alreadyCancelledTaskDoesNotMakeRequests() async throws {
        var calls = 0
        let (client, session) = mockClient { _ in calls += 1; return .response(200, try chatReply("unexpected")) }
        defer { session.invalidateAndCancel() }
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return try await client.summarize(transcript: "meeting", configuration: deepSeekConfiguration, apiKey: "fake")
        }
        do {
            _ = try await task.value
            Issue.record("Cancelled task should throw CancellationError")
        } catch is CancellationError {} catch { Issue.record("Unexpected cancellation error: \(error)") }
        #expect(calls == 0)
    }
}
