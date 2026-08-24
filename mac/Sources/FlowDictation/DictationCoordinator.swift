import Foundation
import FlowCore

/// Owns one dictation session from target capture through durable transcript and safe paste.
///
/// The coordinator deliberately keeps framework work, formatting, persistence, and injection in
/// this order. A transcript is never pasted until its final text is durable, and a late update
/// must pass both session and generation checks before it can reach the delegate.
public actor DictationCoordinator {
    private struct ActiveSession {
        let id: DictationSessionID
        let generation: UInt64
        let target: TargetCapture
        var consumer: Task<Void, Never>?
        var failed = false
        var receivedFinal = false
    }

    private let transcriber: any Transcriber
    private let formatter: any Formatter
    private let transcripts: any TranscriptStore
    private let dictionary: any DictionaryStore
    private let injector: any TextInjector
    private let gate = SessionEventGate()
    private let now: @Sendable () -> Date
    private let injectionTimeout: Duration

    private let delegate: DelegateRelay
    private var active: ActiveSession?
    private var recovery: Transcript?

    @MainActor
    public init(
        transcriber: any Transcriber,
        formatter: any Formatter,
        transcripts: any TranscriptStore,
        dictionary: any DictionaryStore,
        injector: any TextInjector,
        delegate: (any DictationSessionDelegate)? = nil,
        now: @escaping @Sendable () -> Date = Date.init,
        injectionTimeout: Duration = .milliseconds(250)
    ) {
        self.transcriber = transcriber
        self.formatter = formatter
        self.transcripts = transcripts
        self.dictionary = dictionary
        self.injector = injector
        self.delegate = DelegateRelay(delegate)
        self.now = now
        self.injectionTimeout = injectionTimeout
    }

    public func start(
        session id: DictationSessionID,
        source: any AudioFrameSource
    ) async throws {
        if let previous = active?.id {
            await cancel(session: previous)
        }

        let target = await injector.captureTarget()
        let generation = await gate.begin(id)

        do {
            let updates = try await transcriber.start(session: id, source: source)
            active = ActiveSession(id: id, generation: generation, target: target)
            await delegate.state(.listening(id), session: id)

            let consumer = Task { [weak self] in
                do {
                    for try await update in updates {
                        guard !Task.isCancelled else { return }
                        await self?.receive(update, generation: generation)
                    }
                } catch is CancellationError {
                    return
                } catch {
                    await self?.receiveFailure(error, session: id, generation: generation)
                }
            }

            guard var current = active,
                  current.id == id,
                  current.generation == generation
            else {
                consumer.cancel()
                await transcriber.cancel(session: id)
                return
            }
            current.consumer = consumer
            active = current
        } catch {
            await gate.cancel(id)
            active = nil
            let failure = DictationFailure.transcriptionFailed(String(describing: error))
            await delegate.failure(failure, session: id)
            await delegate.state(.recoverableFailure(failure), session: id)
            throw error
        }
    }

    public func finish(session id: DictationSessionID) async {
        guard let current = active, current.id == id else { return }
        await delegate.state(.finalizing(id), session: id)
        await transcriber.finish(session: id)
        await current.consumer?.value
        if var remaining = active,
           remaining.id == id,
           !remaining.receivedFinal,
           !remaining.failed {
            remaining.failed = true
            active = remaining
            await fail(id, .transcriptionFailed("The speech engine finished without a final transcript"))
        }
        await gate.complete(id)
        if active?.id == id { active = nil }
    }

    public func cancel(session id: DictationSessionID) async {
        guard let current = active, current.id == id else { return }

        // Invalidate first. A framework result racing any awaited shutdown is now stale.
        await gate.cancel(id)
        active = nil
        current.consumer?.cancel()
        await transcriber.cancel(session: id)
        await current.consumer?.value
        await delegate.state(.cancelled(id), session: id)
        await delegate.state(.idle, session: id)
    }

    public func recoverableTranscript() -> Transcript? {
        recovery
    }

    private func receive(
        _ update: TranscriptionUpdate,
        generation: UInt64
    ) async {
        switch update {
        case .partial(let id, let text):
            guard await gate.acceptsPartial(session: id, generation: generation) else { return }
            await delegate.partial(text, session: id)

        case .final(let id, let rawText, let audioDuration):
            guard await gate.claimFinal(session: id, generation: generation),
                  var current = active,
                  current.id == id,
                  current.generation == generation,
                  !current.failed
            else { return }
            current.receivedFinal = true
            active = current
            await finalize(
                session: id,
                rawText: rawText,
                audioDuration: audioDuration,
                target: current.target
            )
        }
    }

    private func finalize(
        session id: DictationSessionID,
        rawText: String,
        audioDuration: TimeInterval,
        target: TargetCapture
    ) async {
        // Personal vocabulary is an enhancement, not a dictation availability dependency. A
        // corrupt dictionary must not discard otherwise valid recognized speech.
        let corrections = (try? await dictionary.corrections()) ?? []

        let result = formatter.format(rawText, corrections: corrections)
        guard !result.formattedText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            await fail(id, .transcriptionFailed("No speech was recognized"))
            return
        }

        let transcript = Transcript(
            id: id,
            createdAt: now(),
            rawText: result.rawText,
            formattedText: result.formattedText,
            localeIdentifier: transcriber.localeIdentifier,
            audioDuration: audioDuration
        )
        recovery = transcript

        // Never persist or paste dictated text captured while a secure field was focused.
        if case .secureTarget = target {
            recovery = nil
            await delegate.outcome(.secureTarget, session: id)
            await delegate.state(.idle, session: id)
            return
        }
        if case .target(let value) = target, value.isSecure {
            recovery = nil
            await delegate.outcome(.secureTarget, session: id)
            await delegate.state(.idle, session: id)
            return
        }

        do {
            try await transcripts.append(transcript)
        } catch {
            await fail(id, .internalError("Could not save the transcript: \(error)"))
            return
        }

        await delegate.final(transcript, session: id)
        await delegate.state(.inserting(id), session: id)

        let insertion = await injector.inject(
            transcript.formattedText,
            into: target,
            timeout: injectionTimeout
        )

        do {
            try await transcripts.recordInsertion(insertion.outcome, for: id, at: now())
        } catch {
            await delegate.outcome(insertion.outcome, session: id)
            await fail(id, .internalError("Text was handled, but its result could not be saved: \(error)"))
            return
        }

        await delegate.outcome(insertion.outcome, session: id)
        if insertion.outcome == .inserted {
            await delegate.state(.idle, session: id)
        } else {
            await delegate.state(
                .recoverableFailure(.injectionFailed(insertion.outcome)),
                session: id
            )
        }
    }

    private func receiveFailure(
        _ error: any Error,
        session id: DictationSessionID,
        generation: UInt64
    ) async {
        guard var current = active,
              current.id == id,
              current.generation == generation,
              !current.failed,
              await gate.acceptsPartial(session: id, generation: generation)
        else { return }

        current.failed = true
        active = current
        // A transcription stream can fail while the user is still holding the trigger. Invalidate
        // and stop the backend here; the UI will transition to recoverable failure and may never
        // receive a later release edge for this now-terminal session.
        await gate.cancel(id)
        active = nil
        await transcriber.cancel(session: id)
        await fail(id, .transcriptionFailed(String(describing: error)))
    }

    private func fail(_ id: DictationSessionID, _ failure: DictationFailure) async {
        await delegate.failure(failure, session: id)
        await delegate.state(.recoverableFailure(failure), session: id)
    }
}

/// Keeps the non-Sendable UI delegate entirely on the main actor while allowing the coordinator
/// actor to retain a Sendable relay. The weak reference prevents the resident app from being kept
/// alive by an in-flight dictation task during termination.
@MainActor
private final class DelegateRelay: Sendable {
    private weak var value: (any DictationSessionDelegate)?

    init(_ value: (any DictationSessionDelegate)?) {
        self.value = value
    }

    func state(_ state: DictationState, session: DictationSessionID) {
        value?.dictationSession(session, didChangeState: state)
    }

    func partial(_ text: String, session: DictationSessionID) {
        value?.dictationSession(session, didProducePartial: text)
    }

    func final(_ transcript: Transcript, session: DictationSessionID) {
        value?.dictationSession(session, didFinalize: transcript)
    }

    func outcome(_ outcome: InjectionOutcome, session: DictationSessionID) {
        value?.dictationSession(session, didAttemptInjection: outcome)
    }

    func failure(_ failure: DictationFailure, session: DictationSessionID) {
        value?.dictationSession(session, didFail: failure)
    }
}
