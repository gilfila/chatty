import Foundation
import Testing
import FlowCore
import FlowTestSupport
@testable import FlowDictation

private actor PipelineTrace {
    private var entries: [String] = []

    func record(_ entry: String) {
        entries.append(entry)
    }

    func values() -> [String] {
        entries
    }
}

private enum CoordinatorTestError: Error, Sendable {
    case persistence
    case framework
}

private actor ControlledTranscriber: Transcriber {
    nonisolated let localeIdentifier = "en-US"
    private var continuations: [DictationSessionID: AsyncThrowingStream<TranscriptionUpdate, any Error>.Continuation] = [:]
    private var cancelledSessions: [DictationSessionID] = []

    func start(
        session: DictationSessionID,
        source: any AudioFrameSource
    ) async throws -> AsyncThrowingStream<TranscriptionUpdate, any Error> {
        let (stream, continuation) = AsyncThrowingStream<TranscriptionUpdate, any Error>.makeStream()
        continuations[session] = continuation
        return stream
    }

    func finish(session: DictationSessionID) async {
        continuations[session]?.finish()
    }

    func cancel(session: DictationSessionID) async {
        cancelledSessions.append(session)
        continuations[session]?.finish()
    }

    func emit(_ update: TranscriptionUpdate, for session: DictationSessionID) {
        continuations[session]?.yield(update)
    }

    func fail(_ error: any Error, session: DictationSessionID) {
        continuations[session]?.finish(throwing: error)
    }

    func cancelCount(for session: DictationSessionID) -> Int {
        cancelledSessions.count { $0 == session }
    }
}

private actor RecordingTranscriptStore: TranscriptStore {
    private let trace: PipelineTrace
    private let failAppend: Bool
    private var stored: [StoredTranscript] = []

    init(trace: PipelineTrace, failAppend: Bool = false) {
        self.trace = trace
        self.failAppend = failAppend
    }

    func append(_ transcript: Transcript) async throws {
        await trace.record("append")
        if failAppend { throw CoordinatorTestError.persistence }
        stored.append(StoredTranscript(transcript: transcript))
    }

    func records() async throws -> [StoredTranscript] {
        stored
    }

    func search(_ query: String) async throws -> [StoredTranscript] {
        stored.filter {
            $0.transcript.rawText.contains(query) || $0.transcript.formattedText.contains(query)
        }
    }

    func delete(id: DictationSessionID) async throws {
        stored.removeAll { $0.id == id }
    }

    func recordInsertion(
        _ outcome: InjectionOutcome,
        for id: DictationSessionID,
        at date: Date
    ) async throws {
        await trace.record("outcome")
        guard let index = stored.firstIndex(where: { $0.id == id }) else {
            throw LocalRepositoryError.transcriptNotFound(id)
        }
        stored[index].injectionOutcome = outcome
        stored[index].updatedAt = date
    }

    func lastSuccessfulTranscript() async throws -> Transcript? {
        stored.last?.transcript
    }
}

private struct FixedDictionaryStore: DictionaryStore {
    let values: [DictionaryCorrection]

    init(_ values: [DictionaryCorrection] = []) {
        self.values = values
    }

    func corrections() async throws -> [DictionaryCorrection] { values }
    func upsert(_ correction: DictionaryCorrection) async throws {}
    func delete(id: UUID) async throws {}
}

private actor RecordingInjector: TextInjector {
    private let trace: PipelineTrace
    private let target: TargetCapture
    private let outcome: InjectionOutcome

    init(
        trace: PipelineTrace,
        target: FocusTarget? = FocusTarget(
            processID: 42,
            bundleID: "com.example.editor",
            elementSignature: "field",
            isSecure: false,
            isEditable: true
        ),
        outcome: InjectionOutcome = .inserted
    ) {
        self.trace = trace
        self.target = target.map(TargetCapture.target) ?? .noTarget
        self.outcome = outcome
    }

    func captureTarget() async -> TargetCapture {
        await trace.record("capture")
        return target
    }

    func inject(
        _ text: String,
        into target: TargetCapture,
        timeout: Duration
    ) async -> InjectionResult {
        await trace.record("inject")
        return InjectionResult(outcome: outcome, clipboardRestored: true)
    }
}

@MainActor
private final class CoordinatorDelegate: DictationSessionDelegate {
    var states: [DictationState] = []
    var partials: [String] = []
    var finals: [Transcript] = []
    var outcomes: [InjectionOutcome] = []
    var failures: [DictationFailure] = []

    func dictationSession(_ id: DictationSessionID, didChangeState state: DictationState) {
        states.append(state)
    }

    func dictationSession(_ id: DictationSessionID, didProducePartial text: String) {
        partials.append(text)
    }

    func dictationSession(_ id: DictationSessionID, didFinalize transcript: Transcript) {
        finals.append(transcript)
    }

    func dictationSession(_ id: DictationSessionID, didAttemptInjection outcome: InjectionOutcome) {
        outcomes.append(outcome)
    }

    func dictationSession(_ id: DictationSessionID, didFail failure: DictationFailure) {
        failures.append(failure)
    }
}

@Suite("DictationCoordinator")
struct DictationCoordinatorTests {
    @MainActor
    @Test("finalize orders capture, persist, inject, then persist outcome")
    func orderedPipeline() async throws {
        let trace = PipelineTrace()
        let transcriber = ControlledTranscriber()
        let store = RecordingTranscriptStore(trace: trace)
        let delegate = CoordinatorDelegate()
        let coordinator = DictationCoordinator(
            transcriber: transcriber,
            formatter: DeterministicFormatter(),
            transcripts: store,
            dictionary: FixedDictionaryStore(),
            injector: RecordingInjector(trace: trace),
            delegate: delegate,
            now: { Date(timeIntervalSince1970: 10) }
        )
        let session = DictationSessionID()

        try await coordinator.start(
            session: session,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        await transcriber.emit(.partial(session: session, text: "hello"), for: session)
        await transcriber.emit(
            .final(session: session, rawText: "hello world", audioDuration: 1.5),
            for: session
        )
        await coordinator.finish(session: session)

        #expect(await trace.values() == ["capture", "append", "inject", "outcome"])
        let records = try await store.records()
        #expect(records.count == 1)
        #expect(records.first?.injectionOutcome == .inserted)
        #expect(delegate.partials == ["hello"])
        #expect(delegate.finals.map(\.id) == [session])
        #expect(delegate.outcomes == [.inserted])
    }

    @MainActor
    @Test("append failure prevents injection and keeps an in-memory recovery transcript")
    func persistenceFailureStopsBeforePaste() async throws {
        let trace = PipelineTrace()
        let transcriber = ControlledTranscriber()
        let store = RecordingTranscriptStore(trace: trace, failAppend: true)
        let delegate = CoordinatorDelegate()
        let coordinator = DictationCoordinator(
            transcriber: transcriber,
            formatter: DeterministicFormatter(),
            transcripts: store,
            dictionary: FixedDictionaryStore(),
            injector: RecordingInjector(trace: trace),
            delegate: delegate
        )
        let session = DictationSessionID()

        try await coordinator.start(
            session: session,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        await transcriber.emit(
            .final(session: session, rawText: "keep me", audioDuration: 1),
            for: session
        )
        await coordinator.finish(session: session)

        #expect(await trace.values() == ["capture", "append"])
        #expect(await coordinator.recoverableTranscript()?.rawText == "keep me")
        #expect(delegate.finals.isEmpty)
        #expect(delegate.failures.count == 1)
    }

    @MainActor
    @Test("a repeated framework final is persisted and injected only once")
    func duplicateFinalIsClaimedOnce() async throws {
        let trace = PipelineTrace()
        let transcriber = ControlledTranscriber()
        let store = RecordingTranscriptStore(trace: trace)
        let coordinator = DictationCoordinator(
            transcriber: transcriber,
            formatter: DeterministicFormatter(),
            transcripts: store,
            dictionary: FixedDictionaryStore(),
            injector: RecordingInjector(trace: trace)
        )
        let session = DictationSessionID()

        try await coordinator.start(
            session: session,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        await transcriber.emit(
            .final(session: session, rawText: "first", audioDuration: 1),
            for: session
        )
        await transcriber.emit(
            .final(session: session, rawText: "duplicate", audioDuration: 1),
            for: session
        )
        await coordinator.finish(session: session)

        let records = try await store.records()
        #expect(records.map(\.transcript.rawText) == ["first"])
        #expect(await trace.values() == ["capture", "append", "inject", "outcome"])
    }

    @MainActor
    @Test("cancellation discards a later final without persistence")
    func cancelledSessionDropsLateFinal() async throws {
        let trace = PipelineTrace()
        let transcriber = ControlledTranscriber()
        let store = RecordingTranscriptStore(trace: trace)
        let coordinator = DictationCoordinator(
            transcriber: transcriber,
            formatter: DeterministicFormatter(),
            transcripts: store,
            dictionary: FixedDictionaryStore(),
            injector: RecordingInjector(trace: trace)
        )
        let session = DictationSessionID()

        try await coordinator.start(
            session: session,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        await coordinator.cancel(session: session)
        await transcriber.emit(
            .final(session: session, rawText: "late", audioDuration: 1),
            for: session
        )
        await Task.yield()

        #expect(try await store.records().isEmpty)
        #expect(await trace.values() == ["capture"])
    }

    @MainActor
    @Test("a superseded session cannot paste into the replacement session")
    func supersededSessionDropsLateFinal() async throws {
        let trace = PipelineTrace()
        let transcriber = ControlledTranscriber()
        let store = RecordingTranscriptStore(trace: trace)
        let coordinator = DictationCoordinator(
            transcriber: transcriber,
            formatter: DeterministicFormatter(),
            transcripts: store,
            dictionary: FixedDictionaryStore(),
            injector: RecordingInjector(trace: trace)
        )
        let first = DictationSessionID()
        let second = DictationSessionID()

        try await coordinator.start(
            session: first,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        try await coordinator.start(
            session: second,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        await transcriber.emit(
            .final(session: first, rawText: "stale", audioDuration: 1),
            for: first
        )
        await transcriber.emit(
            .final(session: second, rawText: "current", audioDuration: 1),
            for: second
        )
        await coordinator.finish(session: second)

        let records = try await store.records()
        #expect(records.map(\.id) == [second])
    }

    @MainActor
    @Test("transcriber failure produces no transcript or injection")
    func transcriberFailureHasNoEmptyFinal() async throws {
        let trace = PipelineTrace()
        let transcriber = ControlledTranscriber()
        let store = RecordingTranscriptStore(trace: trace)
        let delegate = CoordinatorDelegate()
        let coordinator = DictationCoordinator(
            transcriber: transcriber,
            formatter: DeterministicFormatter(),
            transcripts: store,
            dictionary: FixedDictionaryStore(),
            injector: RecordingInjector(trace: trace),
            delegate: delegate
        )
        let session = DictationSessionID()

        try await coordinator.start(
            session: session,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        await transcriber.fail(CoordinatorTestError.framework, session: session)
        await coordinator.finish(session: session)

        #expect(try await store.records().isEmpty)
        #expect(await trace.values() == ["capture"])
        #expect(delegate.finals.isEmpty)
        #expect(delegate.failures.count == 1)
        #expect(await transcriber.cancelCount(for: session) == 1)
    }

    @MainActor
    @Test("clean stream completion without a final becomes a recoverable failure")
    func missingFinalIsReported() async throws {
        let trace = PipelineTrace()
        let transcriber = ControlledTranscriber()
        let store = RecordingTranscriptStore(trace: trace)
        let delegate = CoordinatorDelegate()
        let coordinator = DictationCoordinator(
            transcriber: transcriber,
            formatter: DeterministicFormatter(),
            transcripts: store,
            dictionary: FixedDictionaryStore(),
            injector: RecordingInjector(trace: trace),
            delegate: delegate
        )
        let session = DictationSessionID()

        try await coordinator.start(
            session: session,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        await coordinator.finish(session: session)

        #expect(try await store.records().isEmpty)
        #expect(await trace.values() == ["capture"])
        #expect(delegate.failures.count == 1)
        #expect(delegate.states.contains {
            if case .recoverableFailure(.transcriptionFailed) = $0 { true } else { false }
        })
    }

    @MainActor
    @Test("a secure start target is neither persisted nor kept in recovery")
    func secureTargetLeavesNoTranscript() async throws {
        let trace = PipelineTrace()
        let transcriber = ControlledTranscriber()
        let store = RecordingTranscriptStore(trace: trace)
        let delegate = CoordinatorDelegate()
        let secure = FocusTarget(
            processID: 42,
            bundleID: "com.example.passwords",
            elementSignature: "secure-field",
            isSecure: true,
            isEditable: true
        )
        let coordinator = DictationCoordinator(
            transcriber: transcriber,
            formatter: DeterministicFormatter(),
            transcripts: store,
            dictionary: FixedDictionaryStore(),
            injector: RecordingInjector(trace: trace, target: secure),
            delegate: delegate
        )
        let session = DictationSessionID()

        try await coordinator.start(
            session: session,
            source: FakeAudioFrameSource(frameCount: 0)
        )
        await transcriber.emit(
            .final(session: session, rawText: "do not retain", audioDuration: 1),
            for: session
        )
        await coordinator.finish(session: session)

        #expect(try await store.records().isEmpty)
        #expect(await trace.values() == ["capture"])
        #expect(await coordinator.recoverableTranscript() == nil)
        #expect(delegate.outcomes == [.secureTarget])
    }
}
