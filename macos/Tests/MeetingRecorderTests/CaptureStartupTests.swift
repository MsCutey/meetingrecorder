import Foundation
import Testing
@testable import MeetingRecorder

@Suite struct CaptureStartupTests {
    @Test func captureCallbackAloneDoesNotStartTheRecording() {
        var results: [Result<Void, Error>] = []
        let gate = CaptureStartupGate(requiresVideo: true) { results.append($0) }
        gate.captureDidStart()
        #expect(results.isEmpty)
        gate.mediaWasWritten(isVideo: true)
        #expect(results.count == 1)
        #expect((try? results.first?.get()) != nil)
    }

    @Test func firstFrameMayArriveBeforeStartCaptureCallback() {
        var results: [Result<Void, Error>] = []
        let gate = CaptureStartupGate(requiresVideo: true) { results.append($0) }
        gate.mediaWasWritten(isVideo: true)
        #expect(results.isEmpty)
        gate.captureDidStart()
        gate.mediaWasWritten(isVideo: true)
        gate.captureDidStart()
        #expect(results.count == 1)
        #expect(gate.isResolved)
    }

    @Test func timeoutCannotBeOverwrittenByLateCaptureCallbacks() {
        var results: [Result<Void, Error>] = []
        let gate = CaptureStartupGate(requiresVideo: true) { results.append($0) }
        gate.captureDidStart()
        gate.fail(RecorderError("first frame timed out"))
        gate.mediaWasWritten(isVideo: true)
        gate.fail(RecorderError("late stream error"))
        #expect(results.count == 1)
        if case .failure(let error)? = results.first {
            #expect(error.localizedDescription == "first frame timed out")
        } else { Issue.record("A timeout must not become a successful recording") }
    }

    @Test func cancellationAfterFirstFrameStillPreventsStartupUntilStreamIsReady() {
        var results: [Result<Void, Error>] = []
        let gate = CaptureStartupGate(requiresVideo: true) { results.append($0) }
        gate.mediaWasWritten(isVideo: true)
        gate.fail(CancellationError())
        gate.captureDidStart()
        #expect(results.count == 1)
        if case .failure(let error)? = results.first {
            #expect(error is CancellationError)
        } else { Issue.record("A cancelled startup must remain cancelled") }
    }

    @Test func audioCannotMakeAWindowRecordingAppearReady() {
        var videoResults = 0
        let videoGate = CaptureStartupGate(requiresVideo: true) { _ in videoResults += 1 }
        videoGate.captureDidStart()
        videoGate.mediaWasWritten(isVideo: false)
        #expect(videoResults == 0)
        videoGate.mediaWasWritten(isVideo: true)
        #expect(videoResults == 1)

        var audioResults = 0
        let audioGate = CaptureStartupGate(requiresVideo: false) { _ in audioResults += 1 }
        audioGate.captureDidStart()
        audioGate.mediaWasWritten(isVideo: false)
        #expect(audioResults == 1)
    }
}
