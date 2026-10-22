import AppKit
import AVFoundation

/// Explicit developer mode; never records automatically during normal app launch.
@MainActor enum SmokeTest {
    static func run(_ model: RecorderModel) async {
        let arguments = CommandLine.arguments
        guard let index = arguments.firstIndex(of: "--smoke-test"), index + 1 < arguments.count else { return }
        let directory = URL(fileURLWithPath: arguments[index + 1], isDirectory: true)
        var report: [String: Any] = ["success": false, "system": ProcessInfo.processInfo.operatingSystemVersionString]
        let savedSettings = model.settings
        defer { model.settings = savedSettings }
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            model.settings.outputDirectory = directory.path
            model.settings.countdown = 0
            model.settings.audioOnly = arguments.contains("--audio-only")
            model.settings.systemAudio = !arguments.contains("--no-system-audio")
            model.settings.microphone = arguments.contains("--with-microphone")
            await model.refresh()
            if arguments.contains("--window") {
                let titleIndex = arguments.firstIndex(of: "--window-title")
                let title = titleIndex.flatMap { $0 + 1 < arguments.count ? arguments[$0 + 1] : nil }
                guard let window = model.targets.first(where: { target in
                    target.window != nil && (title.map { requested in
                        (target.window?.title ?? "").contains(requested)
                    } ?? true)
                }) else {
                    throw RecorderError("没有可供测试的窗口。")
                }
                model.sourceID = window.id
            }
            await model.start(preapproved: true)
            guard model.state == .recording else { throw RecorderError(model.message) }
            try await Task.sleep(for: .seconds(3))
            if arguments.contains("--pause") {
                model.pauseResume()
                try await Task.sleep(for: .seconds(2))
                model.pauseResume()
            }
            try await Task.sleep(for: .seconds(3))
            await model.stop()
            guard let output = model.lastOutput else { throw RecorderError(model.message) }
            let asset = AVURLAsset(url: output)
            let duration = try await asset.load(.duration).seconds
            let video = try await asset.loadTracks(withMediaType: .video)
            let audio = try await asset.loadTracks(withMediaType: .audio)
            guard duration > 4.5 && duration < 7.5 else { throw RecorderError("时长错误：\(duration)，预期约 6 秒。") }
            guard video.count == (model.settings.audioOnly ? 0 : 1) else { throw RecorderError("视频轨道数量错误。") }
            if model.settings.systemAudio || model.settings.microphone {
                guard audio.count == 1 else { throw RecorderError("混音成品应只有一条音轨。") }
            }
            report["output"] = output.path
            report["duration"] = duration
            report["videoTracks"] = video.count
            report["audioTracks"] = audio.count
            report["sourceAudioPeak"] = model.capturedAudioPeak
            if let track = audio.first {
                let peak = try audioPeak(asset: asset, track: track)
                report["audioPeak"] = peak
                if arguments.contains("--expect-sound") && peak < 0.01 {
                    throw RecorderError("音轨没有捕获到测试声音，峰值 \(peak)。")
                }
            }
            if let track = video.first {
                let size = try await track.load(.naturalSize)
                report["width"] = size.width; report["height"] = size.height
                let image = try await AVAssetImageGenerator(asset: asset).image(at: .init(seconds: 1, preferredTimescale: 600)).image
                let representation = NSBitmapImageRep(cgImage: image)
                if let png = representation.representation(using: .png, properties: [:]) {
                    try png.write(to: directory.appendingPathComponent("recorded-frame.png"))
                }
            }
            report["success"] = true
        } catch {
            report["success"] = false
            report["error"] = error.localizedDescription
            if model.canPause { await model.stop() }
        }
        if let data = try? JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]) {
            try? data.write(to: directory.appendingPathComponent("smoke-result.json"), options: .atomic)
            print(String(decoding: data, as: UTF8.self))
        }
        // Restore preferences before exiting rather than saving test directories as defaults.
        model.settings = savedSettings
        NSApp.terminate(nil)
    }

    private static func audioPeak(asset: AVAsset, track: AVAssetTrack) throws -> Double {
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false, AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false
        ])
        reader.add(output)
        guard reader.startReading() else { throw reader.error ?? RecorderError("无法解码音轨。") }
        var peak = 0
        while let sample = output.copyNextSampleBuffer(), let block = sample.dataBuffer {
            let size = CMBlockBufferGetDataLength(block)
            var samples = [Int16](repeating: 0, count: size / 2)
            let status = samples.withUnsafeMutableBytes {
                CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: size, destination: $0.baseAddress!)
            }
            guard status == noErr else { throw RecorderError("无法读取解码后的音频。") }
            for value in samples { peak = max(peak, abs(Int(value))) }
        }
        if reader.status == .failed { throw reader.error ?? RecorderError("音轨解码失败。") }
        return Double(peak) / 32768
    }
}
