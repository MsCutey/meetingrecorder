import Foundation
import CoreMedia

/// Host-clock timestamps are shared by screen, system audio and microphone samples.
public struct RecordingTimeline {
    public let origin: CMTime
    private var pauseStart: CMTime?
    private var removed = CMTime.zero
    private var pauses: [CMTimeRange] = []

    public init(origin: CMTime) { self.origin = origin }
    public var isPaused: Bool { pauseStart != nil }
    public mutating func pause(at time: CMTime) {
        if pauseStart == nil { pauseStart = time }
    }
    public mutating func resume(at time: CMTime) {
        if let start = pauseStart {
            let duration = CMTimeMaximum(.zero, time - start)
            pauses.append(CMTimeRange(start: start, duration: duration))
            removed = removed + duration
            pauseStart = nil
        }
    }
    public func position(at time: CMTime) -> CMTime {
        CMTimeMaximum(.zero, (pauseStart ?? time) - origin - removed)
    }
    public func timestamp(for time: CMTime) -> CMTime? {
        guard !isPaused, time.isValid, time >= origin else { return nil }
        // A queued sample captured during a pause can arrive after resume.
        if pauses.contains(where: { $0.containsTime(time) }) { return nil }
        let elapsedPauses = pauses.filter { $0.end <= time }.reduce(CMTime.zero) { $0 + $1.duration }
        let adjusted = time - origin - elapsedPauses
        return adjusted >= .zero ? adjusted : nil
    }
}

public enum RecordingFiles {
    public static func dimensions(width: Int, height: Int, maxHeight: Int) -> (Int, Int) {
        let scale = min(1, Double(maxHeight) / Double(max(2, height)))
        return (max(2, Int(Double(width) * scale) / 2 * 2),
                max(2, Int(Double(height) * scale) / 2 * 2))
    }
    public static func uniqueURL(in directory: URL, extension ext: String, now: Date = Date()) -> URL {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd_HH-mm-ss"
        let stem = "Meeting_" + formatter.string(from: now)
        var result = directory.appendingPathComponent(stem).appendingPathExtension(ext)
        var index = 1
        while FileManager.default.fileExists(atPath: result.path) {
            result = directory.appendingPathComponent("\(stem)_\(index)").appendingPathExtension(ext)
            index += 1
        }
        return result
    }
}
