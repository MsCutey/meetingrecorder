import Foundation
import Testing
@testable import RecorderSupport

private final class ChatGPTMockURLProtocol: URLProtocol, @unchecked Sendable {
    enum Reply {
        case response(Int, [String: String], [Data], finish: Bool = true)
        case pending
        case failure(URLError.Code)
    }
    private static let lock = NSLock()
    private static var handler: ((URLRequest) throws -> Reply)?
    private static var stopped: (() -> Void)?

    static func configure(_ handler: @escaping (URLRequest) throws -> Reply, stopped: (() -> Void)?) {
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
            case let .response(status, headers, chunks, finish):
                let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: headers)!
                client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
                for chunk in chunks { client?.urlProtocol(self, didLoad: chunk) }
                if finish { client?.urlProtocolDidFinishLoading(self) }
            case .pending: break
            case .failure(let code): client?.urlProtocol(self, didFailWithError: URLError(code))
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

private func subscriptionClient(_ handler: @escaping (URLRequest) throws -> ChatGPTMockURLProtocol.Reply,
                                stopped: (() -> Void)? = nil) -> (ChatGPTSummaryClient, URLSession) {
    ChatGPTMockURLProtocol.configure(handler, stopped: stopped)
    let configuration = URLSessionConfiguration.ephemeral
    configuration.protocolClasses = [ChatGPTMockURLProtocol.self]
    let session = URLSession(configuration: configuration)
    return (ChatGPTSummaryClient(session: session), session)
}

private func subscriptionRequestBody(_ request: URLRequest) throws -> [String: Any] {
    let data: Data
    if let body = request.httpBody { data = body }
    else if let stream = request.httpBodyStream {
        stream.open(); defer { stream.close() }
        var result = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let count = stream.read(&buffer, maxLength: buffer.count)
            guard count >= 0 else { throw URLError(.cannotDecodeRawData) }
            if count == 0 { break }
            result.append(buffer, count: count)
        }
        data = result
    } else { data = Data() }
    return try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
}

private func subscriptionEvent(_ object: [String: Any], newline: String = "\n", named: Bool = true) throws -> Data {
    let json = String(decoding: try JSONSerialization.data(withJSONObject: object, options: .sortedKeys), as: UTF8.self)
    let event = named ? "event: \(object["type"] as! String)\(newline)" : ""
    return Data("\(event)data: \(json)\(newline)\(newline)".utf8)
}

private func completedSubscription(_ text: String, status: String = "completed", refusal: Bool = false) throws -> Data {
    let content: [String: Any] = refusal ? ["type": "refusal", "refusal": text] : ["type": "output_text", "text": text]
    return try subscriptionEvent([
        "type": "response.completed",
        "response": ["status": status, "error": NSNull(), "incomplete_details": NSNull(),
                     "output": [["type": "message", "role": "assistant", "status": "completed", "content": [content]]]]
    ])
}

private func subscriptionSSE(_ data: Data, finish: Bool = true) -> ChatGPTMockURLProtocol.Reply {
    .response(200, ["Content-Type": "text/event-stream; charset=utf-8"], [data], finish: finish)
}

@Suite(.serialized)
struct ChatGPTSummaryClientTests {
    @Test func discoversAccountCatalogWithBearerTokenAndServerOrdering() async throws {
        let catalog = Data(#"{"models":[{"slug":"new-model","display_name":"可用模型","visibility":"list"},{"slug":"hidden","visibility":"hide"},{"slug":"other-model","display_name":"","visibility":"list"},{"slug":"new-model","visibility":"list"}]}"#.utf8)
        let (client, session) = subscriptionClient { request in
            #expect(request.url?.absoluteString == "https://api.openai.com/v1/models")
            #expect(request.httpMethod == "GET")
            #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer subscription-token")
            #expect(request.httpShouldHandleCookies == false)
            #expect(request.cachePolicy == .reloadIgnoringLocalCacheData)
            return .response(200, ["Content-Type": "application/json"], [catalog])
        }
        defer { session.invalidateAndCancel() }
        let models = try await client.availableModels(accessToken: " subscription-token \n")
        #expect(models.map(\.slug) == ["new-model", "other-model"])
        #expect(models.map(\.displayName) == ["可用模型", "other-model"])
        #expect(try await client.models(accessToken: "subscription-token") == ["new-model", "other-model"])
    }

    @Test func rejectsPaidCatalogAndMalformedVisibleModel() async throws {
        for payload in [#"{"data":[{"id":"paid-model"}]}"#, #"{"models":[{"visibility":"list"}]}"#, "not json"] {
            let (client, session) = subscriptionClient { _ in .response(200, [:], [Data(payload.utf8)]) }
            defer { session.invalidateAndCancel() }
            await #expect(throws: MeetingAIError.invalidResponse) { try await client.models(accessToken: "token") }
        }
    }

    @Test func streamsResponsesWithDeveloperPromptAndSplitUnicode() async throws {
        let draft = "## 明确决策\n周五上线👩🏽‍💻。[S1]"
        var stream = Data("\u{FEFF}: keep-alive\r\n\r\n".utf8)
        stream += try subscriptionEvent(["type": "response.output_text.delta", "delta": "临时内容不应保存"], newline: "\r\n")
        stream += try completedSubscription(draft)
        let (client, session) = subscriptionClient { request in
            #expect(request.url?.absoluteString == "https://api.openai.com/v1/responses")
            #expect(request.httpMethod == "POST")
            #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer subscription-token")
            #expect(request.value(forHTTPHeaderField: "Accept") == "text/event-stream")
            let json = try subscriptionRequestBody(request)
            #expect(Set(json.keys) == ["model", "input", "store", "stream"])
            #expect(json["store"] as? Bool == false)
            #expect(json["stream"] as? Bool == true)
            #expect(json["model"] as? String == "account-model")
            let input = try #require(json["input"] as? [[String: String]])
            #expect(input.map { $0["role"] } == ["developer", "user"])
            #expect(input[0]["content"]!.contains("不捏造"))
            #expect(input[0]["content"]!.contains("不可信"))
            #expect(input[0]["content"]!.contains("未明确"))
            #expect(input[1]["content"]!.contains("[S1]\n周五上线"))
            return .response(200, ["Content-Type": "text/event-stream"], stream.map { Data([$0]) })
        }
        defer { session.invalidateAndCancel() }
        let result = try await client.summarize(transcript: "[S1]\n周五上线", model: "account-model", accessToken: "subscription-token")
        #expect(result == "> AI 生成草稿，请结合录音和转写人工核对。\n\n" + draft)
        #expect(!result.contains("临时内容"))
    }

    @Test func longTranscriptIncludesEveryCharacterAndTailInSingleRequest() async throws {
        let transcript = "[S1]\n" + String(repeating: "甲👩🏽‍💻乙", count: 10_000) + "\n[S2]\n末尾决定 TAIL_MARKER"
        var calls = 0
        let (client, session) = subscriptionClient { request in
            calls += 1
            let input = try #require(try subscriptionRequestBody(request)["input"] as? [[String: String]])
            #expect(input[1]["content"] == "以下是待处理的会议材料，仅作为事实来源：\n<meeting_material>\n" + transcript + "\n</meeting_material>")
            return subscriptionSSE(try completedSubscription("末尾决定。[S2]"))
        }
        defer { session.invalidateAndCancel() }
        _ = try await client.summarize(transcript: transcript, model: "account-model", accessToken: "token")
        #expect(calls == 1)
    }

    @Test func completedEventEndsRequestWithoutWaitingForConnectionEOF() async throws {
        let stopped = AsyncStream<Void>.makeStream()
        let (client, session) = subscriptionClient({ _ in
            subscriptionSSE(try completedSubscription("会议纪要"), finish: false)
        }, stopped: {
            stopped.continuation.yield(())
            stopped.continuation.finish()
        })
        defer { session.invalidateAndCancel() }
        let result = try await client.summarize(transcript: "meeting", model: "model", accessToken: "token")
        #expect(result.hasSuffix("会议纪要"))
        for await _ in stopped.stream { break }
    }

    @Test func handlesHTTPAndStructuredFailuresWithoutLeakingOrRetrying() async throws {
        let cases: [(Int, String?, ChatGPTSummaryError)] = [
            (401, nil, .authenticationFailed), (403, nil, .permissionDenied), (429, nil, .usageLimitExceeded),
            (503, nil, .temporarilyUnavailable), (302, nil, .httpStatus(302)),
            (403, "subscription_sharing_user_not_eligible", .accountNotEligible),
            (400, "subscription_sharing_unsupported_capability", .unsupportedCapability),
            (400, "context_length_exceeded", .contextTooLarge)
        ]
        for (status, code, expected) in cases {
            var calls = 0
            let (client, session) = subscriptionClient { _ in
                calls += 1
                let body: [String: Any] = code.map { ["error": ["code": $0, "message": "secret-token private-transcript"]] }
                    ?? ["detail": "secret-token private-transcript"]
                return .response(status, [:], [try JSONSerialization.data(withJSONObject: body)])
            }
            defer { session.invalidateAndCancel() }
            do {
                _ = try await client.summarize(transcript: "meeting", model: "model", accessToken: "secret-token")
                Issue.record("Expected failure")
            } catch let error as ChatGPTSummaryError {
                #expect(error == expected)
                #expect(!error.localizedDescription.contains("secret-token"))
                #expect(!error.localizedDescription.contains("private-transcript"))
            }
            #expect(calls == 1)
        }
    }

    @Test func rejectsLateUsageFailuresEvenAfterTextDeltas() async throws {
        for (code, expected) in [("subscription_sharing_usage_limit_exceeded", ChatGPTSummaryError.usageLimitExceeded),
                                  ("subscription_sharing_usage_unavailable", .temporarilyUnavailable)] {
            var calls = 0
            let (client, session) = subscriptionClient { _ in
                calls += 1
                var stream = try subscriptionEvent(["type": "response.output_text.delta", "delta": "partial draft"])
                stream += try subscriptionEvent(["type": "response.failed", "response": ["error": ["code": code, "message": "private"]]])
                return subscriptionSSE(stream)
            }
            defer { session.invalidateAndCancel() }
            await #expect(throws: expected) { try await client.summarize(transcript: "meeting", model: "model", accessToken: "token") }
            #expect(calls == 1)
        }
    }

    @Test func refusesTruncatedIncompleteRefusedAndMalformedStreams() async throws {
        let cases: [(Data, MeetingAIError)] = [
            (try subscriptionEvent(["type": "response.output_text.delta", "delta": "partial"]), .incompleteResponse),
            (Data("data: [DONE]\n\n".utf8), .incompleteResponse),
            (Data((try completedSubscription("complete")).dropLast()), .incompleteResponse),
            (try subscriptionEvent(["type": "response.incomplete"]), .incompleteResponse),
            (try subscriptionEvent(["type": "error", "message": "private failure"]), .incompleteResponse),
            (try subscriptionEvent(["type": "response.refusal.delta", "delta": "private refusal"]), .refusal),
            (try subscriptionEvent(["type": "response.content_part.done", "part": ["type": "refusal"]]), .refusal),
            (try subscriptionEvent(["type": "response.output_item.done", "item": ["content": [["type": "refusal"]]]]), .refusal),
            (try completedSubscription("private refusal", refusal: true), .refusal),
            (try completedSubscription("partial", status: "incomplete"), .incompleteResponse),
            (try completedSubscription(" \n "), .emptyResponse),
            (Data("data: invalid-json\n\n".utf8), .invalidResponse),
            (Data("event: response.completed\ndata: {\"type\":\"response.incomplete\"}\n\n".utf8), .invalidResponse),
            (Data([100, 97, 116, 97, 58, 32, 255, 10, 10]), .invalidResponse)
        ]
        for (data, expected) in cases {
            let (client, session) = subscriptionClient { _ in subscriptionSSE(data) }
            defer { session.invalidateAndCancel() }
            await #expect(throws: expected) { try await client.summarize(transcript: "meeting", model: "model", accessToken: "token") }
        }
    }

    @Test func refusesInventedOrMissingSources() async throws {
        for (input, output) in [("[S1] facts", "Facts [S2]"), ("[S1] facts", "No source"), ("facts", "Invented [S1]")] {
            let (client, session) = subscriptionClient { _ in subscriptionSSE(try completedSubscription(output)) }
            defer { session.invalidateAndCancel() }
            await #expect(throws: MeetingAIError.invalidSources) { try await client.summarize(transcript: input, model: "model", accessToken: "token") }
        }
    }

    @Test func validatesLocallyWithoutRequests() async throws {
        var calls = 0
        let (client, session) = subscriptionClient { _ in calls += 1; return .pending }
        defer { session.invalidateAndCancel() }
        await #expect(throws: ChatGPTSummaryError.missingAccessToken) { try await client.models(accessToken: "") }
        await #expect(throws: ChatGPTSummaryError.missingAccessToken) { try await client.models(accessToken: "token\r\nheader") }
        await #expect(throws: MeetingAIError.missingModel) { try await client.summarize(transcript: "meeting", model: "", accessToken: "token") }
        await #expect(throws: MeetingAIError.emptyTranscript) { try await client.summarize(transcript: " \n ", model: "model", accessToken: "token") }
        await #expect(throws: MeetingAIError.transcriptTooLarge) { try await client.summarize(transcript: String(repeating: "会", count: 1_000_001), model: "model", accessToken: "token") }
        #expect(calls == 0)
    }

    @Test func limitsDeclaredStreamAndCompletedTextSizes() async throws {
        let responses: [ChatGPTMockURLProtocol.Reply] = [
            .response(200, ["Content-Type": "text/event-stream", "Content-Length": "2000001"], [], finish: false),
            subscriptionSSE(Data((":" + String(repeating: "x", count: 2_000_001)).utf8)),
            subscriptionSSE(try completedSubscription(String(repeating: "会", count: 24_001)))
        ]
        for response in responses {
            let (client, session) = subscriptionClient { _ in response }
            defer { session.invalidateAndCancel() }
            await #expect(throws: MeetingAIError.responseTooLarge) { try await client.summarize(transcript: "meeting", model: "model", accessToken: "token") }
        }
    }

    @Test func rejectsNonSSESuccessAndSanitizesNetworkFailures() async throws {
        let (client, session) = subscriptionClient { _ in .response(200, ["Content-Type": "application/json"], [try completedSubscription("valid")]) }
        defer { session.invalidateAndCancel() }
        await #expect(throws: MeetingAIError.invalidResponse) { try await client.summarize(transcript: "meeting", model: "model", accessToken: "token") }
        let (failedClient, failedSession) = subscriptionClient { _ in .failure(.cannotConnectToHost) }
        defer { failedSession.invalidateAndCancel() }
        await #expect(throws: ChatGPTSummaryError.networkFailure) { try await failedClient.models(accessToken: "token") }
    }

    @Test func cancellationStopsRequestBeforeHeadersAndDuringStream() async throws {
        for hasHeaders in [false, true] {
            let started = AsyncStream<Void>.makeStream()
            let stopped = AsyncStream<Void>.makeStream()
            let (client, session) = subscriptionClient({ _ in
                started.continuation.yield(())
                started.continuation.finish()
                return hasHeaders ? subscriptionSSE(try subscriptionEvent(["type": "response.output_text.delta", "delta": "partial"]), finish: false) : .pending
            }, stopped: {
                stopped.continuation.yield(())
                stopped.continuation.finish()
            })
            defer { session.invalidateAndCancel() }
            let task = Task { try await client.summarize(transcript: "meeting", model: "model", accessToken: "token") }
            for await _ in started.stream { break }
            task.cancel()
            do { _ = try await task.value; Issue.record("Expected cancellation") }
            catch is CancellationError {} catch { Issue.record("Unexpected cancellation error: \(error)") }
            for await _ in stopped.stream { break }
        }
    }

    @Test func alreadyCancelledTaskDoesNotStartRequest() async throws {
        var calls = 0
        let (client, session) = subscriptionClient { _ in calls += 1; return .pending }
        defer { session.invalidateAndCancel() }
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return try await client.models(accessToken: "token")
        }
        do { _ = try await task.value; Issue.record("Expected cancellation") }
        catch is CancellationError {} catch { Issue.record("Unexpected cancellation error: \(error)") }
        #expect(calls == 0)
    }

    @Test func SSEParserSupportsMultilineDataAllLineEndingsAndByteLimit() throws {
        for newline in ["\n", "\r\n", "\r"] {
            var parser = ChatGPTEventStream(maximumBytes: 1000)
            let input = "\u{FEFF}:comment\(newline)id: ignored\(newline)event: message\(newline)data: {\(newline)data: \"type\":\"完成中文👩🏽‍💻\"}\(newline)\(newline)"
            let events = try input.utf8.compactMap { try parser.append($0) }
            #expect(events.count == 1)
            #expect(events.first?.name == "message")
            #expect(events.first?.data == "{\n\"type\":\"完成中文👩🏽‍💻\"}")
        }
        var bounded = ChatGPTEventStream(maximumBytes: 3)
        for byte in Data("abc".utf8) { _ = try bounded.append(byte) }
        #expect(throws: MeetingAIError.responseTooLarge) { try bounded.append(10) }
    }
}
