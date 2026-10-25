import AppKit
import SwiftUI

@main struct MeetingRecorderApp: App {
    @StateObject private var model = RecorderModel()
    @StateObject private var notes = MeetingNotesModel()
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        WindowGroup("MeetingRecorder · 会议录屏", id: "main") {
            ContentView(model: model, notes: notes)
                .task {
                    delegate.model = model
                    delegate.notes = notes
                    if CommandLine.arguments.contains("--smoke-test") {
                        await SmokeTest.run(model)
                    } else if CGPreflightScreenCaptureAccess() {
                        await model.refresh()
                    }
                }
        }
        .defaultSize(width: 940, height: 720)
        .commands {
            CommandGroup(after: .newItem) {
                Button(model.isBusy ? "停止并保存" : "开始录制") {
                    Task { if model.canPause { await model.stop() } else { await model.start() } }
                }
                .keyboardShortcut("r", modifiers: [.command, .shift])
                .disabled(model.state == .preparing || model.state == .finishing)
                Button(model.state == .paused ? "继续录制" : "暂停录制") { model.pauseResume() }
                    .keyboardShortcut("p", modifiers: [.command, .shift]).disabled(!model.canPause)
                Button("切换麦克风静音") { model.toggleMicrophone() }
                    .keyboardShortcut("m", modifiers: [.command, .shift])
                    .disabled(!model.canPause || !model.settings.microphone)
            }
        }
        MenuBarExtra {
            Text("MeetingRecorder · \(model.state.rawValue)")
            if model.isBusy { Text(model.elapsedText) }
            Button("显示主窗口") {
                NSApp.activate(ignoringOtherApps: true)
                if let window = NSApp.windows.first(where: { $0.canBecomeMain }) { window.makeKeyAndOrderFront(nil) }
            }
            Divider()
            Button(model.isBusy ? "停止并保存" : "开始录制") {
                Task { if model.canPause { await model.stop() } else { await model.start() } }
            }.disabled(model.state == .preparing || model.state == .finishing)
            Button(model.state == .paused ? "继续录制" : "暂停录制") { model.pauseResume() }.disabled(!model.canPause)
            Button(model.microphoneMuted ? "开启麦克风" : "静音麦克风") { model.toggleMicrophone() }
                .disabled(!model.canPause || !model.settings.microphone)
            Divider()
            Button("退出") { NSApp.terminate(nil) }
        } label: {
            Label(model.canPause ? model.elapsedText : "MeetingRecorder",
                  systemImage: model.state == .recording ? "record.circle.fill" : "record.circle")
        }
    }
}

@MainActor final class AppDelegate: NSObject, NSApplicationDelegate {
    weak var model: RecorderModel?
    weak var notes: MeetingNotesModel?
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if notes?.hasPendingWork == true {
            let alert = NSAlert()
            alert.messageText = "会后纪要或 ChatGPT 登录正在处理"
            alert.informativeText = "请先取消处理或等待完成，再退出。已完成的转写分段会保留。"
            alert.runModal()
            return .terminateCancel
        }
        if let notes, !notes.preserveEditsBeforeLeaving() {
            let alert = NSAlert()
            alert.messageText = "纪要修改尚未保存"
            alert.informativeText = "请保存修改或导出后再退出。"
            alert.runModal()
            return .terminateCancel
        }
        guard let model else { return .terminateNow }
        if !model.isBusy {
            model.restoreStageManagerIfNeeded()
            return .terminateNow
        }
        if model.state == .preparing {
            model.cancelPreparation()
            Task { @MainActor in
                while model.state == .preparing { try? await Task.sleep(for: .milliseconds(100)) }
                sender.reply(toApplicationShouldTerminate: true)
            }
            return .terminateLater
        }
        if !model.canPause {
            let alert = NSAlert()
            alert.messageText = "请等待准备或保存完成后再退出。"
            alert.runModal()
            return .terminateCancel
        }
        Task { await model.stop(); sender.reply(toApplicationShouldTerminate: true) }
        return .terminateLater
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { sender.windows.first(where: { $0.canBecomeMain })?.makeKeyAndOrderFront(nil) }
        return true
    }
}

struct ContentView: View {
    @ObservedObject var model: RecorderModel
    @ObservedObject var notes: MeetingNotesModel
    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 14) {
                Image(nsImage: NSApplication.shared.applicationIconImage).resizable().frame(width: 48, height: 48)
                VStack(alignment: .leading, spacing: 4) {
                    Text("MeetingRecorder").font(.title2.bold())
                    Text("本地会议录制 · macOS · AI 纪要预览").font(.subheadline).foregroundStyle(.secondary)
                }
                Spacer()
                Circle().fill(model.state == .recording ? .red : model.state == .paused ? .orange : .gray)
                    .frame(width: 9, height: 9)
                Text(model.countdownRemaining > 0 ? "\(model.countdownRemaining) 秒后开始" : model.state.rawValue)
            }.padding(22)
            Divider()
            HSplitView {
                ScrollView {
                    VStack(alignment: .leading, spacing: 22) {
                        RecorderSection("录制模式") {
                            Picker("模式", selection: $model.settings.audioOnly) {
                                Text("视频 + 声音 · MP4").tag(false)
                                Text("仅声音 · M4A").tag(true)
                            }.pickerStyle(.segmented).labelsHidden().padding(10)
                        }
                        RecorderSection("画面来源") {
                            VStack(alignment: .leading, spacing: 12) {
                                HStack {
                                    Button(model.loading ? "正在读取…" : "授权并刷新来源", systemImage: "arrow.clockwise") {
                                        Task { await model.refresh() }
                                    }.disabled(model.loading)
                                    Button("权限设置") { model.openPrivacySettings() }
                                }
                                if !model.targets.isEmpty {
                                    Picker("来源", selection: $model.sourceID) {
                                        ForEach(model.targets.filter { $0.display != nil }) { Text($0.title).tag($0.id) }
                                        Text(model.selectedWindowTitle).tag("system:window")
                                    }.disabled(model.settings.audioOnly)
                                }
                                Button("选择窗口并开始录制", systemImage: "macwindow") {
                                    Task { await model.chooseWindow() }
                                }.disabled(model.settings.audioOnly || model.loading)
                                Picker("画质", selection: $model.settings.maxHeight) {
                                    Text("1080p").tag(1080); Text("720p").tag(720); Text("源分辨率").tag(16384)
                                }.disabled(model.settings.audioOnly)
                                Toggle("整屏录制时排除本应用", isOn: $model.settings.excludeApp)
                                    .disabled(model.settings.audioOnly)
                                Text(model.settings.audioOnly ? "仅声音模式不读取或保存视频帧，系统声音仍需屏幕与系统音频权限。" : "30 fps · H.264。窗口录制的系统声音限于所属应用。")
                                    .font(.caption).foregroundStyle(.secondary)
                                if !model.settings.audioOnly {
                                    Toggle("录屏时自动关闭台前调度，结束后恢复", isOn: $model.automaticallyManageStageManager)
                                    Text(model.automaticallyManageStageManager
                                        ? "保留原来的开关状态。取消或失败也会恢复；强制退出后，下次打开软件恢复。"
                                        : "后台窗口录制请手动关闭台前调度；窗口被收进左侧或最小化时，系统可能暂停画面。")
                                        .font(.caption).foregroundStyle(.secondary)
                                }
                            }.padding(10)
                        }
                        RecorderSection("声音来源") {
                            VStack(alignment: .leading, spacing: 12) {
                                Toggle("系统声音", isOn: $model.settings.systemAudio)
                                Toggle("麦克风", isOn: $model.settings.microphone)
                                if model.settings.microphone {
                                    Picker("输入设备", selection: $model.settings.microphoneID) {
                                        Text("系统默认").tag("")
                                        ForEach(model.microphones, id: \.uniqueID) { Text($0.localizedName).tag($0.uniqueID) }
                                    }
                                    Toggle("开始时静音麦克风", isOn: $model.settings.startMuted)
                                }
                                Text("双路声音导出为一条混音音轨。建议佩戴耳机，减少回声。")
                                    .font(.caption).foregroundStyle(.secondary)
                            }.padding(10)
                        }
                        RecorderSection("保存与控制") {
                            VStack(alignment: .leading, spacing: 12) {
                                Text(model.settings.outputDirectory).font(.caption).textSelection(.enabled)
                                HStack {
                                    Button("选择目录…") { model.chooseDirectory() }
                                    Picker("倒计时", selection: $model.settings.countdown) {
                                        Text("无").tag(0); Text("3 秒").tag(3); Text("5 秒").tag(5)
                                    }
                                }
                                Text("应用内快捷键：⇧⌘R 开始／停止，⇧⌘P 暂停／继续，⇧⌘M 麦克风。后台可用菜单栏控制。")
                                    .font(.caption).foregroundStyle(.secondary)
                            }.padding(10)
                        }
                    }.padding(20).disabled(model.isBusy)
                }.frame(minWidth: 440, idealWidth: 480)
                VStack(alignment: .leading, spacing: 16) {
                    VStack(alignment: .leading, spacing: 14) {
                        Label(model.settings.audioOnly ? "音频录制" : "屏幕与窗口录制",
                              systemImage: model.settings.audioOnly ? "waveform" : "display")
                            .font(.headline)
                        Text(model.elapsedText).font(.system(size: 42, weight: .medium, design: .monospaced))
                        if !model.captureDimensionsText.isEmpty {
                            Text("输出画面：\(model.captureDimensionsText)").font(.caption).foregroundStyle(.secondary)
                        }
                        if model.stageManagerTemporarilyDisabled {
                            Text("台前调度已临时关闭，录制结束后恢复。").font(.caption).foregroundStyle(.secondary)
                        }
                        if model.videoUnavailable && model.isBusy {
                            Label("窗口画面已停止更新", systemImage: "exclamationmark.triangle")
                                .foregroundStyle(.orange)
                            Text(model.state == .preparing
                                ? "目标窗口可能被台前调度收起或最小化，尚未开始录制。请恢复窗口或取消准备。"
                                : "目标窗口可能被台前调度收起或最小化。恢复窗口后画面会继续；启用的声音轨仍会录制。")
                                .font(.caption)
                        }
                        Text(String(format: "可用磁盘空间 %.1f GB", model.freeGB)).font(.caption).foregroundStyle(.secondary)
                    }.frame(maxWidth: .infinity, alignment: .leading).padding(22)
                        .background(.quaternary, in: RoundedRectangle(cornerRadius: 14))
                    if !model.stageManagerRecoveryMessage.isEmpty {
                        VStack(alignment: .leading, spacing: 8) {
                            Text(model.stageManagerRecoveryMessage).font(.caption).foregroundStyle(.orange)
                            Button("重试恢复台前调度") { model.restoreStageManagerIfNeeded() }.disabled(model.isBusy)
                        }
                    }
                    HStack {
                        Text("录制历史").font(.headline)
                        Spacer()
                        Button("会后纪要", systemImage: "text.bubble") { notes.show() }
                            .disabled(notes.isPresentingFilePanel)
                        Button("打开目录", systemImage: "folder") { NSWorkspace.shared.open(model.outputDirectory) }
                    }
                    if !model.recoveries.isEmpty {
                        ForEach(model.recoveries, id: \.path) { url in
                            HStack {
                                VStack(alignment: .leading, spacing: 4) {
                                    Text("发现未完成录制").font(.caption).foregroundStyle(.orange)
                                    Text(url.lastPathComponent).font(.caption2).foregroundStyle(.secondary)
                                        .lineLimit(1).truncationMode(.middle).help(url.lastPathComponent)
                                }
                                Spacer()
                                Button("尝试恢复") { Task { await model.recover(url) } }.disabled(model.isBusy)
                                Button("关闭恢复提示", systemImage: "xmark") { model.dismissRecovery(url) }
                                    .labelStyle(.iconOnly).disabled(model.isBusy)
                                    .help("关闭这条提示，临时录制文件仍保留")
                                    .accessibilityLabel("关闭恢复提示：\(url.lastPathComponent)")
                            }
                        }
                    }
                    if !model.dismissedRecoveries.isEmpty {
                        Button("显示已关闭的恢复提示（\(model.dismissedRecoveries.count)）") {
                            model.showDismissedRecoveries()
                        }.font(.caption).buttonStyle(.link).disabled(model.isBusy)
                    }
                    if model.history.isEmpty {
                        ContentUnavailableView("暂无录制", systemImage: "record.circle", description: Text("完成录制后会在这里显示本地文件。"))
                    } else {
                        List(model.history, id: \.path) { url in
                            VStack(alignment: .leading, spacing: 8) {
                                Text(url.lastPathComponent).font(.subheadline).lineLimit(2)
                                HStack {
                                    Button("播放") { NSWorkspace.shared.open(url) }
                                    Button("定位") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
                                    Button("纪要") { notes.show(url) }.disabled(notes.hasPendingWork)
                                    Button("删除", role: .destructive) { model.delete(url) }.disabled(model.isBusy || notes.hasPendingWork)
                                }.buttonStyle(.borderless).font(.caption)
                            }.padding(.vertical, 6)
                        }.listStyle(.inset)
                    }
                    Spacer(minLength: 0)
                }.padding(20).frame(minWidth: 330)
            }
            Divider()
            HStack(alignment: .center, spacing: 12) {
                Text(model.message).font(.caption).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading).textSelection(.enabled)
                if !model.isBusy && !model.message.isEmpty {
                    Button("关闭提示", systemImage: "xmark") { model.dismissMessage() }
                        .labelStyle(.iconOnly).help("关闭提示")
                }
                if model.canPause {
                    if model.settings.microphone {
                        Button(model.microphoneMuted ? "开启麦克风" : "静音麦克风") { model.toggleMicrophone() }
                    }
                    Button(model.state == .paused ? "继续" : "暂停") { model.pauseResume() }
                    Button("停止并保存", role: .destructive) { Task { await model.stop() } }.buttonStyle(.borderedProminent)
                } else {
                    if model.isBusy { ProgressView().controlSize(.small) }
                    if model.state == .preparing {
                        Button(model.isChoosingWindow ? "取消选择" : "取消准备") { model.cancelPreparation() }
                    }
                    Button("开始录制", systemImage: "record.circle") { Task { await model.start() } }
                        .buttonStyle(.borderedProminent).disabled(model.isBusy || model.loading)
                }
            }.padding(18)
        }.frame(minWidth: 850, minHeight: 650)
            .sheet(isPresented: $notes.isPresented) { MeetingNotesView(model: notes) }
    }
}

/// A title and ordinary container keep every control visible to accessibility.
/// Native GroupBox inside ScrollView triggers a UIElementTreeTransformation
/// Array.remove(at:) crash in Computer Use 26.1015 on macOS 27.
private struct RecorderSection<Content: View>: View {
    let title: String
    let content: Content

    init(_ title: String, @ViewBuilder content: () -> Content) {
        self.title = title
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.subheadline.weight(.semibold))
            content.frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(.quaternary, lineWidth: 1).allowsHitTesting(false))
    }
}
