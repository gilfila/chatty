import Foundation
@preconcurrency import AVFAudio
import FlowCore
import Speech

public enum AppleSpeechTranscriberError: Error, Sendable, Equatable {
    case noCompatibleAudioFormat
    case conversionFailed(String)
    case emptyFinal
    case backendFailed(String)
}

enum SpeechAnalysisEvent: Sendable, Equatable {
    case partial(String)
    case finalSegment(String)
}

protocol SpeechAnalysisSession: Sendable {
    func events() async -> AsyncThrowingStream<SpeechAnalysisEvent, any Error>
    func start(source: any AudioFrameSource, session: DictationSessionID) async throws
    func finish() async throws -> TimeInterval
    func cancel() async
}

protocol SpeechAnalysisSessionFactory: Sendable {
    func make(localeIdentifier: String) async throws -> any SpeechAnalysisSession
}

struct SystemSpeechAnalysisSessionFactory: SpeechAnalysisSessionFactory {
    func make(localeIdentifier: String) async throws -> any SpeechAnalysisSession {
        SystemSpeechAnalysisSession(localeIdentifier: localeIdentifier)
    }
}

/// Session-safe adapter around macOS 26's on-device SpeechAnalyzer pipeline.
///
/// The system backend owns framework objects and audio conversion inside its own actor. This
/// outer actor owns product semantics: superseding a session invalidates its generation before
/// cancellation, volatile partials are session-scoped, and one combined final is emitted only
/// after framework finalization and result-consumer shutdown both complete.
public actor AppleSpeechTranscriber: Transcriber {
    public nonisolated let localeIdentifier: String

    private struct ActiveSession {
        let id: DictationSessionID
        let generation: UInt64
        let backend: any SpeechAnalysisSession
        let output: AsyncThrowingStream<TranscriptionUpdate, any Error>.Continuation
        var collector: Task<Void, Never>?
        var finalSegments: [String] = []
        var failed = false
        var finishing = false
    }

    private let factory: any SpeechAnalysisSessionFactory
    private var nextGeneration: UInt64 = 0
    private var active: ActiveSession?

    public init(localeIdentifier: String = "en-US") {
        self.localeIdentifier = localeIdentifier
        self.factory = SystemSpeechAnalysisSessionFactory()
    }

    init(
        localeIdentifier: String = "en-US",
        factory: any SpeechAnalysisSessionFactory
    ) {
        self.localeIdentifier = localeIdentifier
        self.factory = factory
    }

    public func start(
        session id: DictationSessionID,
        source: any AudioFrameSource
    ) async throws -> AsyncThrowingStream<TranscriptionUpdate, any Error> {
        if let previous = active?.id {
            await cancel(session: previous)
        }

        nextGeneration &+= 1
        let generation = nextGeneration
        let backend = try await factory.make(localeIdentifier: localeIdentifier)
        let backendEvents = await backend.events()
        let (stream, output) = AsyncThrowingStream<TranscriptionUpdate, any Error>.makeStream()

        active = ActiveSession(
            id: id,
            generation: generation,
            backend: backend,
            output: output
        )

        do {
            try await backend.start(source: source, session: id)
        } catch {
            let mapped = map(error)
            output.finish(throwing: mapped)
            active = nil
            throw mapped
        }

        let collector = Task { [weak self] in
            do {
                for try await event in backendEvents {
                    guard !Task.isCancelled else { return }
                    await self?.receive(event, session: id, generation: generation)
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
            collector.cancel()
            await backend.cancel()
            output.finish()
            return stream
        }
        current.collector = collector
        active = current
        return stream
    }

    public func finish(session id: DictationSessionID) async {
        guard var current = active, current.id == id else { return }
        guard !current.finishing else { return }
        current.finishing = true
        active = current

        do {
            let duration = try await current.backend.finish()
            await current.collector?.value
            complete(session: id, generation: current.generation, audioDuration: duration)
        } catch {
            await current.backend.cancel()
            current.collector?.cancel()
            await current.collector?.value
            receiveFailure(error, session: id, generation: current.generation)
            active = nil
        }
    }

    public func cancel(session id: DictationSessionID) async {
        guard let current = active, current.id == id else { return }

        // Invalidate before awaiting framework shutdown so a racing result cannot publish.
        nextGeneration &+= 1
        active = nil
        current.output.finish()
        current.collector?.cancel()
        await current.backend.cancel()
        await current.collector?.value
    }

    private func receive(
        _ event: SpeechAnalysisEvent,
        session id: DictationSessionID,
        generation: UInt64
    ) {
        guard var current = active,
              current.id == id,
              current.generation == generation,
              !current.failed
        else { return }

        switch event {
        case .partial(let text):
            current.output.yield(.partial(session: id, text: text))
        case .finalSegment(let text):
            current.finalSegments.append(text)
            active = current
        }
    }

    private func receiveFailure(
        _ error: any Error,
        session id: DictationSessionID,
        generation: UInt64
    ) {
        guard var current = active,
              current.id == id,
              current.generation == generation,
              !current.failed
        else { return }

        current.failed = true
        current.output.finish(throwing: map(error))
        active = current
    }

    private func complete(
        session id: DictationSessionID,
        generation: UInt64,
        audioDuration: TimeInterval
    ) {
        guard let current = active,
              current.id == id,
              current.generation == generation
        else { return }

        defer { active = nil }
        guard !current.failed else { return }

        let rawText = current.finalSegments
            .joined()
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !rawText.isEmpty else {
            current.output.finish(throwing: AppleSpeechTranscriberError.emptyFinal)
            return
        }

        current.output.yield(
            .final(
                session: id,
                rawText: rawText,
                audioDuration: audioDuration
            )
        )
        current.output.finish()
    }

    private func map(_ error: any Error) -> AppleSpeechTranscriberError {
        if let error = error as? AppleSpeechTranscriberError { return error }
        return .backendFailed(String(describing: error))
    }
}

private actor SystemSpeechAnalysisSession: SpeechAnalysisSession {
    private let speechTranscriber: SpeechTranscriber
    private let analyzer: SpeechAnalyzer
    private let eventStream: AsyncThrowingStream<SpeechAnalysisEvent, any Error>
    private let eventContinuation: AsyncThrowingStream<SpeechAnalysisEvent, any Error>.Continuation

    private var source: (any AudioFrameSource)?
    private var sessionID: DictationSessionID?
    private var analyzerInput: AsyncStream<AnalyzerInput>.Continuation?
    private var converter: AVAudioConverter?
    private var feederTask: Task<Void, Never>?
    private var resultTask: Task<Void, Never>?
    private var audioDuration: TimeInterval = 0
    private var failure: (any Error)?
    private var started = false
    private var terminal = false

    init(localeIdentifier: String) {
        let speechTranscriber = SpeechTranscriber(
            locale: Locale(identifier: localeIdentifier),
            transcriptionOptions: [],
            reportingOptions: [.volatileResults],
            attributeOptions: [.audioTimeRange]
        )
        self.speechTranscriber = speechTranscriber
        self.analyzer = SpeechAnalyzer(modules: [speechTranscriber])
        (eventStream, eventContinuation) = AsyncThrowingStream.makeStream()
    }

    func events() -> AsyncThrowingStream<SpeechAnalysisEvent, any Error> {
        eventStream
    }

    func start(source: any AudioFrameSource, session: DictationSessionID) async throws {
        guard !started else {
            throw AppleSpeechTranscriberError.backendFailed("Speech analysis session started twice")
        }
        started = true
        self.source = source
        self.sessionID = session

        guard let analyzerFormat = await SpeechAnalyzer.bestAvailableAudioFormat(
            compatibleWith: [speechTranscriber]
        ) else {
            throw AppleSpeechTranscriberError.noCompatibleAudioFormat
        }
        guard let converter = AVAudioConverter(
            from: source.captureFormat,
            to: analyzerFormat
        ) else {
            throw AppleSpeechTranscriberError.conversionFailed("Unsupported capture format")
        }
        self.converter = converter

        let (inputs, inputContinuation) = AsyncStream<AnalyzerInput>.makeStream()
        analyzerInput = inputContinuation
        try await analyzer.prepareToAnalyze(in: analyzerFormat)
        try await analyzer.start(inputSequence: inputs)

        resultTask = Task { [weak self] in
            await self?.collectResults()
        }
        feederTask = Task { [weak self] in
            await self?.feedAudio()
        }
    }

    func finish() async throws -> TimeInterval {
        guard started else {
            throw AppleSpeechTranscriberError.backendFailed("Speech analysis session was not started")
        }
        if terminal {
            if let failure { throw failure }
            return audioDuration
        }

        await source?.stopCapture()
        await feederTask?.value
        analyzerInput?.finish()

        if let failure {
            await analyzer.cancelAndFinishNow()
            resultTask?.cancel()
            await resultTask?.value
            terminal = true
            throw failure
        }

        do {
            try await analyzer.finalizeAndFinishThroughEndOfInput()
            await resultTask?.value
            if let failure { throw failure }
            eventContinuation.finish()
            terminal = true
            return audioDuration
        } catch {
            recordFailure(error)
            terminal = true
            throw error
        }
    }

    func cancel() async {
        guard !terminal else { return }
        terminal = true
        await source?.stopCapture()
        feederTask?.cancel()
        analyzerInput?.finish()
        await analyzer.cancelAndFinishNow()
        resultTask?.cancel()
        await feederTask?.value
        await resultTask?.value
        eventContinuation.finish()
    }

    private func feedAudio() async {
        guard let source, let sessionID else { return }
        do {
            let frames = try await source.startCapture(session: sessionID)
            for try await frame in frames {
                try Task.checkCancellation()
                guard frame.session == sessionID else { continue }
                audioDuration += Double(frame.buffer.frameLength) / frame.buffer.format.sampleRate
                if let converted = try convert(frame.buffer) {
                    analyzerInput?.yield(AnalyzerInput(buffer: converted))
                }
            }
            analyzerInput?.finish()
        } catch is CancellationError {
            analyzerInput?.finish()
        } catch {
            analyzerInput?.finish()
            recordFailure(error)
        }
    }

    private func collectResults() async {
        do {
            for try await result in speechTranscriber.results {
                try Task.checkCancellation()
                let text = String(result.text.characters)
                eventContinuation.yield(
                    result.isFinal ? .finalSegment(text) : .partial(text)
                )
            }
        } catch is CancellationError {
            return
        } catch {
            recordFailure(error)
        }
    }

    private func convert(_ buffer: AVAudioPCMBuffer) throws -> AVAudioPCMBuffer? {
        guard let converter else {
            throw AppleSpeechTranscriberError.conversionFailed("Audio converter is unavailable")
        }
        let ratio = converter.outputFormat.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 1_024
        guard let converted = AVAudioPCMBuffer(
            pcmFormat: converter.outputFormat,
            frameCapacity: capacity
        ) else {
            throw AppleSpeechTranscriberError.conversionFailed("Could not allocate converted audio")
        }

        let input = OneShotAudioBuffer(buffer)
        var conversionError: NSError?
        let status = converter.convert(to: converted, error: &conversionError) { _, inputStatus in
            guard let buffer = input.take() else {
                inputStatus.pointee = .noDataNow
                return nil
            }
            inputStatus.pointee = .haveData
            return buffer
        }

        if let conversionError {
            throw AppleSpeechTranscriberError.conversionFailed(conversionError.localizedDescription)
        }
        if status == .error {
            throw AppleSpeechTranscriberError.conversionFailed("AVAudioConverter returned an error")
        }
        return converted.frameLength > 0 ? converted : nil
    }

    private func recordFailure(_ error: any Error) {
        guard failure == nil else { return }
        failure = error
        eventContinuation.finish(throwing: error)
    }
}

/// AVAudioConverter marks its input callback Sendable even though it calls it synchronously.
/// Keep the one-shot mutable handoff behind a lock so strict concurrency does not rely on a
/// captured local variable.
private final class OneShotAudioBuffer: @unchecked Sendable {
    private let lock = NSLock()
    private var buffer: AVAudioPCMBuffer?

    init(_ buffer: AVAudioPCMBuffer) {
        self.buffer = buffer
    }

    func take() -> AVAudioPCMBuffer? {
        lock.withLock {
            defer { buffer = nil }
            return buffer
        }
    }
}
