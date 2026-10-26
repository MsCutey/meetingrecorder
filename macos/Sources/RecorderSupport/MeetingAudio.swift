import AVFoundation
import Foundation

public struct MeetingAudioRange: Codable, Equatable, Sendable {
    public let start: Double
    public let end: Double

    public init(start: Double, end: Double) {
        self.start = start
        self.end = end
    }
}

public struct MeetingAudioPlan: Sendable {
    public let duration: Double
    public let chunks: [MeetingAudioRange]
}

public enum MeetingAudioError: LocalizedError, Equatable {
    case noAudioTrack
    case invalidDuration
    case recordingTooLong
    case invalidRange
    case noAudioInRange
    case destinationExists
    case exportUnavailable
    case emptyExport
    case exportTooLarge

    public var errorDescription: String? {
        switch self {
        case .noAudioTrack: return "录制文件不含音轨，无法生成纪要。"
        case .invalidDuration: return "录制文件时长为空或无效。"
        case .recordingTooLong: return "暂时支持最长 8 小时的录制文件。"
        case .invalidRange: return "音频分段范围无效或超过 5 分钟。"
        case .noAudioInRange: return "该时间段没有可识别的音频。"
        case .destinationExists: return "音频输出文件已存在，已停止操作以保护原文件。"
        case .exportUnavailable: return "无法创建音频导出任务。"
        case .emptyExport: return "导出的音频文件为空。"
        case .exportTooLarge: return "音频分段超过 24 MB，无法上传识别。"
        }
    }
}

/// Prepares local audio only. Network requests and the original recording are never modified here.
public enum MeetingAudio {
    static let maximumChunkDuration: Double = 300
    static let maximumRecordingDuration: Double = 8 * 60 * 60
    static let maximumExportBytes = 24_000_000

    public static func plan(for source: URL) async throws -> MeetingAudioPlan {
        try Task.checkCancellation()
        let asset = AVURLAsset(url: source)
        let audioTracks = try await asset.loadTracks(withMediaType: .audio)
        guard !audioTracks.isEmpty else { throw MeetingAudioError.noAudioTrack }
        let duration = try await asset.load(.duration)
        try Task.checkCancellation()
        return try makePlan(duration: duration.seconds)
    }

    static func makePlan(duration: Double) throws -> MeetingAudioPlan {
        guard duration.isFinite, duration > 0 else { throw MeetingAudioError.invalidDuration }
        guard duration <= maximumRecordingDuration else { throw MeetingAudioError.recordingTooLong }
        var chunks: [MeetingAudioRange] = []
        var start: Double = 0
        while start < duration {
            let end = min(start + maximumChunkDuration, duration)
            chunks.append(MeetingAudioRange(start: start, end: end))
            start = end
        }
        return MeetingAudioPlan(duration: duration, chunks: chunks)
    }

    public static func export(source: URL, range: MeetingAudioRange, to destination: URL) async throws {
        try Task.checkCancellation()
        let files = FileManager.default
        guard source.standardizedFileURL.resolvingSymlinksInPath() != destination.standardizedFileURL.resolvingSymlinksInPath(),
              !files.fileExists(atPath: destination.path) else {
            throw MeetingAudioError.destinationExists
        }
        guard range.start.isFinite, range.end.isFinite,
              range.start >= 0, range.end > range.start,
              range.end - range.start <= maximumChunkDuration else {
            throw MeetingAudioError.invalidRange
        }

        let asset = AVURLAsset(url: source)
        let audioTracks = try await asset.loadTracks(withMediaType: .audio)
        guard !audioTracks.isEmpty else { throw MeetingAudioError.noAudioTrack }
        let duration = try await asset.load(.duration)
        _ = try makePlan(duration: duration.seconds)
        guard range.end <= duration.seconds else { throw MeetingAudioError.invalidRange }
        let requested = CMTimeRange(
            start: CMTime(seconds: range.start, preferredTimescale: 60_000),
            end: CMTime(seconds: range.end, preferredTimescale: 60_000)
        )
        guard requested.duration > .zero else { throw MeetingAudioError.invalidRange }

        // Building an audio-only composition ensures video cannot enter the upload payload.
        let composition = AVMutableComposition()
        var mixInputs: [AVMutableAudioMixInputParameters] = []
        for track in audioTracks {
            try Task.checkCancellation()
            let available = try await track.load(.timeRange)
            let intersection = CMTimeRangeGetIntersection(available, otherRange: requested)
            guard intersection.isValid, !intersection.isEmpty else { continue }
            guard let mixedTrack = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid) else {
                throw MeetingAudioError.exportUnavailable
            }
            try mixedTrack.insertTimeRange(intersection, of: track, at: intersection.start - requested.start)
            mixInputs.append(AVMutableAudioMixInputParameters(track: mixedTrack))
        }
        guard !mixInputs.isEmpty else { throw MeetingAudioError.noAudioInRange }
        // Keep simultaneous microphone and system audio within the mix's amplitude range.
        let volume = 1 / Float(mixInputs.count)
        for input in mixInputs { input.setVolume(volume, at: .zero) }
        let mix = AVMutableAudioMix()
        mix.inputParameters = mixInputs

        guard let session = AVAssetExportSession(asset: composition, presetName: AVAssetExportPresetAppleM4A) else {
            throw MeetingAudioError.exportUnavailable
        }
        session.audioMix = mix
        // Use our own temporary file so cancellation/failure can never remove an existing file.
        let temporary = destination.deletingLastPathComponent()
            .appendingPathComponent(".meeting-audio-\(UUID().uuidString).m4a")
        defer { try? files.removeItem(at: temporary) }
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            do {
                try await session.export(to: temporary, as: .m4a)
            } catch {
                if Task.isCancelled { throw CancellationError() }
                throw error
            }
            try Task.checkCancellation()
            let size = try temporary.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
            guard size > 0 else { throw MeetingAudioError.emptyExport }
            guard size < maximumExportBytes else { throw MeetingAudioError.exportTooLarge }
            // moveItem refuses to replace a destination created while the export was running.
            try files.moveItem(at: temporary, to: destination)
            if Task.isCancelled {
                try? files.removeItem(at: destination)
                throw CancellationError()
            }
        } onCancel: {
            session.cancelExport()
        }
    }
}
