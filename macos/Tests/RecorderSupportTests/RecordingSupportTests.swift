import Foundation
import CoreMedia
import Testing
@testable import RecorderSupport

private func time(_ seconds: Double) -> CMTime { CMTime(seconds: seconds, preferredTimescale: 48000) }

@Test func pauseIsRemovedFromEveryTrack() {
    var timeline = RecordingTimeline(origin: time(100))
    #expect(timeline.timestamp(for: time(101))!.seconds == 1)
    timeline.pause(at: time(102))
    #expect(timeline.timestamp(for: time(103)) == nil)
    #expect(timeline.position(at: time(103)).seconds == 2)
    timeline.pause(at: time(104)) // Repeated pause must not reset the pause start.
    timeline.resume(at: time(107))
    #expect(timeline.timestamp(for: time(105)) == nil)
    #expect(timeline.timestamp(for: time(101))!.seconds == 1)
    #expect(timeline.timestamp(for: time(108))!.seconds == 3)
    timeline.resume(at: time(109))
    timeline.pause(at: time(110)); timeline.resume(at: time(112))
    #expect(timeline.timestamp(for: time(113))!.seconds == 6)
}

@Test func preRollIsDiscarded() {
    let timeline = RecordingTimeline(origin: time(100))
    #expect(timeline.timestamp(for: time(99)) == nil)
    #expect(timeline.timestamp(for: .invalid) == nil)
    #expect(timeline.timestamp(for: time(100))!.seconds == 0)
}

@Test func dimensionsPreserveAspectRatioAndEvenPixels() {
    let landscape = RecordingFiles.dimensions(width: 3024, height: 1964, maxHeight: 1080)
    #expect(landscape.0 == 1662 && landscape.1 == 1080)
    let small = RecordingFiles.dimensions(width: 801, height: 601, maxHeight: 1080)
    #expect(small.0 == 800 && small.1 == 600)
    let portrait = RecordingFiles.dimensions(width: 1080, height: 1920, maxHeight: 720)
    #expect(portrait.0 == 404 && portrait.1 == 720)
}

@Test func existingRecordingIsNeverOverwritten() throws {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    let date = Date(timeIntervalSince1970: 0)
    let first = RecordingFiles.uniqueURL(in: directory, extension: "mp4", now: date)
    try Data().write(to: first)
    let second = RecordingFiles.uniqueURL(in: directory, extension: "mp4", now: date)
    #expect(first != second)
    #expect(second.lastPathComponent.hasSuffix("_1.mp4"))
}
