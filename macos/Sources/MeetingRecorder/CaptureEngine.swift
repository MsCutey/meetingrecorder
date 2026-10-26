import AppKit
import AVFoundation
@preconcurrency import ScreenCaptureKit
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

/// Resolves startup once both ScreenCaptureKit and the first encoded sample are ready.
/// Owned by CaptureEngine's serial queue; late callbacks cannot change a terminal result.
final class CaptureStartupGate {
    private var captureStarted = false
    private var mediaWritten = false
    private(set) var isResolved = false
    private let requiresVideo: Bool
    private let completion: (Result<Void, Error>) -> Void

    init(requiresVideo: Bool, completion: @escaping (Result<Void, Error>) -> Void) {
        self.requiresVideo = requiresVideo
        self.completion = completion
    }
    func captureDidStart() { captureStarted = true; resolveIfReady() }
    func mediaWasWritten(isVideo: Bool) {
        guard !requiresVideo || isVideo else { return }
        mediaWritten = true
        resolveIfReady()
    }
    func fail(_ error: Error) { resolve(.failure(error)) }
    private func resolveIfReady() {
        if captureStarted && mediaWritten { resolve(.success(())) }
    }
    private func resolve(_ result: Result<Void, Error>) {
        guard !isResolved else { return }
        isResolved = true
        completion(result)
    }
}

/// All writer state, including startup, pause and mute, is confined to a serial queue.
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
    private var isStarting = false
    private var isStopping = false
    private var captureStoppedWithError = false
    private var startCancellationRequested = false
    private var startup: CaptureStartupGate?
    private var startupTimeout: DispatchWorkItem?
    private var videoAvailable: Bool?
    private var ownsTemporaryFiles = false
    var capturedAudioPeak: Double { queue.sync { sourcePeak } }
    private(set) var partial: URL?
    var onFailure: (@Sendable (String) -> Void)?
    var onVideoAvailability: (@Sendable (Bool) -> Void)?

    func start(_ options: CaptureOptions) async throws {
        try await withTaskCancellationHandler {
            do {
                try Task.checkCancellation()
                let stream = try queue.sync { try prepare(options) }
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                    queue.async { [self] in
                        guard !startCancellationRequested else {
                            continuation.resume(throwing: CancellationError())
                            return
                        }
                        startup = CaptureStartupGate(requiresVideo: !options.audioOnly) { [weak self] result in
                            self?.startupTimeout?.cancel()
                            self?.startupTimeout = nil
                            continuation.resume(with: result)
                        }
                        let timeout = DispatchWorkItem { [weak self] in
                            guard let self, startup?.isResolved == false else { return }
                            recordFailure(RecorderError(options.audioOnly
                                ? "10 秒内没有收到可写入的声音，请检查声音来源和权限。"
                                : "10 秒内没有收到可写入的画面。请确认目标窗口未最小化，或重新选择录制窗口。"))
                        }
                        startupTimeout = timeout
                        queue.asyncAfter(deadline: .now() + 10, execute: timeout)
                        accepting = true
                        stream.startCapture { [weak self, stream] error in
                            guard let self else {
                                if error == nil { stream.stopCapture { _ in } }
                                return
                            }
                            self.queue.async { [self] in
                                // A cancellation/timeout may have already released this attempt.
                                guard self.stream === stream, self.isStarting || self.accepting else {
                                    if error == nil { stream.stopCapture { _ in } }
                                    return
                                }
                                if let error { self.recordFailure(error) }
                                else { self.startup?.captureDidStart() }
                            }
                        }
                    }
                }
                try Task.checkCancellation()
                try queue.sync {
                    if startCancellationRequested { throw CancellationError() }
                    if let failure { throw failure }
                    isStarting = false
                    startup = nil
                }
            } catch {
                await cleanUpFailedStart()
                throw error
            }
        } onCancel: {
            self.cancelStart()
        }
    }

    /// May be called while configuration or startCapture is still pending.
    func cancelStart() {
        queue.async { [self] in
            guard isStarting || writer == nil else { return }
            startCancellationRequested = true
            if isStarting { recordFailure(CancellationError()) }
        }
    }

    private func prepare(_ options: CaptureOptions) throws -> SCStream {
        dispatchPrecondition(condition: .onQueue(queue))
        guard !startCancellationRequested else { throw CancellationError() }
        guard writer == nil, stream == nil else { throw RecorderError("这次录制已启动。") }
        isStarting = true
        let partial = options.output.deletingPathExtension().appendingPathExtension("partial.mov")
        let metadata = partial.appendingPathExtension("json")
        guard !FileManager.default.fileExists(atPath: partial.path),
              !FileManager.default.fileExists(atPath: metadata.path) else {
            throw RecorderError("临时录制文件已存在，请稍后重试，原文件已保留。")
        }
        self.partial = partial
        let info = RecoveryInfo(destination: options.output, audioOnly: options.audioOnly, created: Date())
        try JSONEncoder().encode(info).write(to: metadata, options: .atomic)
        ownsTemporaryFiles = true
        let writer = try AVAssetWriter(outputURL: partial, fileType: .mov)
        self.writer = writer
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
        let configuration = SCStreamConfiguration()
        configuration.width = options.audioOnly ? 2 : width
        configuration.height = options.audioOnly ? 2 : height
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: options.audioOnly ? 1 : 30)
        configuration.queueDepth = 3
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.captureResolution = .best
        configuration.ignoreShadowsSingleWindow = true
        configuration.preservesAspectRatio = true
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
        self.stream = stream
        muted = options.startMuted
        return stream
    }

    private func cleanUpFailedStart() async {
        let finishWriter: AVAssetWriter? = queue.sync {
            accepting = false
            isStarting = false
            startupTimeout?.cancel(); startupTimeout = nil
            startup = nil
            let stream = self.stream
            self.stream = nil
            // Do not let an unresponsive startCapture block timeout/cancellation cleanup.
            stream?.stopCapture { [stream] _ in _ = stream }
            let hasContent = video != nil ? frameCount > 0 : audioCount > 0
            if hasContent, let writer, writer.status == .writing {
                finishInputs(at: timeline?.position(at: CMClockGetTime(CMClockGetHostTimeClock())) ?? .zero)
                return writer.status == .writing ? writer : nil
            }
            if !hasContent {
                if writer?.status == .writing || writer?.status == .unknown { writer?.cancelWriting() }
                if ownsTemporaryFiles, let partial {
                    try? FileManager.default.removeItem(at: partial)
                    try? FileManager.default.removeItem(at: partial.appendingPathExtension("json"))
                    ownsTemporaryFiles = false
                }
            }
            return nil
        }
        // Preserve media if startup failed after the first successful append.
        if let finishWriter { await finishWriter.finishWriting() }
    }

    private func recordFailure(_ error: Error) {
        dispatchPrecondition(condition: .onQueue(queue))
        guard failure == nil else { return }
        failure = error
        accepting = false
        startup?.fail(error)
        if !isStarting { onFailure?(error.localizedDescription) }
    }

    func pause() { queue.sync { timeline?.pause(at: CMClockGetTime(CMClockGetHostTimeClock())) } }
    func resume() { queue.sync { timeline?.resume(at: CMClockGetTime(CMClockGetHostTimeClock())) } }
    func setMuted(_ value: Bool) { queue.sync { muted = value } }
    var elapsed: Double {
        queue.sync { timeline?.position(at: CMClockGetTime(CMClockGetHostTimeClock())).seconds ?? 0 }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        queue.async { [self] in
            guard self.stream === stream else { return }
            captureStoppedWithError = true
            recordFailure(error)
        }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sample: CMSampleBuffer,
                of type: SCStreamOutputType) {
        guard self.stream === stream, accepting else { return }
        if type == .screen {
            guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false)
                    as? [[SCStreamFrameInfo: Any]],
                  let status = attachments.first?[.status] as? Int else { return }
            if status == SCFrameStatus.suspended.rawValue {
                if videoAvailable != false { videoAvailable = false; onVideoAvailability?(false) }
                return
            }
            guard status == SCFrameStatus.complete.rawValue, sample.isValid,
                  CMSampleBufferDataIsReady(sample), sample.imageBuffer != nil else { return }
            if videoAvailable != true { videoAvailable = true; onVideoAvailability?(true) }
        }
        guard sample.isValid, CMSampleBufferDataIsReady(sample), sample.presentationTimeStamp.isNumeric else { return }
        let input: AVAssetWriterInput?
        switch type {
        case .screen: input = video
        case .audio: input = system
        case .microphone: input = microphone
        @unknown default: return
        }
        guard let input, let writer else { return }
        if video != nil, frameCount == 0, type != .screen { return }
        guard writer.status == .writing else {
            if writer.status == .failed { recordFailure(writer.error ?? RecorderError("音视频编码器已停止。")) }
            return
        }
        // A video recording has no audio pre-roll or blank lead-in while its window is unavailable.
        if timeline == nil {
            guard video == nil || type == .screen else { return }
            timeline = RecordingTimeline(origin: sample.presentationTimeStamp)
            writer.startSession(atSourceTime: .zero)
        }
        guard input.isReadyForMoreMediaData else { return }
        guard let timestamp = timeline?.timestamp(for: sample.presentationTimeStamp),
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
            if !input.append(adjusted) { throw writer.error ?? RecorderError("音视频写入失败。") }
            lastTimes[type] = timestamp
            if type == .screen { frameCount += 1; lastFrame = sample } else { audioCount += 1 }
            startup?.mediaWasWritten(isVideo: type == .screen)
        } catch { recordFailure(error) }
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

    private func finishInputs(at end: CMTime) {
        dispatchPrecondition(condition: .onQueue(queue))
        guard let writer, writer.status == .writing, timeline != nil else { return }
        if let video, let lastFrame, video.isReadyForMoreMediaData,
           end > (lastTimes[.screen] ?? .zero), let final = try? retimed(lastFrame, to: end) {
            if !video.append(final), failure == nil { failure = writer.error ?? RecorderError("录制末帧写入失败。") }
        }
        guard writer.status == .writing else { return }
        video?.markAsFinished(); system?.markAsFinished(); microphone?.markAsFinished()
        writer.endSession(atSourceTime: end)
    }

    func stop() async throws -> URL {
        let state: (SCStream?, AVAssetWriter, URL, CMTime) = try queue.sync {
            guard !isStarting, !isStopping, let writer, let partial else { throw RecorderError("没有已启动的录制。") }
            isStopping = true
            accepting = false
            let end = timeline?.position(at: CMClockGetTime(CMClockGetHostTimeClock())) ?? .zero
            let stream = captureStoppedWithError ? nil : self.stream
            self.stream = nil
            return (stream, writer, partial, end)
        }
        var captureError: Error?
        do { try await state.0?.stopCapture() } catch { captureError = error }
        let shouldFinish: Bool = queue.sync {
            guard state.1.status == .writing, timeline != nil else { return false }
            finishInputs(at: state.3)
            return state.1.status == .writing
        }
        if shouldFinish { await state.1.finishWriting() }
        let writeError = queue.sync { failure ?? state.1.error }
        if let writeError { throw writeError }
        guard state.1.status == .completed else {
            throw captureError ?? RecorderError("录制收尾失败，临时文件已保留。")
        }
        return state.2
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
