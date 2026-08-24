import Testing
import Foundation
@testable import FlowCore
import FlowTestSupport

// These pin the M0 boundary that M1 and M2 build against in parallel. A change that breaks one of
// these is a contract change and must be announced, not absorbed.

@Suite("PermissionState")
struct PermissionStateTests {
    @Test("notRequired is never reported as blocking")
    func notRequiredIsNotBlocking() {
        var state = PermissionState()
        state[.microphone] = .granted
        state[.inputMonitoring] = .notRequired
        state[.accessibility] = .denied

        #expect(state.blocking(required: [.microphone, .inputMonitoring]) == [])
        #expect(state.blocking(required: [.microphone, .accessibility]) == [.accessibility])
    }

    @Test("dictation survives a denied Accessibility grant but not a denied mic")
    func accessibilityIsNotFatal() {
        var state = PermissionState()
        state[.microphone] = .granted
        state[.inputMonitoring] = .granted
        state[.accessibility] = .denied
        // Transcript is retained for manual paste, so the loop still has value.
        #expect(state.canDictate(triggerPermissions: [.inputMonitoring, .accessibility]))

        state[.microphone] = .denied
        #expect(!state.canDictate(triggerPermissions: [.inputMonitoring, .accessibility]))
    }
}

@Suite("InjectionOutcome")
struct InjectionOutcomeTests {
    @Test("every non-terminal outcome keeps the transcript reachable")
    func recoverySurfaceIsExhaustive() {
        // The whole no-data-loss guarantee rests on this mapping.
        #expect(!InjectionOutcome.inserted.needsRecoverySurface)
        #expect(!InjectionOutcome.secureTarget.needsRecoverySurface)
        for outcome: InjectionOutcome in [.noTarget, .permissionDenied, .targetChanged, .timedOut, .cancelled] {
            #expect(outcome.needsRecoverySurface, "\(outcome) must stay recoverable")
        }
    }
}

@Suite("FocusTarget")
struct FocusTargetTests {
    private func target(secure: Bool = false, editable: Bool = true, signature: String = "sig-1") -> FocusTarget {
        FocusTarget(
            processID: 42,
            bundleID: "com.example.editor",
            elementSignature: signature,
            isSecure: secure,
            isEditable: editable
        )
    }

    @Test("secure and non-editable targets are not injectable")
    func injectability() {
        #expect(target().isInjectable)
        #expect(!target(secure: true).isInjectable)
        #expect(!target(editable: false).isInjectable)
    }

    @Test("a different element compares unequal so the injector can refuse")
    func equalityDetectsTargetChange() {
        #expect(target() == target())
        #expect(target() != target(signature: "sig-2"))
    }
}

@Suite("FakeDictationTrigger")
struct FakeTriggerTests {
    @Test("auto-repeat cannot emit two pressed in a row")
    func pressIsIdempotent() async throws {
        let trigger = FakeDictationTrigger()
        try await trigger.start()

        let collected = Task {
            var events: [TriggerEvent] = []
            for await event in trigger.events {
                events.append(event)
            }
            return events
        }

        trigger.press()
        trigger.press()   // auto-repeat — must be swallowed
        trigger.release()
        await trigger.stop()

        #expect(try await collected.value == [.pressed, .released])
    }

    @Test("cancel wins — no released follows for that press")
    func cancelWins() async throws {
        let trigger = FakeDictationTrigger()
        try await trigger.start()

        let collected = Task {
            var events: [TriggerEvent] = []
            for await event in trigger.events {
                events.append(event)
            }
            return events
        }

        trigger.press()
        trigger.cancel()
        trigger.release()   // already up — must be swallowed
        await trigger.stop()

        #expect(try await collected.value == [.pressed, .cancelled])
    }
}

@Suite("FakeAudioFrameSource")
struct FakeAudioSourceTests {
    @Test("frames carry the session id and match the built-in mic shape")
    func framesAreTagged() async throws {
        let source = FakeAudioFrameSource(frameCount: 3)
        let session = DictationSessionID()
        let stream = try await source.startCapture(session: session)

        var count = 0
        for try await frame in stream {
            #expect(frame.session == session)
            #expect(frame.buffer.format.sampleRate == 48_000)
            #expect(frame.buffer.format.channelCount == 1)
            count += 1
        }
        #expect(count == 3)
    }

    @Test("stopCapture always finishes the stream so a consumer is never stranded")
    func stopFinishesStream() async throws {
        let source = FakeAudioFrameSource(frameCount: 1_000_000)
        let stream = try await source.startCapture(session: DictationSessionID())

        let drain = Task {
            var seen = 0
            for try await _ in stream { seen += 1 }
            return seen
        }
        await source.stopCapture()

        // Must return rather than hang; the count itself is timing-dependent and not asserted.
        _ = try await drain.value
        #expect(await source.isCapturing == false)
    }

    @Test("a failing source throws instead of yielding silence")
    func failurePropagates() async {
        let source = FakeAudioFrameSource(failWith: .permissionDenied)
        await #expect(throws: AudioSourceError.permissionDenied) {
            _ = try await source.startCapture(session: DictationSessionID())
        }
    }
}
