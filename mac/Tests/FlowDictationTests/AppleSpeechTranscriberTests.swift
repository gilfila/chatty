import Foundation
import Testing
import FlowCore
import FlowTestSupport
@testable import FlowDictation

private enum ScriptedSpeechError: Error, Sendable {
    case framework
}

private actor ScriptedSpeechAnalysisSession: SpeechAnalysisSession {
    private let stream: AsyncThrowingStream<SpeechAnalysisEvent, any Error>
    private let continuation: AsyncThrowingStream<SpeechAnalysisEvent, any Error>.Continuation
    private let duration: TimeInterval
    private let suspendFinishUntilCancel: Bool
    private var finishWaiter: CheckedContinuation<Void, Never>?
    private var finishStartedValue = false

    init(duration: TimeInterval = 1.25, suspendFinishUntilCancel: Bool = false) {
        self.duration = duration
        self.suspendFinishUntilCancel = suspendFinishUntilCancel
        (stream, continuation) = AsyncThrowingStream.makeStream()
    }

    func events() async -> AsyncThrowingStream<SpeechAnalysisEvent, any Error> {
        stream
    }

    func start(source: any AudioFrameSource, session: DictationSessionID) async throws {}

    func finish() async throws -> TimeInterval {
        finishStartedValue = true
        if suspendFinishUntilCancel {
            await withCheckedContinuation { continuation in
                finishWaiter = continuation
            }
        }
        continuation.finish()
        return duration
    }

    func cancel() async {
        continuation.finish()
        finishWaiter?.resume()
        finishWaiter = nil
    }

    func emit(_ event: SpeechAnalysisEvent) {
        continuation.yield(event)
    }

    func fail(_ error: any Error) {
        continuation.finish(throwing: error)
    }

    func finishStarted() -> Bool {
        finishStartedValue
    }
}

private actor QueueSpeechAnalysisFactory: SpeechAnalysisSessionFactory {
    private var sessions: [ScriptedSpeechAnalysisSession]

    init(_ sessions: [ScriptedSpeechAnalysisSession]) {
        self.sessions = sessions
    }

    func make(localeIdentifier: String) async throws -> any SpeechAnalysisSession {
        sessions.removeFirst()
    }
}

@Suite("AppleSpeechTranscriber")
struct AppleSpeechTranscriberTests {
    @Test("partials stay volatile and final segments produce exactly one final")
    func partialAndFinal() async throws {
        let backend = ScriptedSpeechAnalysisSession(duration: 2.5)
        let transcriber = AppleSpeechTranscriber(
            factory: QueueSpeechAnalysisFactory([backend])
        )
        let session = DictationSessionID()
        let stream = try await transcriber.start(
            session: session,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        let collected = Task {
            var updates: [TranscriptionUpdate] = []
            for try await update in stream { updates.append(update) }
            return updates
        }

        await backend.emit(.partial("hello wor"))
        await backend.emit(.finalSegment("Hello "))
        await backend.emit(.finalSegment("world."))
        await transcriber.finish(session: session)

        #expect(try await collected.value == [
            .partial(session: session, text: "hello wor"),
            .final(session: session, rawText: "Hello world.", audioDuration: 2.5),
        ])
    }

    @Test("late events from a superseded session cannot enter the replacement stream")
    func supersededSessionDropsLateEvents() async throws {
        let firstBackend = ScriptedSpeechAnalysisSession()
        let secondBackend = ScriptedSpeechAnalysisSession()
        let transcriber = AppleSpeechTranscriber(
            factory: QueueSpeechAnalysisFactory([firstBackend, secondBackend])
        )
        let first = DictationSessionID()
        let second = DictationSessionID()
        let firstStream = try await transcriber.start(
            session: first,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        let firstDrain = Task {
            var updates: [TranscriptionUpdate] = []
            for try await update in firstStream { updates.append(update) }
            return updates
        }

        let secondStream = try await transcriber.start(
            session: second,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        let secondDrain = Task {
            var updates: [TranscriptionUpdate] = []
            for try await update in secondStream { updates.append(update) }
            return updates
        }

        await firstBackend.emit(.finalSegment("stale"))
        await secondBackend.emit(.finalSegment("current"))
        await transcriber.finish(session: second)

        #expect(try await firstDrain.value.isEmpty)
        #expect(try await secondDrain.value == [
            .final(session: second, rawText: "current", audioDuration: 1.25),
        ])
    }

    @Test("framework failure throws and never fabricates an empty final")
    func frameworkFailureHasNoFinal() async throws {
        let backend = ScriptedSpeechAnalysisSession()
        let transcriber = AppleSpeechTranscriber(
            factory: QueueSpeechAnalysisFactory([backend])
        )
        let session = DictationSessionID()
        let stream = try await transcriber.start(
            session: session,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        let collected = Task {
            var updates: [TranscriptionUpdate] = []
            for try await update in stream { updates.append(update) }
            return updates
        }

        await backend.fail(ScriptedSpeechError.framework)
        await transcriber.finish(session: session)

        await #expect(throws: AppleSpeechTranscriberError.backendFailed("framework")) {
            _ = try await collected.value
        }
    }

    @Test("clean framework completion without text is an explicit error")
    func emptyFinalIsRejected() async throws {
        let backend = ScriptedSpeechAnalysisSession()
        let transcriber = AppleSpeechTranscriber(
            factory: QueueSpeechAnalysisFactory([backend])
        )
        let session = DictationSessionID()
        let stream = try await transcriber.start(
            session: session,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        let drain = Task {
            for try await _ in stream {}
        }

        await transcriber.finish(session: session)

        await #expect(throws: AppleSpeechTranscriberError.emptyFinal) {
            try await drain.value
        }
    }

    @Test("cancellation invalidates a session before blocked finalization resumes")
    func cancellationWinsFinalizationRace() async throws {
        let backend = ScriptedSpeechAnalysisSession(suspendFinishUntilCancel: true)
        let transcriber = AppleSpeechTranscriber(
            factory: QueueSpeechAnalysisFactory([backend])
        )
        let session = DictationSessionID()
        let stream = try await transcriber.start(
            session: session,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        let drain = Task {
            var updates: [TranscriptionUpdate] = []
            for try await update in stream { updates.append(update) }
            return updates
        }

        let finalization = Task { await transcriber.finish(session: session) }
        while !(await backend.finishStarted()) { await Task.yield() }
        await transcriber.cancel(session: session)
        await backend.emit(.finalSegment("too late"))
        await finalization.value

        #expect(try await drain.value.isEmpty)
    }
}
