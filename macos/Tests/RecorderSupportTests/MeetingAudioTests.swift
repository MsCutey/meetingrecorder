import AVFoundation
import CoreVideo
import Foundation
import Testing
@testable import RecorderSupport

@Test func meetingAudioSegmentsAreContiguousAndBounded() throws {
    let cases: [(Double, [MeetingAudioRange])] = [
        (0.25, [.init(start: 0, end: 0.25)]),
        (300, [.init(start: 0, end: 300)]),
        (300.25, [.init(start: 0, end: 300), .init(start: 300, end: 300.25)]),
        (600, [.init(start: 0, end: 300), .init(start: 300, end: 600)])
    ]
    for (duration, chunks) in cases {
        let plan = try MeetingAudio.makePlan(duration: duration)
        #expect(plan.duration == duration)
        #expect(plan.chunks == chunks)
    }
    let longest = try MeetingAudio.makePlan(duration: 28_800)
    #expect(longest.chunks.count == 96)
    #expect(longest.chunks.last?.end == 28_800)
    #expect(throws: MeetingAudioError.recordingTooLong) { try MeetingAudio.makePlan(duration: 28_801) }
    for duration in [0, -1, Double.nan, .infinity, -.infinity] {
        #expect(throws: MeetingAudioError.invalidDuration) { try MeetingAudio.makePlan(duration: duration) }
    }
}

@Test func meetingAudioMixesTracksAndExcludesVideo() async throws {
    let directory = try audioTestDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let first = directory.appendingPathComponent("microphone.wav")
    let second = directory.appendingPathComponent("system.wav")
    let video = directory.appendingPathComponent("screen.mov")
    try writeTestTone(to: first, duration: 2, frequency: 440)
    try writeTestTone(to: second, duration: 2, frequency: 880)
    try await writeSilentTestVideo(to: video, duration: 2)
    let recording = directory.appendingPathComponent("recording.mov")
    try await combineTestTracks([first, second, video], to: recording)
    let original = try Data(contentsOf: recording)

    let plan = try await MeetingAudio.plan(for: recording)
    #expect(abs(plan.duration - 2) < 0.05)
    #expect(plan.chunks.count == 1)
    let destination = directory.appendingPathComponent("chunk.m4a")
    try await MeetingAudio.export(source: recording, range: .init(start: 0.5, end: 1.5), to: destination)

    let output = AVURLAsset(url: destination)
    let audio = try await output.loadTracks(withMediaType: .audio)
    let videos = try await output.loadTracks(withMediaType: .video)
    let duration = try await output.load(.duration)
    #expect(audio.count == 1)
    #expect(videos.isEmpty)
    #expect(abs(duration.seconds - 1) < 0.1)
    #expect(try Data(contentsOf: recording) == original)
    #expect(try destination.resourceValues(forKeys: [.fileSizeKey]).fileSize! < 24_000_000)
    // Both synthesized tones must survive the audio mix, rather than selecting only one track.
    let file = try AVAudioFile(forReading: destination)
    let buffer = try #require(AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)))
    try file.read(into: buffer)
    #expect(toneAmplitude(in: buffer, frequency: 440) > 0.03)
    #expect(toneAmplitude(in: buffer, frequency: 880) > 0.03)
    #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path).allSatisfy { !$0.hasPrefix(".meeting-audio-") })
}

@Test func meetingAudioRejectsVideoWithoutAudio() async throws {
    let directory = try audioTestDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let source = directory.appendingPathComponent("silent.mov")
    try await writeSilentTestVideo(to: source, duration: 1)
    await #expect(throws: MeetingAudioError.noAudioTrack) { try await MeetingAudio.plan(for: source) }
    await #expect(throws: MeetingAudioError.noAudioTrack) {
        try await MeetingAudio.export(source: source, range: .init(start: 0, end: 1), to: directory.appendingPathComponent("chunk.m4a"))
    }
}

@Test func meetingAudioRejectsInvalidRangesAndExistingFiles() async throws {
    let directory = try audioTestDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let source = directory.appendingPathComponent("source.wav")
    let destination = directory.appendingPathComponent("chunk.m4a")
    try writeTestTone(to: source, duration: 2)
    let invalid: [MeetingAudioRange] = [
        .init(start: -1, end: 1), .init(start: 1, end: 1), .init(start: 2, end: 1),
        .init(start: .nan, end: 1), .init(start: 0, end: .infinity),
        .init(start: 0, end: 301), .init(start: 0, end: 3)]
    for range in invalid {
        await #expect(throws: MeetingAudioError.invalidRange) {
            try await MeetingAudio.export(source: source, range: range, to: destination)
        }
    }
    #expect(!FileManager.default.fileExists(atPath: destination.path))
    let original = try Data(contentsOf: source)
    await #expect(throws: MeetingAudioError.destinationExists) {
        try await MeetingAudio.export(source: source, range: .init(start: 0, end: 1), to: source)
    }
    try Data("existing output".utf8).write(to: destination)
    await #expect(throws: MeetingAudioError.destinationExists) {
        try await MeetingAudio.export(source: source, range: .init(start: 0, end: 1), to: destination)
    }
    #expect(try String(contentsOf: destination, encoding: .utf8) == "existing output")
    #expect(try Data(contentsOf: source) == original)
}

@Test func meetingAudioCancellationRemovesPartialExport() async throws {
    let directory = try audioTestDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let source = directory.appendingPathComponent("source.wav")
    let destination = directory.appendingPathComponent("chunk.m4a")
    try writeTestTone(to: source, duration: 300)
    let export = Task {
        try await MeetingAudio.export(source: source, range: .init(start: 0, end: 300), to: destination)
    }
    // Wait for AVFoundation to start writing, so this exercises in-flight cancellation.
    var sawPartialExport = false
    for _ in 0..<1_000 {
        if try FileManager.default.contentsOfDirectory(atPath: directory.path).contains(where: { $0.hasPrefix(".meeting-audio-") }) {
            sawPartialExport = true
            break
        }
        try await Task.sleep(for: .milliseconds(2))
    }
    export.cancel()
    await #expect(throws: CancellationError.self) { try await export.value }
    #expect(sawPartialExport)
    #expect(!FileManager.default.fileExists(atPath: destination.path))
    #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path) == ["source.wav"])
}

@Test func meetingAudioHonorsCancellationBeforeStarting() async throws {
    let directory = try audioTestDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let destination = directory.appendingPathComponent("chunk.m4a")
    let export = Task {
        withUnsafeCurrentTask { $0?.cancel() }
        try await MeetingAudio.export(source: directory.appendingPathComponent("missing.wav"), range: .init(start: 0, end: 1), to: destination)
    }
    await #expect(throws: CancellationError.self) { try await export.value }
    #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path).isEmpty)
}

@Test func videoTailWithoutAudioIsCheckpointedWithoutAnUpload() async throws {
    let directory = try audioTestDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let tone = directory.appendingPathComponent("speech.wav")
    let video = directory.appendingPathComponent("screen.mov")
    let recording = directory.appendingPathComponent("recording.mov")
    try writeTestTone(to: tone, duration: 1)
    try await writeSilentTestVideo(to: video, duration: 301)
    try await combineTestTracks([tone, video], to: recording)
    var calls = 0
    var processor = MeetingNotesProcessor()
    processor.recognize = { _, _, _ in calls += 1; return "第一秒的发言。" }
    let result = try await processor.transcribe(source: recording,
        configuration: .init(baseURL: "https://speech.example/v1", model: "fixture"), apiKey: "fixture-key", progress: { _ in })
    #expect(calls == 1)
    #expect(result.transcriptComplete)
    #expect(result.hasSpeech)
    #expect(result.segments.count == 2)
    #expect(result.segments.last?.start == 300)
    #expect(result.segments.last?.end == 301)
    #expect(result.segments.last?.text == "")
    #expect(try MeetingNotesStore.load(for: recording)?.transcriptComplete == true)
}

private func audioTestDirectory() throws -> URL {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent("MeetingAudioTests-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    return directory
}

private func writeTestTone(to url: URL, duration: Double, frequency: Double = 440) throws {
    let format = try #require(AVAudioFormat(standardFormatWithSampleRate: 16_000, channels: 1))
    let file = try AVAudioFile(forWriting: url, settings: format.settings)
    let buffer = try #require(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 16_000))
    let samples = try #require(buffer.floatChannelData?[0])
    let frameCount = Int(duration * 16_000)
    var offset = 0
    while offset < frameCount {
        let count = min(16_000, frameCount - offset)
        buffer.frameLength = AVAudioFrameCount(count)
        for frame in 0..<count {
            samples[frame] = Float(0.25 * sin(2 * .pi * frequency * Double(offset + frame) / 16_000))
        }
        try file.write(from: buffer)
        offset += count
    }
}

private func toneAmplitude(in buffer: AVAudioPCMBuffer, frequency: Double) -> Double {
    guard let samples = buffer.floatChannelData?[0], buffer.frameLength > 0 else { return 0 }
    let count = Int(buffer.frameLength)
    var sine: Double = 0
    var cosine: Double = 0
    for index in 0..<count {
        let angle = 2 * .pi * frequency * Double(index) / buffer.format.sampleRate
        sine += Double(samples[index]) * sin(angle)
        cosine += Double(samples[index]) * cos(angle)
    }
    return 2 * hypot(sine, cosine) / Double(count)
}

private func writeSilentTestVideo(to url: URL, duration: Double) async throws {
    var pixelBuffer: CVPixelBuffer?
    let status = CVPixelBufferCreate(kCFAllocatorDefault, 16, 16, kCVPixelFormatType_32ARGB, nil, &pixelBuffer)
    #expect(status == kCVReturnSuccess)
    let pixels = try #require(pixelBuffer)
    CVPixelBufferLockBaseAddress(pixels, [])
    memset(CVPixelBufferGetBaseAddress(pixels), 0, CVPixelBufferGetDataSize(pixels))
    CVPixelBufferUnlockBaseAddress(pixels, [])
    var description: CMVideoFormatDescription?
    #expect(CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pixels, formatDescriptionOut: &description) == noErr)
    let format = try #require(description)
    let writer = try AVAssetWriter(outputURL: url, fileType: .mov)
    // Raw frames avoid hardware encoder access, keeping this test independent of capture permissions.
    let input = AVAssetWriterInput(mediaType: .video, outputSettings: nil, sourceFormatHint: format)
    writer.add(input)
    try #require(writer.startWriting())
    writer.startSession(atSourceTime: .zero)
    for second in [0.0, duration / 2] {
        while !input.isReadyForMoreMediaData { try await Task.sleep(for: .milliseconds(1)) }
        var timing = CMSampleTimingInfo(duration: CMTime(seconds: duration / 2, preferredTimescale: 600), presentationTimeStamp: CMTime(seconds: second, preferredTimescale: 600), decodeTimeStamp: .invalid)
        var sample: CMSampleBuffer?
        #expect(CMSampleBufferCreateReadyWithImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pixels, formatDescription: format, sampleTiming: &timing, sampleBufferOut: &sample) == noErr)
        try #require(input.append(try #require(sample)))
    }
    writer.endSession(atSourceTime: CMTime(seconds: duration, preferredTimescale: 600))
    input.markAsFinished()
    await writer.finishWriting()
    #expect(writer.status == .completed)
    if let error = writer.error { throw error }
}

private func combineTestTracks(_ sources: [URL], to destination: URL) async throws {
    let composition = AVMutableComposition()
    for source in sources {
        let asset = AVURLAsset(url: source)
        for track in try await asset.load(.tracks) {
            let target = try #require(composition.addMutableTrack(withMediaType: track.mediaType, preferredTrackID: kCMPersistentTrackID_Invalid))
            try await target.insertTimeRange(track.load(.timeRange), of: track, at: .zero)
        }
    }
    let export = try #require(AVAssetExportSession(asset: composition, presetName: AVAssetExportPresetPassthrough))
    try await export.export(to: destination, as: .mov)
}
