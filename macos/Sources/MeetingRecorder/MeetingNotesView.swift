import SwiftUI
import RecorderSupport

struct MeetingNotesView: View {
    @ObservedObject var model: MeetingNotesModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                VStack(alignment: .leading, spacing: 5) {
                    Label("会后纪要", systemImage: "text.bubble").font(.title2.bold())
                    Text(model.source?.lastPathComponent ?? "选择一份已保存的录音或视频")
                        .font(.subheadline).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer()
                Button("选择文件", systemImage: "doc.badge.plus") { model.importRecording() }.disabled(model.isBusy)
                Button("关闭") { model.close() }.disabled(model.hasPendingWork)
            }
            DisclosureGroup("AI 服务设置", isExpanded: $model.showsSettings) {
              ScrollView {
                VStack(alignment: .leading, spacing: 10) {
                    Text("录制默认仅保存在本机。会后处理时会发送音轨和转写文字至下列服务，可能产生 API 费用。")
                        .font(.caption).foregroundStyle(.secondary)
                    HStack(alignment: .top, spacing: 22) {
                        VStack(alignment: .leading, spacing: 8) {
                            Text("1 · 语音转写").font(.headline)
                            TextField("基础地址，例如 https://api.openai.com/v1", text: $model.settings.transcription.baseURL)
                                .onSubmit { model.loadKeyIfNeeded(role: "transcription") }
                            TextField("转写模型", text: $model.settings.transcription.model)
                            SecureField("转写 API Key", text: $model.transcriptionKey)
                            Button("读取已保存的转写 Key") { model.loadKey(role: "transcription") }.font(.caption)
                            Text("兼容 audio/transcriptions，支持 M4A、JSON 返回。")
                                .font(.caption).foregroundStyle(.secondary)
                            Text("转写需要独立 API Key；ChatGPT 订阅登录仅用于右侧的文字总结。")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        VStack(alignment: .leading, spacing: 8) {
                            Text("2 · 纪要总结").font(.headline)
                            Picker("使用方式", selection: $model.settings.useChatGPTSubscription) {
                                Text("API Key").tag(false)
                                Text("ChatGPT 订阅").tag(true)
                            }.pickerStyle(.segmented).labelsHidden()
                            if model.settings.useChatGPTSubscription {
                                ChatGPTConnectionView(model: model.chatGPT)
                            } else {
                            HStack {
                                Text("服务商预设").font(.subheadline)
                                Spacer()
                                Menu("预设") {
                                    Button("DeepSeek") {
                                        model.settings.summary = AIServiceConfiguration(baseURL: "https://api.deepseek.com", model: "deepseek-flash")
                                        model.loadKeyIfNeeded(role: "summary")
                                    }
                                    Button("OpenAI API") {
                                        model.settings.summary = AIServiceConfiguration(baseURL: "https://api.openai.com/v1", model: "gpt-5.4-mini")
                                        model.loadKeyIfNeeded(role: "summary")
                                    }
                                }.fixedSize()
                            }
                            TextField("基础地址，例如 https://api.deepseek.com", text: $model.settings.summary.baseURL)
                                .onSubmit { model.loadKeyIfNeeded(role: "summary") }
                            TextField("总结模型", text: $model.settings.summary.model)
                            SecureField("总结 API Key", text: $model.summaryKey)
                            Button("读取已保存的总结 Key") { model.loadKey(role: "summary") }.font(.caption)
                            Text("兼容 Chat Completions；此处 Key 按 API 用量计费。")
                                .font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }.textFieldStyle(.roundedBorder)
                    HStack {
                        Button("保存配置与 Key") { model.saveSettings() }
                        Text("Key 存在钥匙串，并与服务地址绑定。更换地址后需重新填写或读取该地址的 Key。")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }.padding(.top, 8)
              }.frame(height: 270)
            }.disabled(model.isBusy)
            Divider()
            HStack {
                Picker("内容", selection: $model.selectedTab) {
                    Text("纪要草稿").tag(0)
                    Text("转写原文").tag(1)
                }.pickerStyle(.segmented).frame(width: 240)
                Spacer()
                Button("复制给 ChatGPT") { model.copyForChatGPT() }
                    .disabled(model.document?.transcriptComplete != true || model.isBusy)
                Button("粘贴纪要") { model.pasteSummary() }
                    .disabled(model.document?.transcriptComplete != true || model.isBusy)
                Button("导出 Markdown") { model.exportMarkdown() }
                    .disabled(model.document == nil || model.isBusy)
            }
            if model.selectedTab == 0 {
                if model.document?.transcriptComplete != true && model.summaryDraft.isEmpty {
                    ContentUnavailableView("尚未生成纪要", systemImage: "text.bubble",
                        description: Text("先转写，再整理概要、讨论要点、决策、待办和未决问题。\n可仅转写后复制给 ChatGPT，手动使用已有订阅。"))
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    TextEditor(text: $model.summaryDraft).font(.body)
                        .disabled(model.isBusy).border(.quaternary)
                        .overlay(alignment: .topLeading) {
                            if model.summaryDraft.isEmpty {
                                Text("点击“生成纪要”，或在这里输入、粘贴纪要草稿。")
                                    .foregroundStyle(.secondary).padding(8).allowsHitTesting(false)
                            }
                        }
                    HStack {
                        Text("AI 草稿可编辑；请核对事实和来源。选择“保存修改”后保留修改。")
                            .font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        Button("保存修改") { model.saveEdits() }
                            .disabled(model.isBusy || model.summaryDraft == model.document?.summary)
                    }
                }
            } else {
                ScrollView {
                    Text(model.document?.transcript ?? "完成转写后会在这里显示原文。")
                        .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(12)
                }.frame(maxWidth: .infinity, maxHeight: .infinity).border(.quaternary)
                Text("[S编号] 对应最多 5 分钟的音频范围，非逐句时间戳；首版不区分说话人。")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Divider()
            HStack(spacing: 10) {
                if model.isBusy { ProgressView().controlSize(.small) }
                Text(model.message).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                if model.isBusy {
                    Button("取消处理") { model.cancel() }
                } else {
                    if model.source != nil {
                        Menu("更多") {
                            Button("重新转写（备份旧纪要）") { model.start(transcribeOnly: true, restart: true) }
                        }.fixedSize()
                    }
                    Button(model.document?.transcriptComplete == true ? "转写已完成" : "仅转写 / 继续") { model.start(transcribeOnly: true) }
                        .disabled(model.source == nil || model.document?.transcriptComplete == true)
                    Button(model.document?.summary.isEmpty == false ? "重新生成纪要" : "生成纪要") { model.start() }
                        .buttonStyle(.borderedProminent).disabled(model.source == nil)
                }
            }
        }.padding(22).frame(minWidth: 850, idealWidth: 950, minHeight: 680, idealHeight: 780)
            .interactiveDismissDisabled(model.hasPendingWork || model.hasUnprotectedEdits)
    }
}
