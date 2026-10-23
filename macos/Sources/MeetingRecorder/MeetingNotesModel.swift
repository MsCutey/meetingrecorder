import AppKit
import Security
import SwiftUI
import UniformTypeIdentifiers
import RecorderSupport

struct MeetingAISettings: Codable {
    var transcription = AIServiceConfiguration(baseURL: "https://api.openai.com/v1", model: "gpt-transcribe")
    var summary = AIServiceConfiguration(baseURL: "https://api.deepseek.com", model: "deepseek-flash")
}

enum MeetingAPIKeys {
    private static let service = "com.mscutey.meetingrecorder.ai"
    private static func query(role: String, configuration: AIServiceConfiguration) throws -> [String: Any] {
        let endpoint = try configuration.validatedURL().absoluteString.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                kSecAttrAccount as String: role + ":" + endpoint]
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
    @Published var settings: MeetingAISettings
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
    private var task: Task<Void, Never>?

    init() {
        settings = UserDefaults.standard.data(forKey: "meetingAI.v1")
            .flatMap { try? JSONDecoder().decode(MeetingAISettings.self, from: $0) } ?? MeetingAISettings()
    }

    func show(_ source: URL? = nil) {
        if !isBusy, let source {
            if summaryDraft != (document?.summary ?? "") {
                saveEdits()
                guard summaryDraft == (document?.summary ?? "") else { return }
            }
            self.source = source
            reload()
        }
        isPresented = true
        loadKey(role: "transcription"); loadKey(role: "summary")
    }

    func importRecording() {
        guard !isBusy else { return }
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.mpeg4Audio, .mpeg4Movie, .quickTimeMovie, .mp3, .wav, .aiff]
        panel.canChooseDirectories = false; panel.allowsMultipleSelection = false
        panel.prompt = "选择音视频"
        if panel.runModal() == .OK, let url = panel.url { show(url) }
    }

    func reload() {
        document = nil; summaryDraft = ""
        guard let source else { return }
        do {
            document = try MeetingNotesStore.load(for: source)
            summaryDraft = document?.summary ?? ""
            if let document, try !document.matches(source: source) {
                message = "录音已变化，显示的是旧纪要；请重新转写。"
            } else if let document {
                message = document.transcriptComplete ? "转写已保存，可生成或重新生成纪要。" : "已有部分转写，点击继续可从已完成分段恢复。"
            } else { message = "尚未转写。仅在点击处理并确认后发送内容。" }
        } catch { message = "读取纪要失败：\(error.localizedDescription) 可重新转写，旧文件会先备份。" }
    }

    func loadKey(role: String) {
        let configuration = role == "transcription" ? settings.transcription : settings.summary
        // A key is bound to its exact provider URL; changing providers never carries it over.
        if role == "transcription" { transcriptionKey = "" } else { summaryKey = "" }
        do {
            let value = try MeetingAPIKeys.read(role: role, configuration: configuration)
            if role == "transcription" { transcriptionKey = value } else { summaryKey = value }
        } catch { message = error.localizedDescription }
    }

    func saveSettings() {
        do {
            _ = try settings.transcription.validatedURL(); _ = try settings.summary.validatedURL()
            try MeetingAPIKeys.write(transcriptionKey, role: "transcription", configuration: settings.transcription)
            try MeetingAPIKeys.write(summaryKey, role: "summary", configuration: settings.summary)
            UserDefaults.standard.set(try JSONEncoder().encode(settings), forKey: "meetingAI.v1")
            message = "配置已保存；API Key 保存在本机钥匙串。"
        } catch { message = error.localizedDescription }
    }

    func start(transcribeOnly: Bool = false, restart: Bool = false) {
        guard !isBusy, let source else { return }
        if summaryDraft != (document?.summary ?? "") { saveEdits(); if summaryDraft != (document?.summary ?? "") { return } }
        do {
            let needsTranscription = restart || document?.transcriptComplete != true
            if needsTranscription {
                _ = try settings.transcription.validatedURL()
                guard !transcriptionKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                    throw MeetingNotesError("请先填写语音转写服务的 API Key。")
                }
            }
            if !transcribeOnly {
                _ = try settings.summary.validatedURL()
                guard !summaryKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                    throw MeetingNotesError("请先填写纪要总结服务的 API Key，或先选择“仅转写”。")
                }
            }
            let alert = NSAlert()
            alert.messageText = restart ? "重新转写这份录音？" : "开始会后处理？"
            var destinations: [String] = []
            if needsTranscription { destinations.append("音轨 → \(try settings.transcription.validatedURL().absoluteString)") }
            if !transcribeOnly { destinations.append("转写文本 → \(try settings.summary.validatedURL().absoluteString)") }
            guard !destinations.isEmpty else { message = "转写已完成，可以生成纪要或导出。"; return }
            alert.informativeText = "\(source.lastPathComponent)\n\n\(destinations.joined(separator: "\n"))\n\n仅上传音轨，不上传视频画面。服务可能按用量计费。" +
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
                        document = try await processor.summarize(source: source, configuration: configuration.summary, apiKey: summaryKey)
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

    func saveEdits() {
        guard !isBusy, let source, var document else { return }
        document.summary = summaryDraft; document.updatedAt = Date()
        do { try MeetingNotesStore.save(document, for: source); self.document = document; message = "修改已保存。" }
        catch { message = "保存失败：\(error.localizedDescription)" }
    }

    func copyForChatGPT() {
        guard let document, document.transcriptComplete else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(document.chatGPTPrompt, forType: .string)
        message = "已复制转写与纪要提示词，可粘贴到 ChatGPT 使用订阅；尚未发送内容。"
    }

    func pasteSummary() {
        guard !isBusy, document?.transcriptComplete == true,
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

    func exportMarkdown() {
        guard var document else { return }
        document.summary = summaryDraft
        let panel = NSSavePanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "md") ?? .plainText]
        panel.nameFieldStringValue = document.sourceName + ".minutes.md"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        if let source, [source.standardizedFileURL, MeetingNotesStore.url(for: source).standardizedFileURL].contains(url.standardizedFileURL) {
            message = "请选择新的 Markdown 文件，不能覆盖录音或纪要数据。"
            return
        }
        do { try document.markdown.write(to: url, atomically: true, encoding: .utf8); message = "已导出：\(url.lastPathComponent)" }
        catch { message = "导出失败：\(error.localizedDescription)" }
    }
}
