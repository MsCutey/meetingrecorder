import AppKit
import AVFoundation
import ScreenCaptureKit
import SwiftUI
import RecorderSupport

struct Settings: Codable {
    var outputDirectory = FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("MeetingRecorder").path
    var audioOnly = false
    var systemAudio = true
    var microphone = false
    var microphoneID = ""
    var startMuted = false
    var maxHeight = 1080
    var countdown = 3
    var excludeApp = true
}

struct CaptureTarget: Identifiable {
    let id: String
    let title: String
    let display: SCDisplay?
    let window: SCWindow?
}

enum RecorderState: String {
    case idle = "准备就绪", preparing = "正在准备", recording = "正在录制"
    case paused = "已暂停", finishing = "正在保存"
}

@MainActor final class RecorderModel: ObservableObject {
    @Published var settings: Settings {
        didSet { if let data = try? JSONEncoder().encode(settings) { UserDefaults.standard.set(data, forKey: "macSettings.v1") } }
    }
    @Published var state = RecorderState.idle
    @Published var targets: [CaptureTarget] = []
    @Published var sourceID = ""
    @Published var microphones: [AVCaptureDevice] = []
    @Published var message = "首次使用请点击“授权并刷新来源”。"
    @Published var history: [URL] = []
    @Published var recoveries: [URL] = []
    @Published var elapsed = 0.0
    @Published var microphoneMuted = false
    @Published var loading = false
    @Published var countdownRemaining = 0
    @Published var freeGB = 0.0
    @Published var lastOutput: URL?
    var capturedAudioPeak = 0.0
    private var content: SCShareableContent?
    private var engine: CaptureEngine?
    private var timer: Timer?
    private var pendingOutput: URL?
    private var pendingAudioOnly = false
    private var wakeLock: NSObjectProtocol?
    var isBusy: Bool { state != .idle }
    var canPause: Bool { state == .recording || state == .paused }
    var elapsedText: String {
        let seconds = Int(elapsed)
        return String(format: "%02d:%02d:%02d", seconds / 3600, seconds / 60 % 60, seconds % 60)
    }
    var outputDirectory: URL { URL(fileURLWithPath: settings.outputDirectory, isDirectory: true) }

    init() {
        settings = UserDefaults.standard.data(forKey: "macSettings.v1")
            .flatMap { try? JSONDecoder().decode(Settings.self, from: $0) } ?? Settings()
        scanFiles()
    }

    func refresh() async {
        guard !isBusy, !loading else { return }
        loading = true
        defer { loading = false }
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true)
            self.content = content
            targets = content.displays.enumerated().map { index, display in
                CaptureTarget(id: "display:\(display.displayID)",
                    title: "显示器 \(index + 1) · \(display.width) × \(display.height)", display: display, window: nil)
            }
            targets += content.windows.filter {
                $0.windowLayer == 0 && $0.frame.width > 80 && $0.frame.height > 80 &&
                $0.owningApplication?.processID != ProcessInfo.processInfo.processIdentifier &&
                !($0.title ?? "").isEmpty
            }.sorted { ($0.owningApplication?.applicationName ?? "") < ($1.owningApplication?.applicationName ?? "") }
            .map { CaptureTarget(id: "window:\($0.windowID)",
                title: "\($0.owningApplication?.applicationName ?? "窗口") · \($0.title ?? "未命名")", display: nil, window: $0) }
            if !targets.contains(where: { $0.id == sourceID }) { sourceID = targets.first?.id ?? "" }
            microphones = AVCaptureDevice.DiscoverySession(deviceTypes: [.microphone, .external],
                mediaType: .audio, position: .unspecified).devices
            if !settings.microphoneID.isEmpty && !microphones.contains(where: { $0.uniqueID == settings.microphoneID }) {
                settings.microphoneID = ""
            }
            message = "来源已更新。系统声音来自所选来源中的应用。"
        } catch {
            message = "无法读取来源：\(error.localizedDescription)\n请在系统设置 → 隐私与安全性 → 屏幕与系统音频录制中允许 MeetingRecorder，然后重新打开应用。"
        }
    }

    func chooseDirectory() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true; panel.canChooseFiles = false
        panel.canCreateDirectories = true; panel.prompt = "选择保存目录"
        if panel.runModal() == .OK, let url = panel.url { settings.outputDirectory = url.path; scanFiles() }
    }

    func start(preapproved: Bool = false) async {
        guard state == .idle, !loading else { return }
        if !preapproved && !UserDefaults.standard.bool(forKey: "acceptedRecordingNotice") {
            let alert = NSAlert()
            alert.messageText = "录制前请取得必要授权"
            alert.informativeText = "请告知参会者并取得所有必要授权。录制文件仅保存在本机。macOS 会显示录制指示，并可能定期请求重新授权。"
            alert.addButton(withTitle: "我已了解并同意"); alert.addButton(withTitle: "取消")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
            UserDefaults.standard.set(true, forKey: "acceptedRecordingNotice")
        }
        lastOutput = nil
        if content == nil { await refresh() }
        guard let content, let display = content.displays.first else {
            if self.content != nil { message = "尚无可用的显示器，请连接显示器后刷新来源。" }
            return
        }
        if settings.audioOnly && !settings.systemAudio && !settings.microphone {
            message = "仅声音模式至少需要启用一个声音来源。"; return
        }
        state = .preparing
        defer { countdownRemaining = 0 }
        do {
            if settings.microphone {
                guard AVCaptureDevice.default(for: .audio) != nil else { throw RecorderError("未发现麦克风，请连接设备或关闭麦克风选项。") }
                let granted = await AVCaptureDevice.requestAccess(for: .audio)
                guard granted else { throw RecorderError("麦克风未获授权，请在系统设置 → 隐私与安全性 → 麦克风中允许访问。") }
            }
            try FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)
            updateDiskSpace()
            guard freeGB > 0.5 else { throw RecorderError("可用空间不足 512 MB，请更换保存目录。") }
            let target = targets.first { $0.id == sourceID }
            if !settings.audioOnly && target == nil { throw RecorderError("请选择画面来源。") }
            let filter: SCContentFilter
            var width = display.width, height = display.height
            if let window = target?.window, !settings.audioOnly {
                filter = SCContentFilter(desktopIndependentWindow: window)
                width = Int(filter.contentRect.width * CGFloat(filter.pointPixelScale))
                height = Int(filter.contentRect.height * CGFloat(filter.pointPixelScale))
            } else {
                let selectedDisplay = settings.audioOnly ? display : (target?.display ?? display)
                let excluded = settings.excludeApp ? content.applications.filter {
                    $0.processID == ProcessInfo.processInfo.processIdentifier
                } : []
                filter = SCContentFilter(display: selectedDisplay, excludingApplications: excluded, exceptingWindows: [])
                width = selectedDisplay.width; height = selectedDisplay.height
            }
            for remaining in stride(from: settings.countdown, through: 1, by: -1) {
                countdownRemaining = remaining
                try await Task.sleep(for: .seconds(1))
            }
            let output = RecordingFiles.uniqueURL(in: outputDirectory, extension: settings.audioOnly ? "m4a" : "mp4")
            let engine = CaptureEngine()
            engine.onFailure = { [weak self] error in
                Task { @MainActor in
                    guard let self, self.canPause else { return }
                    self.message = "录制中断：\(error)。正在尝试保存。"
                    await self.stop()
                }
            }
            self.engine = engine
            pendingOutput = output; pendingAudioOnly = settings.audioOnly
            try await engine.start(CaptureOptions(audioOnly: settings.audioOnly, systemAudio: settings.systemAudio,
                microphone: settings.microphone, microphoneID: settings.microphoneID.isEmpty ? nil : settings.microphoneID,
                startMuted: settings.startMuted, maxHeight: settings.maxHeight, excludeApp: settings.excludeApp,
                filter: filter, width: width, height: height, output: output))
            elapsed = 0; microphoneMuted = settings.startMuted; state = .recording
            message = freeGB < 5 ? "可用磁盘空间低于 5 GB，请留意剩余容量。" : "录制已开始。暂停时间不会写入成品。"
            wakeLock = ProcessInfo.processInfo.beginActivity(options: [.userInitiated, .idleSystemSleepDisabled],
                reason: "正在录制本地会议")
            timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
                Task { @MainActor in
                    guard let self else { return }
                    self.elapsed = self.engine?.elapsed ?? self.elapsed
                    self.updateDiskSpace()
                    if self.freeGB < 0.5 && self.canPause {
                        self.message = "空间不足 512 MB，自动停止并保存。"
                        await self.stop()
                    }
                }
            }
        } catch {
            // startCapture can fail after allocating the writer; start() cancels it.
            self.engine = nil; state = .idle
            message = "无法开始录制：\(error.localizedDescription)"
            scanFiles()
        }
    }

    func pauseResume() {
        if state == .recording { engine?.pause(); state = .paused }
        else if state == .paused { engine?.resume(); state = .recording }
    }
    func toggleMicrophone() {
        guard canPause, settings.microphone else { return }
        microphoneMuted.toggle(); engine?.setMuted(microphoneMuted)
    }
    func stop() async {
        guard canPause, let engine, let output = pendingOutput else { return }
        state = .finishing; timer?.invalidate(); timer = nil
        defer {
            if let wakeLock { ProcessInfo.processInfo.endActivity(wakeLock) }
            wakeLock = nil; self.engine = nil; state = .idle; scanFiles()
        }
        do {
            let partial = try await engine.stop()
            capturedAudioPeak = engine.capturedAudioPeak
            lastOutput = try await RecordingExporter.export(partial: partial, destination: output, audioOnly: pendingAudioOnly)
            message = "已保存：\(output.lastPathComponent)"
        } catch { message = "保存失败：\(error.localizedDescription) 临时文件已保留，可尝试恢复。" }
    }

    func recover(_ partial: URL) async {
        guard !isBusy else { return }
        state = .finishing
        defer { state = .idle; scanFiles() }
        do {
            let info = try JSONDecoder().decode(RecoveryInfo.self, from: Data(contentsOf: partial.appendingPathExtension("json")))
            let destination = RecordingFiles.uniqueURL(in: partial.deletingLastPathComponent(),
                extension: info.audioOnly ? "m4a" : "mp4")
            lastOutput = try await RecordingExporter.export(partial: partial, destination: destination, audioOnly: info.audioOnly)
            message = "已恢复：\(destination.lastPathComponent)"
        } catch { message = "无法恢复：\(error.localizedDescription) 原文件已保留。" }
    }

    func scanFiles() {
        let files = (try? FileManager.default.contentsOfDirectory(at: outputDirectory,
            includingPropertiesForKeys: [.creationDateKey], options: [.skipsHiddenFiles])) ?? []
        history = files.filter { ["mp4", "m4a"].contains($0.pathExtension) && !$0.lastPathComponent.contains(".staging.") }
            .sorted { $0.lastPathComponent > $1.lastPathComponent }
        recoveries = files.filter { $0.lastPathComponent.hasSuffix(".partial.mov") }
        updateDiskSpace()
    }
    private func updateDiskSpace() {
        let path = FileManager.default.fileExists(atPath: outputDirectory.path) ? outputDirectory.path : NSHomeDirectory()
        let attributes = try? FileManager.default.attributesOfFileSystem(forPath: path)
        freeGB = (attributes?[.systemFreeSize] as? NSNumber)?.doubleValue ?? 0
        freeGB /= 1_073_741_824
    }
    func delete(_ url: URL) {
        let alert = NSAlert()
        alert.messageText = "将录制移到废纸篓？"
        alert.informativeText = url.lastPathComponent
        alert.addButton(withTitle: "移到废纸篓"); alert.addButton(withTitle: "取消")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        do { try FileManager.default.trashItem(at: url, resultingItemURL: nil); scanFiles() }
        catch { message = error.localizedDescription }
    }
    func openPrivacySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }
}
