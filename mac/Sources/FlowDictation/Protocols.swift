import Foundation
import FlowCore

/// Session-scoped text emitted by a transcriber.
public enum TranscriptionUpdate: Sendable, Equatable {
    /// Volatile text. Replaces the previous partial for this session and is never persisted.
    case partial(session: DictationSessionID, text: String)
    /// The single stable result for a completed session.
    case final(
        session: DictationSessionID,
        rawText: String,
        audioDuration: TimeInterval
    )
}

/// Converts captured audio frames into volatile partials and exactly one completed result.
public protocol Transcriber: Sendable {
    var localeIdentifier: String { get }

    func start(
        session: DictationSessionID,
        source: any AudioFrameSource
    ) async throws -> AsyncThrowingStream<TranscriptionUpdate, any Error>

    /// Stop accepting audio and allow the backend to finalize the current session.
    func finish(session: DictationSessionID) async

    /// Invalidate the session before stopping backend work so late results are discarded.
    func cancel(session: DictationSessionID) async
}

public struct FormattingResult: Sendable, Equatable {
    public let rawText: String
    public let formattedText: String

    public init(rawText: String, formattedText: String) {
        self.rawText = rawText
        self.formattedText = formattedText
    }

    public var changed: Bool { rawText != formattedText }
}

/// P0 normalization is deterministic, local, and reversible.
public protocol Formatter: Sendable {
    func format(
        _ rawText: String,
        corrections: [DictionaryCorrection]
    ) -> FormattingResult
}

public struct StoredTranscript: Sendable, Codable, Equatable, Identifiable {
    public let transcript: Transcript
    public var injectionOutcome: InjectionOutcome?
    public var updatedAt: Date

    public init(
        transcript: Transcript,
        injectionOutcome: InjectionOutcome? = nil,
        updatedAt: Date? = nil
    ) {
        self.transcript = transcript
        self.injectionOutcome = injectionOutcome
        self.updatedAt = updatedAt ?? transcript.createdAt
    }

    public var id: DictationSessionID { transcript.id }
}

public protocol TranscriptStore: Sendable {
    /// Must fail rather than overwrite when this session already has a persisted final.
    func append(_ transcript: Transcript) async throws
    func records() async throws -> [StoredTranscript]
    func search(_ query: String) async throws -> [StoredTranscript]
    func delete(id: DictationSessionID) async throws
    func recordInsertion(
        _ outcome: InjectionOutcome,
        for id: DictationSessionID,
        at date: Date
    ) async throws
    func lastSuccessfulTranscript() async throws -> Transcript?
}

public protocol DictionaryStore: Sendable {
    func corrections() async throws -> [DictionaryCorrection]
    func upsert(_ correction: DictionaryCorrection) async throws
    func delete(id: UUID) async throws
}

public struct InjectionResult: Sendable, Equatable {
    public let outcome: InjectionOutcome
    /// Diagnostic only. A changed user clipboard is intentionally never restored.
    public let clipboardRestored: Bool

    public init(outcome: InjectionOutcome, clipboardRestored: Bool = false) {
        self.outcome = outcome
        self.clipboardRestored = clipboardRestored
    }
}

public enum TargetCapture: Sendable, Equatable {
    case target(FocusTarget)
    case noTarget
    case secureTarget
    case permissionDenied
}

public protocol TextInjector: Sendable {
    /// Captures identity only; P0 never reads the field's contents.
    func captureTarget() async -> TargetCapture

    func inject(
        _ text: String,
        into target: TargetCapture,
        timeout: Duration
    ) async -> InjectionResult
}
