import AppKit
import Security
import SwiftUI
import RecorderSupport

enum ChatGPTConnectionStore {
    private static let service = "com.mscutey.meetingrecorder.chatgpt"
    private static let activeAccountKey = "chatGPT.activeAccount.v1"
    private static func query(account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }
    static func read(defaults: UserDefaults) throws -> ChatGPTCredentials? {
        guard let account = defaults.string(forKey: activeAccountKey) else { return nil }
        var query = query(account: account)
        query[kSecReturnData as String] = true; query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else {
            throw MeetingNotesError("无法读取本软件的 ChatGPT 登录凭据（\(status)）。")
        }
        return try JSONDecoder().decode(ChatGPTCredentials.self, from: data)
    }
    static func save(_ credentials: ChatGPTCredentials, defaults: UserDefaults) throws {
        let account = credentials.clientID + ":" + credentials.subject
        var query = query(account: account)
        let attributes = [kSecValueData as String: try JSONEncoder().encode(credentials)]
        var status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            query.merge(attributes) { _, new in new }
            query[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
            status = SecItemAdd(query as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw MeetingNotesError("无法将 ChatGPT 登录保存到钥匙串（\(status)）。") }
        defaults.set(account, forKey: activeAccountKey)
    }
    static func delete(_ credentials: ChatGPTCredentials, defaults: UserDefaults) throws {
        let status = SecItemDelete(query(account: credentials.clientID + ":" + credentials.subject) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw MeetingNotesError("无法清除本软件的 ChatGPT 登录凭据（\(status)）。")
        }
        defaults.removeObject(forKey: activeAccountKey)
    }
}

extension ChatGPTCredentials {
    /// Sign-out clears tokens but retains the verified account-to-client registration.
    var signedOutRegistration: ChatGPTCredentials {
        var registration = self
        registration.accessToken = ""; registration.refreshToken = ""; registration.idToken = ""
        registration.expiresAt = .distantPast; registration.earliestRefreshAt = nil
        registration.scopes = []; registration.identityNonce = nil
        return registration
    }
}

@MainActor final class ChatGPTConnectionModel: ObservableObject {
    @Published private(set) var identity = "尚未登录 ChatGPT"
    @Published private(set) var canUsePlan = false
    @Published private(set) var isConnected = false
    @Published private(set) var hasRegistration = false
    @Published private(set) var isWorking = false
    @Published private(set) var models: [ChatGPTModel] = []
    @Published var selectedModel: String {
        didSet { defaults.set(selectedModel, forKey: "chatGPT.summaryModel.v1") }
    }
    @Published var message = "使用官方登录授权本软件调用套餐额度；目前仅支持文字总结，音频转写仍需独立 API。"
    private let defaults: UserDefaults
    private let authorization = ChatGPTAuthorizationFlow()
    private let client = ChatGPTSummaryClient()
    private var credentials: ChatGPTCredentials?
    private var loaded = false
    private var task: Task<Void, Never>?

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        selectedModel = defaults.string(forKey: "chatGPT.summaryModel.v1") ?? ""
    }

    func restoreIfNeeded() {
        guard !loaded else { return }
        loaded = true
        do {
            if let saved = try ChatGPTConnectionStore.read(defaults: defaults) {
                apply(saved)
                message = saved.accessToken.isEmpty && saved.idToken.isEmpty ? "已保留账号注册信息，请重新登录。" :
                    (saved.canUseChatGPTPlan ? "已恢复本软件的登录，可刷新模型列表。" : "已登录，但尚未授权本软件使用套餐额度。")
            }
        } catch { message = error.localizedDescription }
    }

    func connect(requestConsent: Bool = false, useDifferentAccount: Bool = false) {
        guard !isWorking else { return }
        restoreIfNeeded()
        if useDifferentAccount { defaults.removeObject(forKey: "chatGPT.pendingClientID.v1") }
        let savedHostID = defaults.string(forKey: "chatGPT.hostID.v1")
        let hostID: String
        if let savedHostID, savedHostID.hasPrefix("urn:uuid:"), UUID(uuidString: String(savedHostID.dropFirst(9))) != nil {
            hostID = savedHostID
        } else if let savedHostID, UUID(uuidString: savedHostID) != nil {
            hostID = "urn:uuid:" + savedHostID
        } else { hostID = "urn:uuid:" + UUID().uuidString }
        defaults.set(hostID, forKey: "chatGPT.hostID.v1")
        isWorking = true
        message = "正在打开官方登录页面，请在浏览器中完成登录与授权…"
        task = Task {
            defer { isWorking = false; task = nil }
            do {
                let registration = useDifferentAccount ? nil : credentials
                let pendingClientID = registration == nil ? defaults.string(forKey: "chatGPT.pendingClientID.v1") : nil
                let signedIn = try await authorization.start(hostID: hostID, existing: registration,
                    registeredClientID: pendingClientID, requestConsent: requestConsent,
                    onRegistered: { [defaults] clientID in
                        if registration == nil { defaults.set(clientID, forKey: "chatGPT.pendingClientID.v1") }
                    }, openURL: { url in
                        await MainActor.run { _ = NSWorkspace.shared.open(url) }
                    })
                try Task.checkCancellation()
                try ChatGPTConnectionStore.save(signedIn, defaults: defaults)
                defaults.removeObject(forKey: "chatGPT.pendingClientID.v1")
                apply(signedIn)
                if signedIn.canUseChatGPTPlan {
                    try await updateModels(using: signedIn)
                } else { message = "登录成功，但尚未授权使用订阅。可点击“启用套餐额度”再次授权。" }
            } catch {
                message = Task.isCancelled ? "已取消登录，原有连接保留。" : error.localizedDescription
            }
        }
    }

    func refreshModels() {
        guard !isWorking else { return }
        isWorking = true
        task = Task {
            defer { isWorking = false; task = nil }
            do { try await updateModels(using: freshCredentials()) }
            catch { message = Task.isCancelled ? "已取消刷新。" : error.localizedDescription }
        }
    }

    private func updateModels(using credentials: ChatGPTCredentials) async throws {
        let result = try await client.availableModels(accessToken: credentials.accessToken)
        try Task.checkCancellation()
        models = result
        if !models.contains(where: { $0.slug == selectedModel }) { selectedModel = models.first?.slug ?? "" }
        message = models.isEmpty ? "当前账号没有向本软件开放可用模型，请检查套餐和工作区权限。" : "已连接，可使用套餐额度生成纪要。"
    }

    private func apply(_ value: ChatGPTCredentials) {
        credentials = value; hasRegistration = true
        isConnected = !value.accessToken.isEmpty || !value.idToken.isEmpty; canUsePlan = value.canUseChatGPTPlan
        let label = value.email ?? value.name ?? "ChatGPT 账号"
        identity = isConnected ? label : "已退出：" + label
    }

    private func freshCredentials() async throws -> ChatGPTCredentials {
        restoreIfNeeded()
        guard var value = credentials else { throw MeetingNotesError("请先使用 ChatGPT 登录。") }
        guard !value.accessToken.isEmpty || !value.idToken.isEmpty else { throw MeetingNotesError("请重新登录 ChatGPT。") }
        guard value.canUseChatGPTPlan else { throw MeetingNotesError("尚未授权使用 ChatGPT 套餐额度，请点击“启用套餐额度”。") }
        if value.needsRefresh {
            do {
                value = try await authorization.refresh(credentials: value)
                // Persist rotated tokens even if cancellation arrived just after refresh completed.
                try ChatGPTConnectionStore.save(value, defaults: defaults)
                apply(value)
            } catch {
                if let authError = error as? ChatGPTAuthorizationError, authError.requiresSignIn {
                    let registration = value.signedOutRegistration
                    apply(registration); models = []
                    try ChatGPTConnectionStore.save(registration, defaults: defaults)
                    message = "ChatGPT 登录已失效，请重新登录。"
                }
                throw error
            }
        }
        guard value.canUseChatGPTPlan else { throw MeetingNotesError("尚未授权使用 ChatGPT 套餐额度，请点击“启用套餐额度”。") }
        return value
    }

    func requestContext() async throws -> (model: String, accessToken: String) {
        guard !isWorking else { throw MeetingNotesError("请先完成 ChatGPT 登录或模型刷新。") }
        let value = try await freshCredentials()
        if models.isEmpty { try await updateModels(using: value) }
        guard !selectedModel.isEmpty, models.contains(where: { $0.slug == selectedModel }) else {
            throw MeetingNotesError("请选择当前账号开放的 ChatGPT 模型。")
        }
        return (selectedModel, value.accessToken)
    }

    func disconnect() {
        guard !isWorking, let credentials else { return }
        isWorking = true
        task = Task {
            defer { isWorking = false; task = nil }
            var revokeFailure = false
            do { try await authorization.revoke(credentials: credentials) }
            catch { revokeFailure = true }
            do {
                let registration = credentials.signedOutRegistration
                try ChatGPTConnectionStore.save(registration, defaults: defaults)
                apply(registration); models = []; selectedModel = ""
                message = revokeFailure ? "已退出本机登录；远端撤销未确认，请在 ChatGPT 设置中断开本应用。" : "已退出本软件的 ChatGPT 登录。"
            } catch { message = error.localizedDescription }
        }
    }

    func cancel() { task?.cancel() }
}

struct ChatGPTConnectionView: View {
    @ObservedObject var model: ChatGPTConnectionModel
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(model.identity).font(.subheadline.bold())
            HStack {
                if model.isWorking {
                    ProgressView().controlSize(.small)
                    Button("取消") { model.cancel() }
                } else {
                    Button(model.isConnected ? "重新登录 ChatGPT" : "使用 ChatGPT 登录") { model.connect() }
                    if model.hasRegistration {
                        Button("使用其他账号") { model.connect(useDifferentAccount: true) }
                    }
                    if model.isConnected {
                        if !model.canUsePlan { Button("启用套餐额度") { model.connect(requestConsent: true) } }
                        Button("刷新模型") { model.refreshModels() }.disabled(!model.canUsePlan)
                        Button("退出此账号") { model.disconnect() }
                    }
                }
            }
            if !model.models.isEmpty {
                Picker("总结模型", selection: $model.selectedModel) {
                    ForEach(model.models, id: \.slug) { item in Text(item.displayName).tag(item.slug) }
                }.disabled(model.isWorking)
            }
            Text(model.message).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }.task { model.restoreIfNeeded() }
    }
}
