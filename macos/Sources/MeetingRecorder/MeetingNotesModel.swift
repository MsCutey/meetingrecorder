import AppKit
import Combine
import Security
import SwiftUI
import UniformTypeIdentifiers
import RecorderSupport

struct MeetingAISettings: Codable {
    var transcription = AIServiceConfiguration(baseURL: "https://api.openai.com/v1", model: "gpt-transcribe")
    var summary = AIServiceConfiguration(baseURL: "https://api.deepseek.com", model: "deepseek-flash")
    var useChatGPTSubscription = true

    init() {}
    private enum CodingKeys: String, CodingKey { case transcription, summary, useChatGPTSubscription }
    init(from decoder: Decoder) throws {
        self.init()
        let container = try decoder.container(keyedBy: CodingKeys.self)
        transcription = try container.decodeIfPresent(AIServiceConfiguration.self, forKey: .transcription) ?? transcription
        summary = try container.decodeIfPresent(AIServiceConfiguration.self, forKey: .summary) ?? summary
        useChatGPTSubscription = try container.decodeIfPresent(Bool.self, forKey: .useChatGPTSubscription) ?? false
    }
}

enum MeetingAPIKeys {
    private static let service = "com.mscutey.meetingrecorder.ai"
    static func account(role: String, configuration: AIServiceConfiguration) throws -> String {
        var components = URLComponents(url: try configuration.validatedURL(), resolvingAgainstBaseURL: false)!
        components.scheme = components.scheme?.lowercased()
        components.host = components.host?.lowercased()
        let endpoint = components.url!.absoluteString.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return role + ":" + endpoint
    }
    private static func query(role: String, configuration: AIServiceConfiguration) throws -> [String: Any] {
        return [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                kSecAttrAccount as String: try account(role: role, configuration: configuration)]
    }
    static func read(role: String, configuration: AIServiceConfiguration) throws -> String {
        var query = try query(role: role, configuration: configuration)
        query[kSecReturnData as String] = true; query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return "" }
        guard status == errSecSuccess, let data = result as? Data, let key = String(data: data, encoding: .utf8) else {
            throw MeetingNotesError("无法读取钥匙串中的 API Key（\(status)）。")
        }
        return key
    }
    static func write(_ value: String, role: String, configuration: AIServiceConfiguration) throws {
        var query = try query(role: role, configuration: configuration)
        let key = value.trimmingCharacters(in: .whitespacesAndNewlines)
        if key.isEmpty {
            let status = SecItemDelete(query as CFDictionary)
            guard status == errSecSuccess || status == errSecItemNotFound else { throw MeetingNotesError("删除 API Key 失败（\(status)）。") }
            return
        }
        let attributes = [kSecValueData as String: Data(key.utf8)]
        var status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            query.merge(attributes) { _, new in new }
            query[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
            status = SecItemAdd(query as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw MeetingNotesError("保存 API Key 到钥匙串失败（\(status)）。") }
    }
}

@MainActor final class MeetingNotesModel: ObservableObject {
    @Published var settings: MeetingAISettings {
        didSet { stageKey(role: "transcription"); stageKey(role: "summary") }
    }
    @Published var transcriptionKey = ""
    @Published var summaryKey = ""
    @Published var source: URL?
    @Published var document: MeetingNotesDocument?
    @Published var summaryDraft = ""
    @Published var message = "选择已保存的录音，生成会后纪要。"
    @Published var isBusy = false
    @Published var isPresented = false
    @Published var showsSettings = false
    @Published var selectedTab = 0
    @Published private(set) var isPresentingFilePanel = false
    @Published private var exportedDraft: String?
    private var filePanel: NSSavePanel?
    private var task: Task<Void, Never>?
    private let defaults: UserDefaults
    private let readKey: (String, AIServiceConfiguration) throws -> String
    private let writeKey: (String, String, AIServiceConfiguration) throws -> Void
    private var activeKeyAccounts: [String: String] = [:]
    private var keyDrafts: [String: String] = [:]
    private var loadedKeyAccounts: Set<String> = []
    private var failedKeyAccounts: Set<String> = []
    let chatGPT: ChatGPTConnectionModel
    private var chatGPTObservation: AnyCancellable?

    var hasUnsavedEdits: Bool { summaryDraft != (document?.summary ?? "") }
    var hasUnprotectedEdits: Bool { hasUnsavedEdits && exportedDraft != summaryDraft }
    var hasPendingWork: Bool { isBusy || chatGPT.isWorking || isPresentingFilePanel }

    init(defaults: UserDefaults = .standard,
         readKey: @escaping (String, AIServiceConfiguration) throws -> String = MeetingAPIKeys.read,
         writeKey: @escaping (String, String, AIServiceConfiguration) throws -> Void = MeetingAPIKeys.write) {
        self.defaults = defaults; self.readKey = readKey; self.writeKey = writeKey
        self.chatGPT = ChatGPTConnectionModel(defaults: defaults)
        settings = defaults.data(forKey: "meetingAI.v1")
            .flatMap { try? JSONDecoder().decode(MeetingAISettings.self, from: $0) } ?? MeetingAISettings()
        chatGPTObservation = chatGPT.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }
    }

    func show(_ source: URL? = nil) {
        guard !isPresentingFilePanel else { return }
        if !isBusy, let source {
            guard preserveEditsBeforeLeaving() else { return }
            self.source = source
            reload()
        }
        isPresented = true
        loadKeyIfNeeded(role: "transcription"); loadKeyIfNeeded(role: "summary")
        if transcriptionKey.isEmpty || (settings.useChatGPTSubscription ? !chatGPT.isConnected : summaryKey.isEmpty) {
            showsSettings = true
        }
    }

    func importRecording(presentingIn window: NSWindow? = nil) {
        guard !hasPendingWork else { return }
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.mpeg4Audio, .mpeg4Movie, .quickTimeMovie, .mp3, .wav, .aiff]
        panel.canChooseDirectories = false; panel.allowsMultipleSelection = false
        panel.prompt = "选择音视频"
        presentFilePanel(panel, for: window) { [weak self] url in
            if let url { self?.show(url) }
        }
    }

    func reload() {
        document = nil; summaryDraft = ""; exportedDraft = nil
        guard let source else { return }
        do {
            document = try MeetingNotesStore.load(for: source)
            summaryDraft = document?.summary ?? ""
            if !FileManager.default.fileExists(atPath: source.path) {
                message = "录音文件不存在；已有纪要仍可编辑和导出。恢复录音后才能继续 AI 处理。"
            } else if let document, try !document.matches(source: source) {
                message = "录音已变化，显示的是旧纪要；请重新转写。"
            } else if let document {
                message = document.transcriptComplete ? "转写已保存，可生成或重新生成纪要。" : "已有部分转写，点击继续可从已完成分段恢复。"
            } else { message = "尚未转写。仅在点击处理并确认后发送内容。" }
        } catch { message = "读取纪要失败：\(error.localizedDescription) 可重新转写，旧文件会先备份。" }
    }

    private func keyValue(role: String) -> String { role == "transcription" ? transcriptionKey : summaryKey }
    private func setKey(_ value: String, role: String) {
        if role == "transcription" { transcriptionKey = value } else { summaryKey = value }
    }

    private func stageKey(role: String) {
        let configuration = role == "transcription" ? settings.transcription : settings.summary
        let account = try? MeetingAPIKeys.account(role: role, configuration: configuration)
        guard activeKeyAccounts[role] != account else { return }
        if let previous = activeKeyAccounts[role] { keyDrafts[previous] = keyValue(role: role) }
        activeKeyAccounts[role] = account
        setKey(account.flatMap { keyDrafts[$0] } ?? "", role: role)
    }

    func loadKeyIfNeeded(role: String) {
        stageKey(role: role)
        guard let account = activeKeyAccounts[role], !loadedKeyAccounts.contains(account) else { return }
        if !keyValue(role: role).isEmpty { loadedKeyAccounts.insert(account); return }
        loadKey(role: role)
    }

    /// Explicit reload only; opening a file must never overwrite an unsaved credential draft.
    func loadKey(role: String) {
        stageKey(role: role)
        let configuration = role == "transcription" ? settings.transcription : settings.summary
        guard let account = activeKeyAccounts[role] else { return }
        loadedKeyAccounts.insert(account)
        do {
            let value = try readKey(role, configuration)
            setKey(value, role: role); keyDrafts[account] = value; failedKeyAccounts.remove(account)
        } catch { failedKeyAccounts.insert(account); message = error.localizedDescription }
    }

    func saveSettings() {
        do {
            _ = try settings.transcription.validatedURL()
            if !settings.useChatGPTSubscription { _ = try settings.summary.validatedURL() }
            let roles = settings.useChatGPTSubscription ? ["transcription"] : ["transcription", "summary"]
            for role in roles {
                if let account = activeKeyAccounts[role], failedKeyAccounts.contains(account), keyValue(role: role).isEmpty {
                    throw MeetingNotesError("钥匙串读取失败，尚未保存。请重新读取 Key 或填写新的 Key，避免误删已有凭据。")
                }
            }
            try writeKey(transcriptionKey, "transcription", settings.transcription)
            if !settings.useChatGPTSubscription { try writeKey(summaryKey, "summary", settings.summary) }
            for role in roles {
                if let account = activeKeyAccounts[role] { failedKeyAccounts.remove(account); loadedKeyAccounts.insert(account) }
            }
            defaults.set(try JSONEncoder().encode(settings), forKey: "meetingAI.v1")
            message = "配置已保存；API Key 保存在本机钥匙串。"
        } catch { message = error.localizedDescription }
    }

    func start(transcribeOnly: Bool = false, restart: Bool = false) {
        guard !isBusy, !isPresentingFilePanel, let source else { return }
        if summaryDraft != (document?.summary ?? "") { saveEdits(); if summaryDraft != (document?.summary ?? "") { return } }
        do {
            guard FileManager.default.fileExists(atPath: source.path) else {
                throw MeetingNotesError("录音文件不存在，请重新选择文件。已有纪要仍可导出。")
            }
            let needsTranscription = restart || document?.transcriptComplete != true
            let useSubscription = !transcribeOnly && settings.useChatGPTSubscription
            if needsTranscription {
                _ = try settings.transcription.validatedURL()
                guard !transcriptionKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                    throw MeetingNotesError("请先填写语音转写服务的 API Key。")
                }
            }
            if !transcribeOnly {
                if useSubscription {
                    chatGPT.restoreIfNeeded()
                    guard chatGPT.isConnected, chatGPT.canUsePlan, !chatGPT.isWorking else {
                        throw MeetingNotesError("请先完成 ChatGPT 登录并授权套餐额度，或先选择“仅转写”。")
                    }
                } else {
                    _ = try settings.summary.validatedURL()
                    guard !summaryKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                        throw MeetingNotesError("请先填写纪要总结服务的 API Key，或先选择“仅转写”。")
                    }
                }
            }
            let alert = NSAlert()
            alert.messageText = restart ? "重新转写这份录音？" : "开始会后处理？"
            var destinations: [String] = []
            if needsTranscription { destinations.append("音轨 → \(try settings.transcription.validatedURL().absoluteString)") }
            if useSubscription {
                destinations.append("转写文本 → OpenAI（ChatGPT 套餐额度，账号：\(chatGPT.identity)）")
            } else if !transcribeOnly { destinations.append("转写文本 → \(try settings.summary.validatedURL().absoluteString)") }
            guard !destinations.isEmpty else { message = "转写已完成，可以生成纪要或导出。"; return }
            alert.informativeText = "\(source.lastPathComponent)\n\n\(destinations.joined(separator: "\n"))\n\n视频画面不会上传。" +
                (useSubscription ? "纪要使用 ChatGPT 套餐额度，音频转写的 API 费用独立计算。" : "API 服务可能按用量计费。") +
                (restart ? "旧转写与纪要将先备份，再重新识别。" : "已完成的分段会保留；取消不会撤回已发出的请求。")
            alert.addButton(withTitle: "开始处理"); alert.addButton(withTitle: "取消")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
            let configuration = settings, transcriptionKey = transcriptionKey, summaryKey = summaryKey
            isBusy = true
            task = Task { [self] in
                defer { isBusy = false; task = nil }
                let activity = ProcessInfo.processInfo.beginActivity(options: [.userInitiated], reason: "生成会后纪要")
                defer { ProcessInfo.processInfo.endActivity(activity) }
                do {
                    let processor = MeetingNotesProcessor()
                    if needsTranscription {
                        document = try await processor.transcribe(source: source, configuration: configuration.transcription,
                            apiKey: transcriptionKey, restart: restart) { [weak self] value in
                                await MainActor.run { self?.message = value }
                            }
                    }
                    if !transcribeOnly {
                        message = "正在生成纪要，长会议会分段归纳…"
                        if useSubscription {
                            message = "正在使用 ChatGPT 订阅生成纪要…"
                            let context = try await chatGPT.requestContext()
                            let service = AIServiceConfiguration(baseURL: "https://api.openai.com/v1", model: context.model)
                            let client = ChatGPTSummaryClient()
                            document = try await processor.summarize(source: source, configuration: service) { transcript in
                                try await client.summarize(transcript: transcript, model: context.model, accessToken: context.accessToken)
                            }
                        } else {
                            document = try await processor.summarize(source: source, configuration: configuration.summary, apiKey: summaryKey)
                        }
                    }
                    summaryDraft = document?.summary ?? ""
                    message = transcribeOnly ? "转写已保存，可以生成纪要或复制给 ChatGPT。" : "纪要已保存，请核对决策、待办及引用的原文片段。"
                } catch {
                    reload()
                    message = (error is CancellationError || Task.isCancelled) ? "处理已取消，已完成的转写和原有纪要已保留。" : "处理失败：\(error.localizedDescription) 已完成的转写已保留，可重试。"
                }
            }
        } catch { message = error.localizedDescription; showsSettings = true }
    }

    func cancel() { task?.cancel() }

    @discardableResult func saveEdits() -> Bool {
        guard !isBusy, !isPresentingFilePanel else { return false }
        guard hasUnsavedEdits else { return true }
        guard let source, var document else { return false }
        document.summary = summaryDraft; document.updatedAt = Date()
        do {
            let encoder = JSONEncoder()
            encoder.outputFormatting = .sortedKeys
            guard let current = try MeetingNotesStore.load(for: source),
                  let baseline = self.document,
                  try encoder.encode(current) == encoder.encode(baseline) else {
                throw MeetingNotesError("纪要文件已被其他操作修改或删除，请导出当前草稿，避免覆盖其他修改。")
            }
            try MeetingNotesStore.save(document, for: source)
            self.document = document; exportedDraft = nil; message = "修改已保存。"
            return true
        } catch { message = "保存失败：\(error.localizedDescription)"; return false }
    }

    func preserveEditsBeforeLeaving() -> Bool {
        if !hasUnprotectedEdits { return true }
        return saveEdits()
    }

    func close() {
        guard !hasPendingWork, preserveEditsBeforeLeaving() else { return }
        isPresented = false
    }

    func writeMarkdown(to url: URL) throws {
        guard var document else { throw MeetingNotesError("尚无可导出的纪要。") }
        if let source, [source, MeetingNotesStore.url(for: source)].contains(where: {
            $0.standardizedFileURL.resolvingSymlinksInPath() == url.standardizedFileURL.resolvingSymlinksInPath()
        }) { throw MeetingNotesError("请选择新的 Markdown 文件，不能覆盖录音或纪要数据。") }
        document.summary = summaryDraft
        try document.markdown.write(to: url, atomically: true, encoding: .utf8)
        exportedDraft = summaryDraft
    }

    func copyForChatGPT() {
        guard let document, document.transcriptComplete else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(document.chatGPTPrompt, forType: .string)
        message = "已复制转写与纪要提示词，可粘贴到 ChatGPT 使用订阅；尚未发送内容。"
    }

    func pasteSummary() {
        guard !isBusy, !isPresentingFilePanel, document?.transcriptComplete == true,
              let text = NSPasteboard.general.string(forType: .string),
              !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        if !summaryDraft.isEmpty {
            let alert = NSAlert()
            alert.messageText = "用剪贴板内容替换纪要草稿？"
            alert.addButton(withTitle: "替换草稿"); alert.addButton(withTitle: "取消")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        summaryDraft = text
        selectedTab = 0
        message = "已粘贴纪要，请核对后保存修改。"
    }

    func exportMarkdown(presentingIn window: NSWindow? = nil) {
        guard !hasPendingWork, let document else { return }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "md") ?? .plainText]
        panel.nameFieldStringValue = document.sourceName + ".minutes.md"
        presentFilePanel(panel, for: window) { [weak self] url in
            guard let self, let url else { return }
            do {
                try writeMarkdown(to: url)
                message = "已导出：\(url.lastPathComponent)。可关闭窗口；源文件旁的纪要仅在保存修改成功后更新。"
            } catch { message = "导出失败：\(error.localizedDescription)" }
        }
    }

    private func presentFilePanel(_ panel: NSSavePanel, for window: NSWindow?, completion: @escaping (URL?) -> Void) {
        guard !hasPendingWork else { return }
        var owner = window ?? NSApp.keyWindow ?? NSApp.mainWindow
        while let sheet = owner?.attachedSheet { owner = sheet }
        guard let owner else {
            message = "请先打开会后纪要窗口，再选择文件或导出。"
            return
        }
        // Bind the native panel to this SwiftUI sheet instead of entering a nested
        // application-modal run loop, which can leave background UI automation stale.
        filePanel = panel
        isPresentingFilePanel = true
        panel.beginSheetModal(for: owner) { [weak self, weak panel, weak owner] response in
            let url = response == .OK ? panel?.url : nil
            panel?.orderOut(nil)
            guard let self else { return }
            filePanel = nil
            isPresentingFilePanel = false
            owner?.makeKeyAndOrderFront(nil)
            completion(url)
        }
    }
}
