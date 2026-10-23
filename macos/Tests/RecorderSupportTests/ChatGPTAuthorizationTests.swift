import Foundation
import CryptoKit
import Security
import Testing
@testable import RecorderSupport

private final class AuthURLProtocol: URLProtocol, @unchecked Sendable {
    static let lock = NSLock()
    static var handler: ((URLRequest) throws -> (Int, Data))?
    static var holdOpen = false
    static var stopped: (() -> Void)?
    private var onStop: (() -> Void)?
    static func set(holdOpen: Bool = false, stopped: (() -> Void)? = nil,
                    _ handler: @escaping (URLRequest) throws -> (Int, Data)) {
        lock.lock(); self.handler = handler; self.holdOpen = holdOpen; self.stopped = stopped; lock.unlock()
    }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock(); let handler = Self.handler, holdOpen = Self.holdOpen; onStop = Self.stopped; Self.lock.unlock()
        do {
            let (status, data) = try handler!(request)
            client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: status,
                httpVersion: "HTTP/1.1", headerFields: ["Content-Type": "application/json"])!, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            if !holdOpen { client?.urlProtocolDidFinishLoading(self) }
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    override func stopLoading() {
        onStop?()
    }
}

private let authIssuer = "https://auth.openai.com"
private let authClientID = "oaiapp_test_local"
private let authScopes = ["openid", "profile", "email", "offline_access", "resource.invoke", "chatgpt.tokens.use.direct"]

private func authJSON(_ value: Any) throws -> Data { try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]) }
private func authDiscovery() throws -> Data {
    try authJSON(["issuer": authIssuer, "authorization_endpoint": authIssuer + "/api/accounts/authorize",
        "token_endpoint": authIssuer + "/api/accounts/oauth/token", "jwks_uri": authIssuer + "/.well-known/jwks.json",
        "revocation_endpoint": authIssuer + "/api/accounts/oauth/revoke", "id_token_signing_alg_values_supported": ["RS256"]])
}
private func authFlow() -> ChatGPTAuthorizationFlow {
    let configuration = URLSessionConfiguration.ephemeral
    configuration.protocolClasses = [AuthURLProtocol.self]
    return ChatGPTAuthorizationFlow(configuration: configuration)
}
private func authBody(_ request: URLRequest) throws -> [String: String] {
    var data = request.httpBody ?? Data()
    if let stream = request.httpBodyStream, data.isEmpty {
        stream.open(); defer { stream.close() }
        var bytes = [UInt8](repeating: 0, count: 4096)
        while true {
            let count = stream.read(&bytes, maxLength: bytes.count)
            guard count >= 0 else { throw URLError(.cannotDecodeRawData) }
            if count == 0 { break }; data.append(bytes, count: count)
        }
    }
    var components = URLComponents()
    components.percentEncodedQuery = String(data: data, encoding: .utf8)
    return Dictionary(uniqueKeysWithValues: (components.queryItems ?? []).map { ($0.name, $0.value ?? "") })
}
private func authCredentials() -> ChatGPTCredentials {
    ChatGPTCredentials(subject: "test-subject", clientID: authClientID, name: "Test User", email: "test@example.invalid",
        accessToken: "old-access", refreshToken: "old-refresh", idToken: "retained-id-token",
        expiresAt: Date().addingTimeInterval(-10), scopes: authScopes, identityNonce: "old-nonce")
}
private func authReceived(_ stream: AsyncStream<Void>) async -> Bool {
    await withTaskGroup(of: Bool.self) { group in
        group.addTask { for await _ in stream { return true }; return false }
        group.addTask { try? await Task.sleep(for: .seconds(2)); return false }
        let received = await group.next() ?? false
        group.cancelAll()
        return received
    }
}

/// Test keys are generated in memory and never saved to disk or Keychain.
private final class AuthSigningFixture: @unchecked Sendable {
    let privateKey: SecKey
    let jwks: Data
    init() throws {
        let attributes: [String: Any] = [kSecAttrKeyType as String: kSecAttrKeyTypeRSA,
                                        kSecAttrKeySizeInBits as String: 2048]
        privateKey = try #require(SecKeyCreateRandomKey(attributes as CFDictionary, nil))
        let publicKey = try #require(SecKeyCopyPublicKey(privateKey))
        let encoded = try #require(SecKeyCopyExternalRepresentation(publicKey, nil)) as Data
        let values = try Self.parseRSA(encoded)
        jwks = try authJSON(["keys": [["kid": "test-key", "kty": "RSA", "alg": "RS256", "use": "sig",
                                       "n": values.0.chatGPTBase64URL, "e": values.1.chatGPTBase64URL]]])
    }
    func claims(nonce: String = "expected-nonce") -> [String: Any] {
        ["iss": authIssuer, "aud": authClientID, "sub": "test-subject", "nonce": nonce,
         "exp": Date().timeIntervalSince1970 + 3600, "iat": Date().timeIntervalSince1970 - 10,
         "name": "Test User", "email": "test@example.invalid"]
    }
    func sign(_ claims: [String: Any], header: [String: Any] = ["alg": "RS256", "kid": "test-key"]) throws -> String {
        let text = try authJSON(header).chatGPTBase64URL + "." + authJSON(claims).chatGPTBase64URL
        let signature = try #require(SecKeyCreateSignature(privateKey, .rsaSignatureMessagePKCS1v15SHA256,
                                                         Data(text.utf8) as CFData, nil)) as Data
        return text + "." + signature.chatGPTBase64URL
    }
    private static func parseRSA(_ data: Data) throws -> (Data, Data) {
        let bytes = Array(data); var offset = 0
        func length() throws -> Int {
            guard offset < bytes.count else { throw ChatGPTAuthorizationError.invalidIdentity }
            let first = Int(bytes[offset]); offset += 1
            if first < 128 { return first }
            let count = first & 0x7f
            guard count > 0, count <= 4, offset + count <= bytes.count else { throw ChatGPTAuthorizationError.invalidIdentity }
            var value = 0
            for _ in 0..<count { value = value * 256 + Int(bytes[offset]); offset += 1 }
            return value
        }
        guard bytes[offset] == 0x30 else { throw ChatGPTAuthorizationError.invalidIdentity }; offset += 1
        _ = try length()
        func integer() throws -> Data {
            guard offset < bytes.count, bytes[offset] == 0x02 else { throw ChatGPTAuthorizationError.invalidIdentity }; offset += 1
            let count = try length()
            guard offset + count <= bytes.count else { throw ChatGPTAuthorizationError.invalidIdentity }
            let value = Data(bytes[offset..<offset + count].drop(while: { $0 == 0 })); offset += count
            return value
        }
        return try (integer(), integer())
    }
}

@Suite(.serialized) struct ChatGPTAuthorizationTests {
    @Test func hostIDRequiresPersistableUUIDv4URI() async throws {
        #expect(ChatGPTAuthorizationFlow.validHostID("urn:uuid:10f2d558-1c57-4716-a910-c7e73ff71a48"))
        for value in ["10f2d558-1c57-4716-a910-c7e73ff71a48", "urn:uuid:test-host", "urn:uuid:10f2d558-1c57-1716-a910-c7e73ff71a48"] {
            #expect(!ChatGPTAuthorizationFlow.validHostID(value))
            await #expect(throws: ChatGPTAuthorizationError.invalidHost) {
                try await authFlow().start(hostID: value) { _ in Issue.record("Invalid host opened browser") }
            }
        }
    }

    @Test func authorizationUsesPKCEAndFreshState() throws {
        let transaction = try ChatGPTAuthorizationTransaction()
        let next = try ChatGPTAuthorizationTransaction()
        #expect(transaction.state != next.state && transaction.nonce != next.nonce && transaction.verifier != next.verifier)
        let url = try transaction.authorizationURL(endpoint: URL(string: authIssuer + "/api/accounts/authorize")!,
            callback: URL(string: "http://127.0.0.1:54321/auth/callback")!, hostID: "urn:uuid:10f2d558-1c57-4716-a910-c7e73ff71a48", existing: nil, requestConsent: false)
        let values = Dictionary(uniqueKeysWithValues: URLComponents(url: url, resolvingAgainstBaseURL: false)!.queryItems!.map { ($0.name, $0.value!) })
        #expect(values["client_id"] == "dynamic_agent_client")
        #expect(values["agent_name_hint"] == "MeetingRecorder")
        #expect(values["code_challenge"] == Data(SHA256.hash(data: Data(transaction.verifier.utf8))).chatGPTBase64URL)
        #expect(values["code_challenge_method"] == "S256" && values["prompt"] == nil)
        #expect(values["scope"]?.contains("chatgpt.tokens.use.direct") == true)
        #expect(values["redirect_uri"] == "http://127.0.0.1:54321/auth/callback")
        let returning = try transaction.authorizationURL(endpoint: URL(string: authIssuer + "/api/accounts/authorize")!,
            callback: URL(string: "http://127.0.0.1:12345/auth/callback")!, hostID: "urn:uuid:10f2d558-1c57-4716-a910-c7e73ff71a48", existing: authCredentials(), requestConsent: true)
        let query = Dictionary(uniqueKeysWithValues: URLComponents(url: returning, resolvingAgainstBaseURL: false)!.queryItems!.map { ($0.name, $0.value!) })
        #expect(query["client_id"] == authClientID && query["agent_name_hint"] == nil)
        #expect(query["id_token_hint"] == "retained-id-token" && query["prompt"] == "consent")
    }

    @Test func callbackRejectsStateAndClientSubstitutionBeforeExchange() throws {
        #expect(throws: ChatGPTAuthorizationError.invalidState) {
            try ChatGPTAuthorizationCallback.validate(["state": "bad", "error": "access_denied"], state: "good", existingClientID: nil)
        }
        #expect(throws: ChatGPTAuthorizationError.accessDenied) {
            try ChatGPTAuthorizationCallback.validate(["state": "good", "error": "access_denied"], state: "good", existingClientID: nil)
        }
        #expect(throws: ChatGPTAuthorizationError.invalidCallback) {
            try ChatGPTAuthorizationCallback.validate(["state": "good", "code": "code", "client_id": "dynamic_agent_client"], state: "good", existingClientID: nil)
        }
        #expect(throws: ChatGPTAuthorizationError.accountMismatch) {
            try ChatGPTAuthorizationCallback.validate(["state": "good", "code": "code", "client_id": "other-client"], state: "good", existingClientID: authClientID)
        }
        let valid = try ChatGPTAuthorizationCallback.validate(["state": "good", "code": "code"], state: "good", existingClientID: authClientID)
        #expect(valid.clientID == authClientID)
    }

    @Test func loopbackParserRejectsWrongHostPathAndDuplicateState() throws {
        let good = "GET /auth/callback?code=test&state=state&scope=openid+profile HTTP/1.1\r\nHost: 127.0.0.1:1234\r\n\r\n"
        let result = try ChatGPTAuthorizationLoopback.parseRequest(good, expectedHost: "127.0.0.1:1234")
        #expect(result["scope"] == "openid profile")
        for request in [good.replacingOccurrences(of: "Host: 127.0.0.1", with: "Host: localhost"),
                        good.replacingOccurrences(of: "/auth/callback", with: "/callback"),
                        good.replacingOccurrences(of: "state=state", with: "state=state&state=other"),
                        good.replacingOccurrences(of: "GET ", with: "POST ")] {
            #expect(throws: ChatGPTAuthorizationError.invalidCallback) {
                try ChatGPTAuthorizationLoopback.parseRequest(request, expectedHost: "127.0.0.1:1234")
            }
        }
    }

    @Test func jwtVerifiesSignatureAndEveryIdentityBinding() throws {
        let fixture = try AuthSigningFixture()
        let token = try fixture.sign(fixture.claims())
        let valid = try ChatGPTAuthorizationJWT.verify(token, jwks: fixture.jwks, issuer: authIssuer,
            clientID: authClientID, nonce: "expected-nonce", requireNonce: true)
        #expect(valid.subject == "test-subject")
        var invalidClaims: [[String: Any]] = []
        for (key, value) in [("iss", "https://example.invalid" as Any), ("aud", "other-client" as Any),
                             ("sub", "" as Any), ("nonce", "wrong-nonce" as Any),
                             ("exp", Date().timeIntervalSince1970 - 60 as Any),
                             ("iat", Date().timeIntervalSince1970 + 600 as Any),
                             ("nbf", Date().timeIntervalSince1970 + 600 as Any), ("exp", true as Any)] {
            var claims = fixture.claims(); claims[key] = value; invalidClaims.append(claims)
        }
        var missingNonce = fixture.claims(); missingNonce["nonce"] = nil; invalidClaims.append(missingNonce)
        var ambiguousAudience = fixture.claims(); ambiguousAudience["aud"] = [authClientID, "other"]; invalidClaims.append(ambiguousAudience)
        for claims in invalidClaims {
            let signed = try fixture.sign(claims)
            #expect(throws: ChatGPTAuthorizationError.invalidIdentity) {
                try ChatGPTAuthorizationJWT.verify(signed, jwks: fixture.jwks, issuer: authIssuer,
                    clientID: authClientID, nonce: "expected-nonce", requireNonce: true)
            }
        }
        let wrongSigner = try AuthSigningFixture()
        let forged = try wrongSigner.sign(fixture.claims())
        for bad in [forged, try fixture.sign(fixture.claims(), header: ["alg": "none", "kid": "test-key"]),
                    try fixture.sign(fixture.claims(), header: ["alg": "RS256", "kid": "unknown-key"])] {
            #expect(throws: ChatGPTAuthorizationError.invalidIdentity) {
                try ChatGPTAuthorizationJWT.verify(bad, jwks: fixture.jwks, issuer: authIssuer,
                    clientID: authClientID, nonce: "expected-nonce", requireNonce: true)
            }
        }
    }

    @Test func refreshRotatesTogetherAndDoesNotReuseOldRefreshToken() async throws {
        let fixture = try AuthSigningFixture(), flow = authFlow()
        var tokenRequests = 0
        AuthURLProtocol.set { request in
            switch request.url!.path {
            case "/.well-known/openid-configuration": return (200, try authDiscovery())
            case "/.well-known/jwks.json": return (200, fixture.jwks)
            case "/api/accounts/oauth/token":
                tokenRequests += 1
                let body = try authBody(request)
                #expect(body["grant_type"] == "refresh_token" && body["client_id"] == authClientID)
                #expect(body["refresh_token"] == "old-refresh" && body["scope"] == nil)
                #expect(body["resource"] == "https://api.openai.com/v1")
                return (200, try authJSON(["access_token": "new-access", "refresh_token": "new-refresh",
                    "id_token": fixture.sign(fixture.claims(nonce: "old-nonce")), "token_type": "Bearer", "expires_in": 3600]))
            default: throw URLError(.unsupportedURL)
            }
        }
        async let a = flow.refresh(credentials: authCredentials())
        async let b = flow.refresh(credentials: authCredentials())
        let (first, second) = try await (a, b)
        #expect(first == second && tokenRequests == 1)
        #expect(first.accessToken == "new-access" && first.refreshToken == "new-refresh" && first.canUseChatGPTPlan)
        #expect(first.subject == "test-subject" && first.expiresAt.timeIntervalSinceNow > 3500)
        let third = try await flow.refresh(credentials: authCredentials())
        #expect(third == first && tokenRequests == 1)
        // A failed persistence step can leave the caller with the previous record. Signing out
        // must revoke the replacement, not report success for its already-consumed predecessor.
        AuthURLProtocol.set { request in
            if request.url!.path.contains("openid-configuration") { return (200, try authDiscovery()) }
            #expect(request.url!.path.hasSuffix("/revoke"))
            #expect(try authBody(request)["token"] == "new-refresh")
            return (200, Data())
        }
        try await flow.revoke(credentials: authCredentials())
    }

    @Test func refreshRejectsIdentitySwitchAndTerminalErrors() async throws {
        let fixture = try AuthSigningFixture()
        AuthURLProtocol.set { request in
            if request.url!.path.contains("openid-configuration") { return (200, try authDiscovery()) }
            if request.url!.path.contains("jwks") { return (200, fixture.jwks) }
            var claims = fixture.claims(nonce: "old-nonce"); claims["sub"] = "another-account"
            return (200, try authJSON(["access_token": "new", "refresh_token": "rotated", "id_token": fixture.sign(claims),
                                      "token_type": "Bearer", "expires_in": 3600, "scope": authScopes.joined(separator: " ")]))
        }
        await #expect(throws: ChatGPTAuthorizationError.accountMismatch) {
            try await authFlow().refresh(credentials: authCredentials())
        }
        AuthURLProtocol.set { request in
            if request.url!.path.contains("openid-configuration") { return (200, try authDiscovery()) }
            if request.url!.path.contains("jwks") { return (200, fixture.jwks) }
            return (400, try authJSON(["error": "refresh_token_reused", "error_description": "secret response body must not appear"]))
        }
        do {
            _ = try await authFlow().refresh(credentials: authCredentials())
            Issue.record("Expected refresh failure")
        } catch let error as ChatGPTAuthorizationError {
            #expect(error.requiresSignIn)
            #expect(!error.localizedDescription.contains("secret response body"))
        }
    }

    @Test func refreshFetchesKeysBeforeRotationAndRetainsPendingReplacement() async throws {
        let oldKey = try AuthSigningFixture(), newKey = try AuthSigningFixture(), flow = authFlow()
        var tokenRequests = 0, keysAvailable = false, useNewKey = false
        AuthURLProtocol.set { request in
            if request.url!.path.contains("openid-configuration") { return (200, try authDiscovery()) }
            if request.url!.path.contains("jwks") {
                if !keysAvailable { return (503, try authJSON(["error": "temporarily_unavailable"])) }
                return (200, useNewKey ? newKey.jwks : oldKey.jwks)
            }
            tokenRequests += 1
            #expect(tokenRequests == 1)
            // Simulate signing keys changing between prefetch and the token response.
            return (200, try authJSON(["access_token": "new-access", "refresh_token": "new-refresh",
                "id_token": newKey.sign(newKey.claims(nonce: "old-nonce")), "token_type": "Bearer", "expires_in": 3600]))
        }
        await #expect(throws: ChatGPTAuthorizationError.requestFailed(status: 503, code: "temporarily_unavailable")) {
            try await flow.refresh(credentials: authCredentials())
        }
        #expect(tokenRequests == 0)
        keysAvailable = true
        await #expect(throws: ChatGPTAuthorizationError.invalidIdentity) {
            try await flow.refresh(credentials: authCredentials())
        }
        #expect(tokenRequests == 1)
        keysAvailable = false
        await #expect(throws: ChatGPTAuthorizationError.requestFailed(status: 503, code: "temporarily_unavailable")) {
            try await flow.refresh(credentials: authCredentials())
        }
        #expect(tokenRequests == 1)
        keysAvailable = true; useNewKey = true
        let recovered = try await flow.refresh(credentials: authCredentials())
        #expect(recovered.refreshToken == "new-refresh" && tokenRequests == 1)
    }

    @Test func registrationIsSavedBeforeFailedExchangeAndReusedOnRetry() async throws {
        let fixture = try AuthSigningFixture(), flow = authFlow()
        var pending: String?, tokenRequests = 0, nonce = ""
        AuthURLProtocol.set { request in
            if request.url!.path.contains("openid-configuration") { return (200, try authDiscovery()) }
            if request.url!.path.contains("jwks") { return (200, fixture.jwks) }
            #expect(pending == authClientID)
            let fields = try authBody(request)
            #expect(fields["client_id"] == pending)
            tokenRequests += 1
            if tokenRequests == 1 { return (400, try authJSON(["error": "invalid_grant"])) }
            return (200, try authJSON(["id_token": fixture.sign(fixture.claims(nonce: nonce)), "scope": "openid profile email"]))
        }
        for attempt in 0...1 {
            do {
                let result = try await flow.start(hostID: "urn:uuid:10f2d558-1c57-4716-a910-c7e73ff71a48", registeredClientID: pending,
                    onRegistered: { pending = $0 }) { authorization in
                    let values = Dictionary(uniqueKeysWithValues: URLComponents(url: authorization, resolvingAgainstBaseURL: false)!.queryItems!.map { ($0.name, $0.value!) })
                    #expect(values["client_id"] == (attempt == 0 ? "dynamic_agent_client" : authClientID))
                    if attempt == 1 { #expect(values["agent_name_hint"] == nil) }
                    nonce = values["nonce"]!
                    var callback = URLComponents(string: values["redirect_uri"]!)!
                    callback.queryItems = [URLQueryItem(name: "state", value: values["state"]),
                        URLQueryItem(name: "code", value: "code"), URLQueryItem(name: "client_id", value: authClientID)]
                    do { _ = try await URLSession.shared.data(from: callback.url!) }
                    catch { Issue.record("Local callback failed: \(error)") }
                }
                #expect(attempt == 1 && result.clientID == authClientID)
            } catch let error as ChatGPTAuthorizationError {
                #expect(attempt == 0 && error == .requestFailed(status: 400, code: "invalid_grant"))
            }
        }
        #expect(tokenRequests == 2 && pending == authClientID)
    }

    @Test func startCompletesLocalCallbackAndKeepsIdentityOnlyLoginDisabled() async throws {
        let fixture = try AuthSigningFixture(), flow = authFlow()
        var nonce = "", redirectURI = "", verifierChallenge = ""
        AuthURLProtocol.set { request in
            if request.url!.path.contains("openid-configuration") { return (200, try authDiscovery()) }
            if request.url!.path.contains("jwks") { return (200, fixture.jwks) }
            let body = try authBody(request)
            #expect(body["client_id"] == authClientID && body["code"] == "test-code")
            #expect(body["redirect_uri"] == redirectURI)
            #expect(Data(SHA256.hash(data: Data((body["code_verifier"] ?? "").utf8))).chatGPTBase64URL == verifierChallenge)
            return (200, try authJSON(["id_token": fixture.sign(fixture.claims(nonce: nonce)), "scope": "openid profile email"]))
        }
        let credentials = try await flow.start(hostID: "urn:uuid:10f2d558-1c57-4716-a910-c7e73ff71a48") { authorizationURL in
            let query = Dictionary(uniqueKeysWithValues: URLComponents(url: authorizationURL, resolvingAgainstBaseURL: false)!.queryItems!.map { ($0.name, $0.value!) })
            nonce = query["nonce"]!; redirectURI = query["redirect_uri"]!; verifierChallenge = query["code_challenge"]!
            var callback = URLComponents(string: redirectURI)!
            callback.queryItems = [URLQueryItem(name: "code", value: "test-code"), URLQueryItem(name: "state", value: query["state"]), URLQueryItem(name: "client_id", value: authClientID)]
            do {
                let (_, response) = try await URLSession.shared.data(from: callback.url!)
                #expect((response as? HTTPURLResponse)?.statusCode == 200)
            } catch { Issue.record("Loopback request failed: \(error)") }
        }
        #expect(credentials.subject == "test-subject" && credentials.clientID == authClientID)
        #expect(!credentials.canUseChatGPTPlan && credentials.accessToken.isEmpty)
    }

    @Test func loopbackIgnoresForeignStateThenAcceptsValidCallback() async throws {
        let listener = try ChatGPTAuthorizationLoopback(state: "valid-state")
        let url = try await listener.start()
        defer { listener.cancel() }
        var wrong = URLComponents(url: url, resolvingAgainstBaseURL: false)!
        wrong.queryItems = [URLQueryItem(name: "state", value: "other-state"), URLQueryItem(name: "code", value: "code")]
        let (_, denied) = try await URLSession.shared.data(from: wrong.url!)
        #expect((denied as? HTTPURLResponse)?.statusCode == 400)
        wrong.queryItems = [URLQueryItem(name: "state", value: "valid-state"), URLQueryItem(name: "code", value: "code")]
        let (_, accepted) = try await URLSession.shared.data(from: wrong.url!)
        #expect((accepted as? HTTPURLResponse)?.statusCode == 200)
        let result = try await listener.callback()
        #expect(result["code"] == "code")
    }

    @Test func cancellingCallbackClosesListenerAndResumesWaiter() async throws {
        let listener = try ChatGPTAuthorizationLoopback(state: "state")
        let url = try await listener.start()
        let waiting = Task { try await listener.callback() }
        waiting.cancel()
        await #expect(throws: CancellationError.self) { try await waiting.value }
        var request = URLRequest(url: url); request.timeoutInterval = 1
        do { _ = try await URLSession.shared.data(for: request); Issue.record("Cancelled listener remained open") }
        catch { /* Refused connection proves there is no orphan callback listener. */ }
    }

    @Test func revokeUsesDiscoveryAndRefreshTokenForm() async throws {
        var revoked = false
        AuthURLProtocol.set { request in
            if request.url!.path.contains("openid-configuration") { return (200, try authDiscovery()) }
            #expect(request.url!.path == "/api/accounts/oauth/revoke")
            let fields = try authBody(request)
            #expect(fields["token"] == "old-refresh" && fields["token_type_hint"] == "refresh_token" && fields["client_id"] == authClientID)
            revoked = true
            return (200, Data())
        }
        try await authFlow().revoke(credentials: authCredentials())
        #expect(revoked)
    }

    @Test func oversizedUnfinishedHTTPResponseCancelsUnderlyingRequest() async throws {
        let (stopped, continuation) = AsyncStream<Void>.makeStream()
        AuthURLProtocol.set(holdOpen: true, stopped: { continuation.yield(()); continuation.finish() }) { _ in
            (200, Data(repeating: 0x20, count: 1_048_577))
        }
        await #expect(throws: ChatGPTAuthorizationError.invalidTokenResponse) {
            try await authFlow().start(hostID: "urn:uuid:10f2d558-1c57-4716-a910-c7e73ff71a48") { _ in
                Issue.record("Oversized discovery opened browser")
            }
        }
        #expect(await authReceived(stopped))
    }

    @Test func cancellationInterruptsUnfinishedNetworkStream() async throws {
        let (started, continuation) = AsyncStream<Void>.makeStream()
        let (stopped, stopContinuation) = AsyncStream<Void>.makeStream()
        AuthURLProtocol.set(holdOpen: true, stopped: { stopContinuation.yield(()); stopContinuation.finish() }) { _ in
            continuation.yield(()); continuation.finish()
            return (200, Data())
        }
        let task = Task {
            try await authFlow().start(hostID: "urn:uuid:10f2d558-1c57-4716-a910-c7e73ff71a48") { _ in
                Issue.record("Unfinished discovery opened browser")
            }
        }
        for await _ in started { break }
        task.cancel()
        await #expect(throws: CancellationError.self) { try await task.value }
        #expect(await authReceived(stopped))
    }
}
