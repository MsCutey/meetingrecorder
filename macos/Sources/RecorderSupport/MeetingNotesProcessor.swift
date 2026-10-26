import Foundation

/// One user-initiated job at a time; completed transcription chunks are checkpoints.
public struct MeetingNotesProcessor {
    var plan: (URL) async throws -> MeetingAudioPlan
    var export: (URL, MeetingAudioRange, URL) async throws -> Void
    var recognize: (URL, AIServiceConfiguration, String) async throws -> String
    var summarizeText: (String, AIServiceConfiguration, String) async throws -> String

    public init(client: MeetingAIClient = MeetingAIClient()) {
        plan = { try await MeetingAudio.plan(for: $0) }
        export = { try await MeetingAudio.export(source: $0, range: $1, to: $2) }
        recognize = { try await client.transcribe(file: $0, configuration: $1, apiKey: $2) }
        summarizeText = { try await client.summarize(transcript: $0, configuration: $1, apiKey: $2) }
    }

    public func transcribe(source: URL, configuration: AIServiceConfiguration, apiKey: String,
                           restart: Bool = false, progress: @escaping (String) async -> Void) async throws -> MeetingNotesDocument {
        try Task.checkCancellation()
        _ = try configuration.validatedURL()
        await progress("正在读取音轨…")
        let audio = try await plan(source)
        var document: MeetingNotesDocument
        let existing = restart ? nil : try MeetingNotesStore.load(for: source)
        if let existing {
            guard try existing.matches(source: source), existing.transcriptionService == configuration,
                  existing.duration == audio.duration, existing.segments.count <= audio.chunks.count,
                  existing.segments.enumerated().allSatisfy({ index, segment in
                      segment.start == audio.chunks[index].start && segment.end == audio.chunks[index].end
                  }) else {
                throw MeetingNotesError("录音或转写服务已变化，请使用“重新转写”。旧纪要会先备份。")
            }
            document = existing
            if document.transcriptComplete { return document }
        } else {
            document = try MeetingNotesDocument(source: source, duration: audio.duration, transcriptionService: configuration)
            let notesURL = MeetingNotesStore.url(for: source)
            if restart && FileManager.default.fileExists(atPath: notesURL.path) {
                try FileManager.default.copyItem(at: notesURL,
                    to: notesURL.deletingPathExtension().appendingPathExtension("backup-\(UUID().uuidString).json"))
            }
        }
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("MeetingRecorder-Notes-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try Task.checkCancellation()
        try MeetingNotesStore.prepareToSave(document, for: source)
        for index in document.segments.count..<audio.chunks.count {
            try Task.checkCancellation()
            let range = audio.chunks[index]
            await progress("正在准备音频 \(index + 1)/\(audio.chunks.count)…")
            let file = directory.appendingPathComponent("chunk.m4a")
            let hasAudio: Bool
            do {
                try await export(source, range, file)
                hasAudio = true
            } catch MeetingAudioError.noAudioInRange {
                // A video can extend beyond its audio track or begin before audio starts.
                // Such a range is valid silence and must not block later speech.
                hasAudio = false
            }
            try Task.checkCancellation()
            guard try document.matches(source: source) else { throw MeetingNotesError("处理期间录音已变化，请重新转写。") }
            let text: String
            if hasAudio {
                await progress("正在转写 \(index + 1)/\(audio.chunks.count)…")
                text = try await recognize(file, configuration, apiKey)
            } else {
                await progress("音频 \(index + 1)/\(audio.chunks.count) 无音轨，已跳过上传。")
                text = ""
            }
            try Task.checkCancellation()
            // Reject a replaced or edited recording before checkpointing its transcript.
            guard try document.matches(source: source) else { throw MeetingNotesError("处理期间录音已变化，请重新转写。") }
            document.segments.append(MeetingTranscriptSegment(id: index + 1, start: range.start, end: range.end,
                                                               text: text.trimmingCharacters(in: .whitespacesAndNewlines)))
            document.transcriptComplete = index == audio.chunks.count - 1
            document.updatedAt = Date()
            try MeetingNotesStore.save(document, for: source)
            if hasAudio { try FileManager.default.removeItem(at: file) }
        }
        return document
    }

    public func summarize(source: URL, configuration: AIServiceConfiguration, apiKey: String) async throws -> MeetingNotesDocument {
        try await summarize(source: source, configuration: configuration) { transcript in
            try await summarizeText(transcript, configuration, apiKey)
        }
    }

    /// Alternate providers share the same checkpoint and draft-preservation rules without
    /// passing their credentials through the API-key chat-completions client.
    public func summarize(source: URL, configuration: AIServiceConfiguration,
                          generate: @escaping (String) async throws -> String) async throws -> MeetingNotesDocument {
        try Task.checkCancellation()
        guard var document = try MeetingNotesStore.load(for: source), document.transcriptComplete,
              try document.matches(source: source) else {
            throw MeetingNotesError("请先完成当前录音的转写。")
        }
        guard document.hasSpeech else { throw MeetingNotesError("未识别到可总结的文字，请检查录音。") }
        try MeetingNotesStore.prepareToSave(document, for: source)
        let summary = try await generate(document.transcript)
        try Task.checkCancellation()
        guard try document.matches(source: source) else { throw MeetingNotesError("处理期间录音已变化，结果未覆盖旧纪要。") }
        document.summary = summary; document.summaryService = configuration; document.updatedAt = Date()
        // The old summary survives any failure or cancellation above.
        try MeetingNotesStore.save(document, for: source)
        return document
    }
}
