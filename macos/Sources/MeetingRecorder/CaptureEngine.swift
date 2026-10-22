import AppKit
import AVFoundation
import ScreenCaptureKit
import RecorderSupport

struct RecorderError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

struct CaptureOptions {
    var audioOnly = false
    var systemAudio = true
    var microphone = false
    var microphoneID: String?
    var startMuted = false
    var maxHeight = 1080
    var excludeApp = true
    var filter: SCContentFilter
    var width: Int
    var height: Int
    var output: URL
}

struct RecoveryInfo: Codable {
    let destination: URL
    let audioOnly: Bool
    let created: Date
}

/// All writer state, including pause and mute, is confined to a serial queue.
final class CaptureEngine: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    private let queue = DispatchQueue(label: "com.mscutey.meetingrecorder.capture")
    private var stream: SCStream?
    private var writer: AVAssetWriter?
    private var video: AVAssetWriterInput?
    private var system: AVAssetWriterInput?
    private var microphone: AVAssetWriterInput?
    private var timeline: RecordingTimeline?
    private var lastTimes: [SCStreamOutputType: CMTime] = [:]
    private var lastFrame: CMSampleBuffer?
    private var muted = false
    private var accepting = false
    private var failure: Error?
    private var frameCount = 0
    private var audioCount = 0
    private var sourcePeak = 0.0
    var capturedAudioPeak: Double { queue.sync { sourcePeak } }
    private(set) var partial: URL?
    var onFailure: (@Sendable (String) -> Void)?

    func start(_ options: CaptureOptions) async throws {
        let partial = options.output.deletingPathExtension().appendingPathExtension("partial.mov")
        self.partial = partial
        let info = RecoveryInfo(destination: options.output, audioOnly: options.audioOnly, created: Date())
        try JSONEncoder().encode(info).write(to: partial.appendingPathExtension("json"), options: .atomic)
        let writer = try AVAssetWriter(outputURL: partial, fileType: .mov)
        var started = false
        defer { if !started { writer.cancelWriting() } }
        // Fragmented MOV retains already completed fragments after an abnormal exit.
        writer.movieFragmentInterval = CMTime(seconds: 2, preferredTimescale: 600)
        let (width, height) = RecordingFiles.dimensions(width: options.width, height: options.height,
                                                        maxHeight: options.maxHeight)
        func add(_ input: AVAssetWriterInput) throws {
            input.expectsMediaDataInRealTime = true
            guard writer.canAdd(input) else { throw RecorderError("无法创建编码轨道。") }
            writer.add(input)
        }
        if !options.audioOnly {
            let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
                AVVideoCodecKey: AVVideoCodecType.h264,
                AVVideoWidthKey: width, AVVideoHeightKey: height,
                AVVideoCompressionPropertiesKey: [
                    AVVideoAverageBitRateKey: max(1_000_000, width * height * 4),
                    AVVideoExpectedSourceFrameRateKey: 30,
                    AVVideoMaxKeyFrameIntervalKey: 60
                ]
            ])
            try add(input)
            video = input
        }
        let audioSettings: [String: Any] = [AVFormatIDKey: kAudioFormatMPEG4AAC,
            AVSampleRateKey: 48000, AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 160000]
        if options.systemAudio {
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: audioSettings)
            try add(input); system = input
        }
        if options.microphone {
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: audioSettings)
            try add(input); microphone = input
        }
        guard writer.startWriting() else { throw writer.error ?? RecorderError("编码器启动失败。") }
        writer.startSession(atSourceTime: .zero)
        let configuration = SCStreamConfiguration()
        configuration.width = options.audioOnly ? 2 : width
        configuration.height = options.audioOnly ? 2 : height
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: options.audioOnly ? 1 : 30)
        configuration.queueDepth = 3
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.showsCursor = !options.audioOnly
        configuration.capturesAudio = options.systemAudio
        configuration.excludesCurrentProcessAudio = true
        configuration.sampleRate = 48000
        configuration.channelCount = 2
        configuration.captureMicrophone = options.microphone
        configuration.microphoneCaptureDeviceID = options.microphoneID
        let stream = SCStream(filter: options.filter, configuration: configuration, delegate: self)
        if !options.audioOnly { try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue) }
        if options.systemAudio { try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue) }
        if options.microphone { try stream.addStreamOutput(self, type: .microphone, sampleHandlerQueue: queue) }
        self.writer = writer
        self.stream = stream
        queue.sync {
            timeline = RecordingTimeline(origin: CMClockGetTime(CMClockGetHostTimeClock()))
            muted = options.startMuted
            accepting = true
        }
        do { try await stream.startCapture(); started = true }
        catch {
            queue.sync { accepting = false; writer.cancelWriting() }
            self.stream = nil
            throw error
        }
    }

    func pause() { queue.sync { timeline?.pause(at: CMClockGetTime(CMClockGetHostTimeClock())) } }
    func resume() { queue.sync { timeline?.resume(at: CMClockGetTime(CMClockGetHostTimeClock())) } }
    func setMuted(_ value: Bool) { queue.sync { muted = value } }
    var elapsed: Double {
        queue.sync { timeline?.position(at: CMClockGetTime(CMClockGetHostTimeClock())).seconds ?? 0 }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        onFailure?(error.localizedDescription)
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sample: CMSampleBuffer,
                of type: SCStreamOutputType) {
        guard accepting, sample.isValid, CMSampleBufferDataIsReady(sample),
              let timeline, let timestamp = timeline.timestamp(for: sample.presentationTimeStamp) else { return }
        if type == .screen {
            guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false)
                    as? [[SCStreamFrameInfo: Any]],
                  let status = attachments.first?[.status] as? Int,
                  status == SCFrameStatus.complete.rawValue else { return }
            lastFrame = sample
        }
        let input: AVAssetWriterInput?
        switch type {
        case .screen: input = video
        case .audio: input = system
        case .microphone: input = microphone
        @unknown default: return
        }
        guard let input, input.isReadyForMoreMediaData,
              lastTimes[type].map({ timestamp > $0 }) ?? true else { return }
        if type == .audio, let format = sample.formatDescription,
           let description = CMAudioFormatDescriptionGetStreamBasicDescription(format),
           description.pointee.mFormatFlags & kAudioFormatFlagIsFloat != 0,
           description.pointee.mBitsPerChannel == 32, let block = sample.dataBuffer {
            let size = CMBlockBufferGetDataLength(block)
            var values = [Float](repeating: 0, count: size / 4)
            let status = values.withUnsafeMutableBytes {
                CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: size, destination: $0.baseAddress!)
            }
            if status == noErr {
                for value in values where value.isFinite { sourcePeak = max(sourcePeak, Double(abs(value))) }
            }
        }
        do {
            // Write actual silence when muted, including a microphone-only muted session.
            let source = type == .microphone && muted ? try silentCopy(sample) : sample
            let adjusted = try retimed(source, to: timestamp)
            if !input.append(adjusted) {
                throw writer?.error ?? RecorderError("音视频写入失败。")
            }
            lastTimes[type] = timestamp
            if type == .screen { frameCount += 1 } else { audioCount += 1 }
        } catch {
            if failure == nil { failure = error; onFailure?(error.localizedDescription) }
        }
    }

    private func silentCopy(_ sample: CMSampleBuffer) throws -> CMSampleBuffer {
        guard let source = CMSampleBufferGetDataBuffer(sample), let format = sample.formatDescription else {
            throw RecorderError("无法静音麦克风采样。")
        }
        var data: CMBlockBuffer?
        var status = CMBlockBufferCreateContiguous(allocator: kCFAllocatorDefault, sourceBuffer: source,
            blockAllocator: kCFAllocatorDefault, customBlockSource: nil, offsetToData: 0,
            dataLength: 0, flags: kCMBlockBufferAlwaysCopyDataFlag, blockBufferOut: &data)
        guard status == noErr, let data else { throw RecorderError("无法创建静音缓冲区。") }
        status = CMBlockBufferFillDataBytes(with: 0, blockBuffer: data, offsetIntoDestination: 0,
                                           dataLength: CMBlockBufferGetDataLength(data))
        guard status == noErr else { throw RecorderError("无法清空麦克风缓冲区。") }
        var result: CMSampleBuffer?
        status = CMAudioSampleBufferCreateReadyWithPacketDescriptions(allocator: kCFAllocatorDefault,
            dataBuffer: data, formatDescription: format, sampleCount: sample.numSamples,
            presentationTimeStamp: sample.presentationTimeStamp, packetDescriptions: nil,
            sampleBufferOut: &result)
        guard status == noErr, let result else { throw RecorderError("创建静音采样失败。") }
        return result
    }

    private func retimed(_ sample: CMSampleBuffer, to timestamp: CMTime) throws -> CMSampleBuffer {
        var count = 0
        var status = CMSampleBufferGetSampleTimingInfoArray(sample, entryCount: 0,
                                                           arrayToFill: nil, entriesNeededOut: &count)
        guard status == noErr, count > 0 else { throw RecorderError("采样时间戳不可用。") }
        var timing = [CMSampleTimingInfo](repeating: CMSampleTimingInfo(), count: count)
        status = CMSampleBufferGetSampleTimingInfoArray(sample, entryCount: count,
                                                       arrayToFill: &timing, entriesNeededOut: &count)
        guard status == noErr else { throw RecorderError("读取采样时间戳失败。") }
        let offset = sample.presentationTimeStamp - timestamp
        for index in timing.indices {
            timing[index].presentationTimeStamp = timing[index].presentationTimeStamp - offset
            if timing[index].decodeTimeStamp.isValid {
                timing[index].decodeTimeStamp = timing[index].decodeTimeStamp - offset
            }
        }
        var copy: CMSampleBuffer?
        status = CMSampleBufferCreateCopyWithNewTiming(allocator: kCFAllocatorDefault,
            sampleBuffer: sample, sampleTimingEntryCount: count, sampleTimingArray: &timing,
            sampleBufferOut: &copy)
        guard status == noErr, let copy else { throw RecorderError("调整采样时间戳失败。") }
        return copy
    }

    func stop() async throws -> URL {
        // Freeze the end timestamp before waiting for ScreenCaptureKit to shut down.
        let end = queue.sync { () -> CMTime in
            accepting = false
            return timeline?.position(at: CMClockGetTime(CMClockGetHostTimeClock())) ?? .zero
        }
        var captureError: Error?
        do { try await stream?.stopCapture() } catch { captureError = error }
        stream = nil
        guard let writer, let partial else { throw RecorderError("没有活动录制。") }
        let writeError: Error? = queue.sync {
            if let video, let lastFrame, video.isReadyForMoreMediaData,
               end > (lastTimes[.screen] ?? .zero), let final = try? retimed(lastFrame, to: end) {
                if !video.append(final) { failure = writer.error }
            }
            video?.markAsFinished(); system?.markAsFinished(); microphone?.markAsFinished()
            writer.endSession(atSourceTime: end)
            if video != nil && frameCount == 0 { return RecorderError("没有收到画面，请检查屏幕录制权限或重新选择窗口。") }
            if video == nil && audioCount == 0 { return RecorderError("没有收到声音，请检查音频来源和权限。") }
            return failure
        }
        await writer.finishWriting()
        if let error = writeError ?? writer.error { throw error }
        guard writer.status == .completed else { throw captureError ?? RecorderError("录制收尾失败，临时文件已保留。") }
        return partial
    }
}

enum RecordingExporter {
    static func export(partial: URL, destination: URL, audioOnly: Bool) async throws -> URL {
        let asset = AVURLAsset(url: partial)
        let duration = try await asset.load(.duration)
        guard duration.isNumeric, duration.seconds > 0 else { throw RecorderError("临时文件还没有完整媒体片段。") }
        let composition = AVMutableComposition()
        let audioTracks = try await asset.loadTracks(withMediaType: .audio)
        let videoTracks = try await asset.loadTracks(withMediaType: .video)
        if audioOnly && audioTracks.isEmpty { throw RecorderError("录制中没有音轨。") }
        if !audioOnly && videoTracks.isEmpty { throw RecorderError("录制中没有视频轨道。") }
        var mixInputs: [AVMutableAudioMixInputParameters] = []
        for source in (audioOnly ? audioTracks : videoTracks + audioTracks) {
            guard let track = composition.addMutableTrack(withMediaType: source.mediaType,
                preferredTrackID: kCMPersistentTrackID_Invalid) else { throw RecorderError("无法创建导出轨道。") }
            let range = try await source.load(.timeRange)
            try track.insertTimeRange(range, of: source, at: range.start)
            if source.mediaType == .video { track.preferredTransform = try await source.load(.preferredTransform) }
            if source.mediaType == .audio {
                let parameters = AVMutableAudioMixInputParameters(track: track)
                parameters.setVolume(audioTracks.count > 1 ? 0.5 : 1, at: .zero)
                mixInputs.append(parameters)
            }
        }
        let preset = audioOnly ? AVAssetExportPresetAppleM4A : AVAssetExportPresetHighestQuality
        guard let exporter = AVAssetExportSession(asset: composition, presetName: preset) else {
            throw RecorderError("当前系统无法创建导出器。")
        }
        let mix = AVMutableAudioMix()
        mix.inputParameters = mixInputs
        if !mixInputs.isEmpty { exporter.audioMix = mix }
        let staging = destination.deletingPathExtension().appendingPathExtension("staging." + destination.pathExtension)
        // A previous failed export may exist, but never overwrite a finished recording.
        if FileManager.default.fileExists(atPath: destination.path) { throw RecorderError("目标文件已存在，请另选文件名。") }
        try? FileManager.default.removeItem(at: staging)
        try await exporter.export(to: staging, as: audioOnly ? .m4a : .mp4)
        let result = AVURLAsset(url: staging)
        guard try await result.load(.isPlayable), try await result.load(.duration).seconds > 0 else {
            throw RecorderError("导出文件验证失败，临时录制已保留。")
        }
        try FileManager.default.moveItem(at: staging, to: destination)
        try? FileManager.default.removeItem(at: partial)
        try? FileManager.default.removeItem(at: partial.appendingPathExtension("json"))
        return destination
    }
}
