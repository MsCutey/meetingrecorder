import Foundation
import CryptoKit
import Network
import Security

/// Store this entire record atomically in the application's own protected storage.
/// The host identifier belongs to this installation and is saved separately by the caller.
public struct ChatGPTCredentials: Codable, Equatable, Sendable {
    public var issuer: String
    public var subject: String
    public var clientID: String
    public var name: String?
    public var email: String?
    public var accessToken: String
    public var refreshToken: String
    public var idToken: String
    public var expiresAt: Date
    public var earliestRefreshAt: Date?
    public var scopes: [String]
    public var identityNonce: String?

    public var canUseChatGPTPlan: Bool {
        scopes.contains("chatgpt.tokens.use.direct") && scopes.contains("resource.invoke") && !accessToken.isEmpty
    }
    public var needsRefresh: Bool { expiresAt.timeIntervalSinceNow < 60 }

    public init(issuer: String = "https://auth.openai.com", subject: String, clientID: String,
                name: String? = nil, email: String? = nil, accessToken: String, refreshToken: String,
                idToken: String, expiresAt: Date, earliestRefreshAt: Date? = nil,
                scopes: [String], identityNonce: String? = nil) {
        self.issuer = issuer; self.subject = subject; self.clientID = clientID
        self.name = name; self.email = email; self.accessToken = accessToken
        self.refreshToken = refreshToken; self.idToken = idToken; self.expiresAt = expiresAt
        self.earliestRefreshAt = earliestRefreshAt; self.scopes = scopes; self.identityNonce = identityNonce
    }
}

public enum ChatGPTAuthorizationError: LocalizedError, Equatable {
    case invalidHost, invalidDiscovery, invalidCallback, invalidState, accessDenied
    case invalidIdentity, accountMismatch, invalidTokenResponse, listenerUnavailable, timedOut, network
    case requestFailed(status: Int, code: String)

    public var requiresSignIn: Bool {
        guard case .requestFailed(_, let code) = self else { return false }
        return ["invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired",
                "refresh_token_invalidated", "refresh_token_reused"].contains(code)
    }
    public var errorDescription: String? {
        switch self {
        case .invalidHost: return "ChatGPT 登录的本机标识无效。"
        case .invalidDiscovery: return "OpenAI 登录服务配置未通过验证，请稍后再试。"
        case .invalidCallback, .invalidState: return "ChatGPT 登录回调未通过校验，请重新登录。"
        case .accessDenied: return "ChatGPT 授权已取消，当前账户未更改。"
        case .invalidIdentity: return "ChatGPT 身份签名或登录信息未通过验证，当前账户未更改。"
        case .accountMismatch: return "返回的 ChatGPT 账户与所选账户不一致，请使用“添加账户”登录。"
        case .invalidTokenResponse: return "ChatGPT 登录服务返回了不完整的凭据，请重新登录。"
        case .listenerUnavailable: return "无法启动本机登录回调，请稍后重新登录。"
        case .timedOut: return "ChatGPT 登录已超时，请重新登录。"
        case .network: return "无法连接 ChatGPT 登录服务，请检查网络后重试。"
        case .requestFailed(let status, let code):
            if requiresSignIn { return "ChatGPT 登录已失效（\(code)），请重新登录。" }
            return "ChatGPT 登录请求失败（HTTP \(status)，\(code)），请稍后重试。"
        }
    }
}

/// Official public-client SIWC flow; never reads another application's login state.
/// See https://developers.openai.com/siwc/token-sharing-open-source/sign-in
public actor ChatGPTAuthorizationFlow {
    static let issuer = "https://auth.openai.com"
    static let resource = "https://api.openai.com/v1"
    static let requestedScopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct"
    private let session: URLSession
    private let redirectGuard = ChatGPTAuthorizationRedirectGuard()
    private var refreshes: [String: Task<ChatGPTCredentials, Error>] = [:]
    private var pendingRefreshes: [String: ChatGPTAuthorizationTokens] = [:]
    private var lastRefresh: (key: String, value: ChatGPTCredentials)?

    public init(configuration: URLSessionConfiguration = .ephemeral) {
        let configuration = configuration.copy() as! URLSessionConfiguration
        configuration.httpCookieStorage = nil; configuration.httpShouldSetCookies = false
        configuration.urlCredentialStorage = nil
        configuration.urlCache = nil; configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 30; configuration.timeoutIntervalForResource = 60
        session = URLSession(configuration: configuration)
    }
    deinit { session.invalidateAndCancel() }

    public func start(hostID: String, existing: ChatGPTCredentials? = nil, registeredClientID: String? = nil,
                      requestConsent: Bool = false,
                      onRegistered: @escaping (String) async throws -> Void = { _ in },
                      openURL: @escaping (URL) async -> Void) async throws -> ChatGPTCredentials {
        try Task.checkCancellation()
        guard Self.validHostID(hostID) else {
            throw ChatGPTAuthorizationError.invalidHost
        }
        if let existing { try Self.validateRegistration(existing) }
        if let registeredClientID {
            guard ChatGPTAuthorizationCallback.isIssuedClientID(registeredClientID),
                  existing == nil || existing?.clientID == registeredClientID else {
                throw ChatGPTAuthorizationError.accountMismatch
            }
        }
        let discovery = try await discover()
        let transaction = try ChatGPTAuthorizationTransaction()
        let listener = try ChatGPTAuthorizationLoopback(state: transaction.state)
        return try await withTaskCancellationHandler {
            let callbackURL = try await listener.start()
            defer { listener.cancel() }
            let authorizationURL = try transaction.authorizationURL(endpoint: discovery.authorizationEndpoint,
                callback: callbackURL, hostID: hostID, existing: existing,
                registeredClientID: registeredClientID, requestConsent: requestConsent)
            await openURL(authorizationURL)
            let query = try await listener.callback()
            try Task.checkCancellation()
            let callback = try ChatGPTAuthorizationCallback.validate(query, state: transaction.state,
                existingClientID: existing?.clientID ?? registeredClientID)
            // Registration survives an expired authorization code or a failed exchange. This callback
            // saves only the client ID; it must never mark an unverified account as signed in.
            try await onRegistered(callback.clientID)
            try Task.checkCancellation()
            let response = try await tokenRequest(endpoint: discovery.tokenEndpoint, fields: [
                "grant_type": "authorization_code", "client_id": callback.clientID, "code": callback.code,
                "code_verifier": transaction.verifier, "redirect_uri": callbackURL.absoluteString,
                "resource": Self.resource
            ])
            guard let idToken = response.idToken else { throw ChatGPTAuthorizationError.invalidTokenResponse }
            let identity = try await verify(idToken, discovery: discovery, clientID: callback.clientID,
                nonce: transaction.nonce, requireNonce: true)
            if let existing, identity.subject != existing.subject || identity.issuer != existing.issuer {
                throw ChatGPTAuthorizationError.accountMismatch
            }
            return try response.credentials(identity: identity, clientID: callback.clientID,
                previous: nil, nonce: transaction.nonce)
        } onCancel: { listener.cancel() }
    }

    /// Shares an in-flight refresh and its immediately previous result so rotating tokens aren't reused.
    /// Keep one flow instance per application and persist the returned record before another request.
    public func refresh(credentials: ChatGPTCredentials) async throws -> ChatGPTCredentials {
        try Task.checkCancellation()
        try Self.validateRegistration(credentials)
        guard !credentials.refreshToken.isEmpty else {
            throw ChatGPTAuthorizationError.requestFailed(status: 401, code: "invalid_refresh_token")
        }
        let key = Self.refreshKey(credentials)
        if let cached = lastRefresh, cached.key == key { return cached.value }
        if let running = refreshes[key] { return try await running.value }
        let task = Task { try await self.performRefresh(credentials, key: key) }
        refreshes[key] = task
        defer { refreshes[key] = nil }
        let result = try await task.value
        lastRefresh = (key, result)
        return result
    }

    /// A failure must be reported if the caller still chooses to sign out locally.
    public func revoke(credentials: ChatGPTCredentials) async throws {
        try Self.validateRegistration(credentials)
        let key = Self.refreshKey(credentials)
        // The caller may still hold the old record if its Keychain write failed after rotation.
        let completedReplacement = lastRefresh?.key == key ? lastRefresh?.value.refreshToken : nil
        let token = pendingRefreshes[key]?.refreshToken ?? completedReplacement ?? credentials.refreshToken
        guard !token.isEmpty else { return }
        let discovery = try await discover()
        guard let endpoint = discovery.revocationEndpoint else { throw ChatGPTAuthorizationError.invalidDiscovery }
        _ = try await request(endpoint, fields: ["token": token,
            "token_type_hint": "refresh_token", "client_id": credentials.clientID])
        lastRefresh = nil; pendingRefreshes[key] = nil
    }

    private func performRefresh(_ previous: ChatGPTCredentials, key: String) async throws -> ChatGPTCredentials {
        let discovery = try await discover()
        // Resolve trusted signing keys before rotating a refresh token. A JWKS outage must not
        // consume the old token and strand its replacement behind another network request.
        let jwks = try await request(discovery.jwksURI)
        let response: ChatGPTAuthorizationTokens
        if let pending = pendingRefreshes[key] { response = pending }
        else {
            response = try await tokenRequest(endpoint: discovery.tokenEndpoint, fields: [
                "grant_type": "refresh_token", "client_id": previous.clientID,
                "refresh_token": previous.refreshToken, "resource": Self.resource
            ])
            // If validation fails (including a key rotation race), a later attempt revalidates
            // this response with fresh keys instead of submitting an already-used refresh token.
            pendingRefreshes[key] = response
        }
        var identity = ChatGPTAuthorizationIdentity(issuer: previous.issuer, subject: previous.subject,
                                                    name: previous.name, email: previous.email)
        if let token = response.idToken {
            identity = try ChatGPTAuthorizationJWT.verify(token, jwks: jwks, issuer: discovery.issuer,
                clientID: previous.clientID, nonce: previous.identityNonce, requireNonce: false)
            guard identity.issuer == previous.issuer, identity.subject == previous.subject else {
                throw ChatGPTAuthorizationError.accountMismatch
            }
        }
        guard let replacement = response.refreshToken, !replacement.isEmpty else {
            throw ChatGPTAuthorizationError.invalidTokenResponse
        }
        let result = try response.credentials(identity: identity, clientID: previous.clientID,
                                             previous: previous, nonce: previous.identityNonce)
        pendingRefreshes[key] = nil
        return result
    }

    static func validHostID(_ value: String) -> Bool {
        guard value.hasPrefix("urn:uuid:"), value.count == 45,
              let uuid = UUID(uuidString: String(value.dropFirst(9))) else { return false }
        let bytes = uuid.uuid
        return bytes.6 >> 4 == 4 && bytes.8 & 0xc0 == 0x80
    }

    private static func refreshKey(_ credentials: ChatGPTCredentials) -> String {
        Data(SHA256.hash(data: Data((credentials.clientID + "\n" + credentials.refreshToken).utf8))).base64EncodedString()
    }

    private static func validateRegistration(_ credentials: ChatGPTCredentials) throws {
        guard credentials.issuer == issuer, !credentials.subject.isEmpty,
              ChatGPTAuthorizationCallback.isIssuedClientID(credentials.clientID) else {
            throw ChatGPTAuthorizationError.accountMismatch
        }
    }

    private func discover() async throws -> ChatGPTAuthorizationDiscovery {
        let data = try await request(URL(string: Self.issuer + "/.well-known/openid-configuration")!)
        guard let value = try? JSONDecoder().decode(ChatGPTAuthorizationDiscovery.self, from: data),
              value.issuer == Self.issuer, value.algorithms.contains("RS256"),
              [value.authorizationEndpoint, value.tokenEndpoint, value.jwksURI].allSatisfy(Self.trustedEndpoint),
              value.revocationEndpoint.map(Self.trustedEndpoint) ?? true else {
            throw ChatGPTAuthorizationError.invalidDiscovery
        }
        return value
    }

    private static func trustedEndpoint(_ url: URL) -> Bool {
        url.scheme == "https" && url.host == "auth.openai.com" && (url.port == nil || url.port == 443) &&
            url.user == nil && url.password == nil && url.query == nil && url.fragment == nil
    }

    private func verify(_ token: String, discovery: ChatGPTAuthorizationDiscovery, clientID: String,
                        nonce: String?, requireNonce: Bool) async throws -> ChatGPTAuthorizationIdentity {
        // Fetch per validation: a rotated kid is never pinned to a stale, indefinite cache.
        let jwks = try await request(discovery.jwksURI)
        return try ChatGPTAuthorizationJWT.verify(token, jwks: jwks, issuer: discovery.issuer,
            clientID: clientID, nonce: nonce, requireNonce: requireNonce)
    }

    private func tokenRequest(endpoint: URL, fields: [String: String]) async throws -> ChatGPTAuthorizationTokens {
        let data = try await request(endpoint, fields: fields)
        guard let response = try? JSONDecoder().decode(ChatGPTAuthorizationTokens.self, from: data) else {
            throw ChatGPTAuthorizationError.invalidTokenResponse
        }
        return response
    }

    private func request(_ url: URL, fields: [String: String]? = nil) async throws -> Data {
        try Task.checkCancellation()
        guard Self.trustedEndpoint(url) else { throw ChatGPTAuthorizationError.invalidDiscovery }
        var request = URLRequest(url: url)
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let fields {
            request.httpMethod = "POST"
            request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
            request.httpBody = Data(Self.formEncode(fields).utf8)
        }
        do {
            let (bytes, response) = try await session.bytes(for: request, delegate: redirectGuard)
            defer { bytes.task.cancel() }
            guard let response = response as? HTTPURLResponse, response.url == url else {
                throw ChatGPTAuthorizationError.invalidTokenResponse
            }
            let data = try await withTaskCancellationHandler {
                try Task.checkCancellation()
                var data = Data()
                for try await byte in bytes {
                    guard data.count < 1_048_576 else { throw ChatGPTAuthorizationError.invalidTokenResponse }
                    data.append(byte)
                }
                return data
            } onCancel: { bytes.task.cancel() }
            guard response.statusCode == 200 else {
                let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
                let rawCode = (json?["error"] as? String) ?? (json?["error"] as? [String: Any])?["code"] as? String
                let code = rawCode.flatMap { value in
                    value.count <= 80 && value.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_") } ? value : nil
                } ?? "oauth_request_failed"
                throw ChatGPTAuthorizationError.requestFailed(status: response.statusCode, code: code)
            }
            return data
        } catch is CancellationError { throw CancellationError() }
        catch let error as ChatGPTAuthorizationError { throw error }
        catch {
            if Task.isCancelled || (error as? URLError)?.code == .cancelled { throw CancellationError() }
            throw ChatGPTAuthorizationError.network
        }
    }

    static func formEncode(_ fields: [String: String]) -> String {
        let allowed = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
        return fields.sorted { $0.key < $1.key }.map {
            $0.key.addingPercentEncoding(withAllowedCharacters: allowed)! + "=" +
            $0.value.addingPercentEncoding(withAllowedCharacters: allowed)!
        }.joined(separator: "&")
    }
}

private final class ChatGPTAuthorizationRedirectGuard: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

struct ChatGPTAuthorizationDiscovery: Decodable {
    let issuer: String
    let authorizationEndpoint: URL
    let tokenEndpoint: URL
    let jwksURI: URL
    let revocationEndpoint: URL?
    let algorithms: [String]
    enum CodingKeys: String, CodingKey {
        case issuer, authorizationEndpoint = "authorization_endpoint", tokenEndpoint = "token_endpoint"
        case jwksURI = "jwks_uri", revocationEndpoint = "revocation_endpoint"
        case algorithms = "id_token_signing_alg_values_supported"
    }
}

struct ChatGPTAuthorizationTransaction {
    let state: String
    let nonce: String
    let verifier: String
    init() throws {
        state = try Self.random(); nonce = try Self.random(); verifier = try Self.random(count: 64)
    }
    private static func random(count: Int = 32) throws -> String {
        var bytes = [UInt8](repeating: 0, count: count)
        guard SecRandomCopyBytes(kSecRandomDefault, count, &bytes) == errSecSuccess else {
            throw ChatGPTAuthorizationError.invalidState
        }
        return Data(bytes).chatGPTBase64URL
    }
    func authorizationURL(endpoint: URL, callback: URL, hostID: String,
                          existing: ChatGPTCredentials?, registeredClientID: String? = nil,
                          requestConsent: Bool) throws -> URL {
        var parameters = ["client_id": existing?.clientID ?? registeredClientID ?? "dynamic_agent_client", "response_type": "code",
            "redirect_uri": callback.absoluteString, "scope": ChatGPTAuthorizationFlow.requestedScopes,
            "resource": ChatGPTAuthorizationFlow.resource, "state": state, "nonce": nonce,
            "ext_agent_host_id": hostID, "code_challenge_method": "S256",
            "code_challenge": Data(SHA256.hash(data: Data(verifier.utf8))).chatGPTBase64URL]
        if let existing {
            if !existing.idToken.isEmpty { parameters["id_token_hint"] = existing.idToken }
            if let email = existing.email, !email.isEmpty { parameters["login_hint"] = email }
        } else if registeredClientID == nil { parameters["agent_name_hint"] = "MeetingRecorder" }
        if requestConsent { parameters["prompt"] = "consent" }
        var components = URLComponents(url: endpoint, resolvingAgainstBaseURL: false)!
        components.percentEncodedQuery = ChatGPTAuthorizationFlow.formEncode(parameters)
        guard let url = components.url else { throw ChatGPTAuthorizationError.invalidCallback }
        return url
    }
}

struct ChatGPTAuthorizationCallback {
    let code: String
    let clientID: String
    static func isIssuedClientID(_ value: String) -> Bool {
        !value.isEmpty && value != "dynamic_agent_client" && value.count <= 256 &&
            value.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_" || $0 == "-") }
    }
    static func validate(_ query: [String: String], state: String, existingClientID: String?) throws -> Self {
        guard let returnedState = query["state"], secureEqual(returnedState, state) else {
            throw ChatGPTAuthorizationError.invalidState
        }
        if query["error"] != nil { throw ChatGPTAuthorizationError.accessDenied }
        let clientID: String
        if let existingClientID {
            guard query["client_id"] == nil || query["client_id"] == existingClientID else {
                throw ChatGPTAuthorizationError.accountMismatch
            }
            clientID = existingClientID
        } else {
            guard let issued = query["client_id"], isIssuedClientID(issued) else {
                throw ChatGPTAuthorizationError.invalidCallback
            }
            clientID = issued
        }
        guard let code = query["code"], !code.isEmpty, code.utf8.count <= 8192 else {
            throw ChatGPTAuthorizationError.invalidCallback
        }
        return Self(code: code, clientID: clientID)
    }
    static func secureEqual(_ lhs: String, _ rhs: String) -> Bool {
        let a = Array(lhs.utf8), b = Array(rhs.utf8)
        guard a.count == b.count else { return false }
        return zip(a, b).reduce(UInt8(0)) { $0 | ($1.0 ^ $1.1) } == 0
    }
}

struct ChatGPTAuthorizationIdentity {
    let issuer: String
    let subject: String
    let name: String?
    let email: String?
}

private struct ChatGPTAuthorizationTokens: Decodable {
    let accessToken: String?
    let refreshToken: String?
    let idToken: String?
    let tokenType: String?
    let expiresIn: Double?
    let scope: String?
    let earliestRefreshAt: Double?
    enum CodingKeys: String, CodingKey {
        case accessToken = "access_token", refreshToken = "refresh_token", idToken = "id_token"
        case tokenType = "token_type", expiresIn = "expires_in", scope, earliestRefreshAt = "earliest_refresh_at"
    }
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        accessToken = try c.decodeIfPresent(String.self, forKey: .accessToken)
        refreshToken = try c.decodeIfPresent(String.self, forKey: .refreshToken)
        idToken = try c.decodeIfPresent(String.self, forKey: .idToken)
        tokenType = try c.decodeIfPresent(String.self, forKey: .tokenType)
        expiresIn = try c.decodeIfPresent(Double.self, forKey: .expiresIn)
        scope = try c.decodeIfPresent(String.self, forKey: .scope)
        earliestRefreshAt = (try? c.decode(Double.self, forKey: .earliestRefreshAt)) ??
            (try? c.decode(String.self, forKey: .earliestRefreshAt)).flatMap { Double($0) ?? ISO8601DateFormatter().date(from: $0)?.timeIntervalSince1970 }
    }
    func credentials(identity: ChatGPTAuthorizationIdentity, clientID: String,
                     previous: ChatGPTCredentials?, nonce: String?) throws -> ChatGPTCredentials {
        let scopes = scope.map { Array(Set($0.split(whereSeparator: \.isWhitespace).map(String.init))).sorted() } ?? previous?.scopes ?? []
        let permitsPlan = scopes.contains("chatgpt.tokens.use.direct")
        if permitsPlan {
            guard let accessToken, !accessToken.isEmpty, tokenType?.lowercased() == "bearer",
                  let expiresIn, expiresIn.isFinite, expiresIn > 0, expiresIn <= 604_800 else {
                throw ChatGPTAuthorizationError.invalidTokenResponse
            }
        }
        if scopes.contains("offline_access"), refreshToken?.isEmpty != false {
            throw ChatGPTAuthorizationError.invalidTokenResponse
        }
        return ChatGPTCredentials(issuer: identity.issuer, subject: identity.subject, clientID: clientID,
            name: identity.name, email: identity.email, accessToken: accessToken ?? "", refreshToken: refreshToken ?? "",
            idToken: idToken ?? previous?.idToken ?? "", expiresAt: Date().addingTimeInterval(expiresIn ?? 0),
            earliestRefreshAt: earliestRefreshAt.map { Date(timeIntervalSince1970: $0) }, scopes: scopes, identityNonce: nonce)
    }
}

enum ChatGPTAuthorizationJWT {
    /// Only RS256 is allowed, as advertised by OpenAI's production OIDC discovery.
    static func verify(_ token: String, jwks: Data, issuer: String, clientID: String, nonce: String?,
                       requireNonce: Bool, now: Date = Date()) throws -> ChatGPTAuthorizationIdentity {
        guard token.utf8.count <= 65_536 else { throw ChatGPTAuthorizationError.invalidIdentity }
        let parts = token.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3, let headerData = Data(chatGPTBase64URL: String(parts[0])),
              let payloadData = Data(chatGPTBase64URL: String(parts[1])),
              let signature = Data(chatGPTBase64URL: String(parts[2])),
              let header = try? JSONSerialization.jsonObject(with: headerData) as? [String: Any],
              header["alg"] as? String == "RS256", header["crit"] == nil,
              let kid = header["kid"] as? String, !kid.isEmpty,
              let keySet = try? JSONSerialization.jsonObject(with: jwks) as? [String: Any],
              let keys = keySet["keys"] as? [[String: Any]] else {
            throw ChatGPTAuthorizationError.invalidIdentity
        }
        let matches = keys.filter { ($0["kid"] as? String) == kid && ($0["kty"] as? String) == "RSA" }
        guard matches.count == 1, let key = matches.first,
              key["alg"] == nil || key["alg"] as? String == "RS256",
              key["use"] == nil || key["use"] as? String == "sig",
              key["key_ops"] == nil || (key["key_ops"] as? [String])?.contains("verify") == true,
              let n = key["n"] as? String, let e = key["e"] as? String,
              let modulus = Data(chatGPTBase64URL: n), let exponent = Data(chatGPTBase64URL: e),
              modulus.count >= 256, modulus.count <= 1024, !exponent.isEmpty, exponent.count <= 8 else {
            throw ChatGPTAuthorizationError.invalidIdentity
        }
        let encodedKey = der(0x30, derInteger(modulus) + derInteger(exponent))
        let attributes = [kSecAttrKeyType: kSecAttrKeyTypeRSA, kSecAttrKeyClass: kSecAttrKeyClassPublic] as CFDictionary
        guard let publicKey = SecKeyCreateWithData(encodedKey as CFData, attributes, nil),
              SecKeyVerifySignature(publicKey, .rsaSignatureMessagePKCS1v15SHA256,
                Data((parts[0] + "." + parts[1]).utf8) as CFData, signature as CFData, nil),
              let claims = try? JSONSerialization.jsonObject(with: payloadData) as? [String: Any] else {
            throw ChatGPTAuthorizationError.invalidIdentity
        }
        let audiences = (claims["aud"] as? [String]) ?? (claims["aud"] as? String).map { [$0] } ?? []
        let now = now.timeIntervalSince1970
        guard claims["iss"] as? String == issuer, audiences.contains(clientID),
              (audiences.count <= 1 || claims["azp"] as? String == clientID),
              (claims["azp"] == nil || claims["azp"] as? String == clientID),
              let subject = claims["sub"] as? String, !subject.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              let expires = number(claims["exp"]), expires > now - 5,
              let issued = number(claims["iat"]), issued <= now + 5, issued < expires,
              claims["nbf"] == nil || number(claims["nbf"]).map({ $0 <= now + 5 }) == true else {
            throw ChatGPTAuthorizationError.invalidIdentity
        }
        if requireNonce || claims["nonce"] != nil {
            guard let nonce, let returned = claims["nonce"] as? String,
                  ChatGPTAuthorizationCallback.secureEqual(nonce, returned) else {
                throw ChatGPTAuthorizationError.invalidIdentity
            }
        }
        return ChatGPTAuthorizationIdentity(issuer: issuer, subject: subject,
                                            name: claims["name"] as? String, email: claims["email"] as? String)
    }
    private static func number(_ value: Any?) -> Double? {
        guard let value = value as? NSNumber, CFGetTypeID(value) != CFBooleanGetTypeID(), value.doubleValue.isFinite else { return nil }
        return value.doubleValue
    }
    private static func derInteger(_ value: Data) -> Data {
        var bytes = Data(value.drop(while: { $0 == 0 }))
        if bytes.isEmpty { bytes.append(0) }
        if bytes.first! & 0x80 != 0 { bytes.insert(0, at: 0) }
        return der(0x02, bytes)
    }
    private static func der(_ tag: UInt8, _ bytes: Data) -> Data {
        var result = Data([tag])
        if bytes.count < 128 { result.append(UInt8(bytes.count)) }
        else {
            var count = bytes.count, length: [UInt8] = []
            while count > 0 { length.insert(UInt8(count & 0xff), at: 0); count >>= 8 }
            result.append(0x80 | UInt8(length.count)); result.append(contentsOf: length)
        }
        result.append(bytes); return result
    }
}

extension Data {
    var chatGPTBase64URL: String {
        base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
    init?(chatGPTBase64URL value: String) {
        guard !value.isEmpty, value.count % 4 != 1,
              value.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }) else { return nil }
        let standard = value.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        self.init(base64Encoded: standard + String(repeating: "=", count: (4 - standard.count % 4) % 4))
    }
}

/// A single-use, bounded HTTP listener bound only to IPv4 loopback, never all interfaces.
final class ChatGPTAuthorizationLoopback: @unchecked Sendable {
    private let listener: NWListener
    private let queue = DispatchQueue(label: "MeetingRecorder.ChatGPTCallback")
    private let state: String
    private var ready: CheckedContinuation<URL, Error>?
    private var waiting: CheckedContinuation<[String: String], Error>?
    private var result: Result<[String: String], Error>?
    private var connections: [UUID: NWConnection] = [:]
    private var timeout: DispatchWorkItem?
    private var callbackURL: URL?

    init(state: String) throws {
        self.state = state
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        do { listener = try NWListener(using: parameters) }
        catch { throw ChatGPTAuthorizationError.listenerUnavailable }
    }

    func start() async throws -> URL {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                queue.async { [self] in
                    if let result = self.result { continuation.resume(throwing: result.failure ?? CancellationError()); return }
                    self.ready = continuation
                    self.listener.stateUpdateHandler = { [weak self] state in
                        guard let self else { return }
                        switch state {
                        case .ready:
                            guard let port = self.listener.port,
                                  let url = URL(string: "http://127.0.0.1:\(port.rawValue)/auth/callback") else {
                                self.finish(.failure(ChatGPTAuthorizationError.listenerUnavailable)); return
                            }
                            self.callbackURL = url
                            self.ready?.resume(returning: url); self.ready = nil
                        case .failed: self.finish(.failure(ChatGPTAuthorizationError.listenerUnavailable))
                        default: break
                        }
                    }
                    self.listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
                    let timeout = DispatchWorkItem { [weak self] in self?.finish(.failure(ChatGPTAuthorizationError.timedOut)) }
                    self.timeout = timeout; self.queue.asyncAfter(deadline: .now() + 600, execute: timeout)
                    self.listener.start(queue: self.queue)
                }
            }
        } onCancel: { self.cancel() }
    }

    func callback() async throws -> [String: String] {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                queue.async {
                    if let result = self.result { continuation.resume(with: result) }
                    else { self.waiting = continuation }
                }
            }
        } onCancel: { self.cancel() }
    }

    func cancel() { queue.async { self.finish(.failure(CancellationError())) } }

    private func finish(_ result: Result<[String: String], Error>) {
        guard self.result == nil else { return }
        self.result = result; timeout?.cancel(); timeout = nil
        ready?.resume(throwing: result.failure ?? ChatGPTAuthorizationError.invalidCallback); ready = nil
        waiting?.resume(with: result); waiting = nil
        listener.cancel(); listener.stateUpdateHandler = nil; listener.newConnectionHandler = nil
        for connection in connections.values { connection.cancel() }
        connections.removeAll()
    }

    private func accept(_ connection: NWConnection) {
        guard result == nil, connections.count < 8 else { connection.cancel(); return }
        let id = UUID(); connections[id] = connection
        connection.start(queue: queue)
        queue.asyncAfter(deadline: .now() + 10) { [weak self, weak connection] in
            connection?.cancel(); self?.connections[id] = nil
        }
        read(connection, id: id, accumulated: Data())
    }

    private func read(_ connection: NWConnection, id: UUID, accumulated: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { [weak self] data, _, complete, error in
            guard let self, self.result == nil else { connection.cancel(); return }
            var bytes = accumulated; bytes.append(data ?? Data())
            guard bytes.count <= 16_384, error == nil else { self.close(connection, id: id); return }
            if bytes.range(of: Data("\r\n\r\n".utf8)) != nil { self.respond(connection, id: id, bytes: bytes) }
            else if complete { self.close(connection, id: id) }
            else { self.read(connection, id: id, accumulated: bytes) }
        }
    }

    private func close(_ connection: NWConnection, id: UUID) { connection.cancel(); connections[id] = nil }

    private func respond(_ connection: NWConnection, id: UUID, bytes: Data) {
        var query: [String: String]?
        if let callbackURL,
           let text = String(data: bytes, encoding: .utf8),
           let parsed = try? Self.parseRequest(text, expectedHost: "127.0.0.1:\(callbackURL.port!)"),
           let returnedState = parsed["state"], ChatGPTAuthorizationCallback.secureEqual(returnedState, state) { query = parsed }
        let valid = query != nil
        let body = valid ? "Authorization received. Return to MeetingRecorder to complete sign-in. You can close this tab." : "Invalid authorization callback. Return to MeetingRecorder and try again."
        let response = "HTTP/1.1 \(valid ? "200 OK" : "400 Bad Request")\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: \(body.utf8.count)\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n\(body)"
        connection.send(content: Data(response.utf8), completion: .contentProcessed { [weak self] _ in
            guard let self else { connection.cancel(); return }
            self.close(connection, id: id)
            if let query { self.finish(.success(query)) }
        })
    }

    static func parseRequest(_ request: String, expectedHost: String) throws -> [String: String] {
        let lines = request.components(separatedBy: "\r\n")
        guard let first = lines.first else { throw ChatGPTAuthorizationError.invalidCallback }
        let fields = first.split(separator: " ", omittingEmptySubsequences: false)
        guard fields.count == 3, fields[0] == "GET", fields[2] == "HTTP/1.1" || fields[2] == "HTTP/1.0",
              fields[1].hasPrefix("/auth/callback?"),
              let components = URLComponents(string: "http://" + expectedHost + String(fields[1])),
              components.path == "/auth/callback", components.fragment == nil else {
            throw ChatGPTAuthorizationError.invalidCallback
        }
        let hosts = lines.dropFirst().filter { $0.lowercased().hasPrefix("host:") }
        guard hosts.count == 1, hosts[0].dropFirst(5).trimmingCharacters(in: .whitespaces) == expectedHost else {
            throw ChatGPTAuthorizationError.invalidCallback
        }
        var query: [String: String] = [:]
        // Query parameters use application/x-www-form-urlencoded '+' semantics.
        var decoded = components
        decoded.percentEncodedQuery = components.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%20")
        for item in decoded.queryItems ?? [] {
            guard query[item.name] == nil, let value = item.value else { throw ChatGPTAuthorizationError.invalidCallback }
            query[item.name] = value
        }
        return query
    }
}

private extension Result {
    var failure: Failure? { if case .failure(let error) = self { return error }; return nil }
}
