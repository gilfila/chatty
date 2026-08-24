import Foundation

/// A completed dictation. Persisted BEFORE any injection is attempted (state-machine invariant 2),
/// which is what makes every injection failure recoverable rather than data loss.
///
/// P0 stores text only. There is deliberately no audio field: microphone audio is never written to
/// disk, so "retry" means re-format or re-paste retained text, never re-transcribe a recording.
public struct Transcript: Sendable, Codable, Equatable, Identifiable {
    public let id: DictationSessionID
    public let createdAt: Date
    /// Exactly what the transcriber produced, before the formatter touched it. Kept so every
    /// formatter change is reversible and inspectable.
    public let rawText: String
    /// After deterministic normalization + dictionary replacement. This is what gets injected.
    public let formattedText: String
    public let localeIdentifier: String
    /// Wall-clock seconds of audio captured, for the timing evidence the QA scorecard asks for.
    public let audioDuration: TimeInterval

    public init(
        id: DictationSessionID,
        createdAt: Date,
        rawText: String,
        formattedText: String,
        localeIdentifier: String,
        audioDuration: TimeInterval
    ) {
        self.id = id
        self.createdAt = createdAt
        self.rawText = rawText
        self.formattedText = formattedText
        self.localeIdentifier = localeIdentifier
        self.audioDuration = audioDuration
    }
}

/// The result of trying to put text into the user's focused field. Owner of the implementation:
/// Tony S (M2). The type lives in FlowCore because the menu bar and `flow diagnose` both render it.
///
/// This is exhaustive on purpose: the injector must return one of these rather than silently
/// falling back, so the HUD can always explain what happened.
public enum InjectionOutcome: String, Sendable, Codable, Equatable {
    /// Text reached the target that was focused at recording start.
    case inserted
    /// Nothing editable was focused when recording started.
    case noTarget
    /// Target is a secure field (password). Never inject; keep the transcript out of history too.
    case secureTarget
    /// Accessibility not granted. Transcript retained; Copy Last / Paste Last still work.
    case permissionDenied
    /// Focus moved between recording start and completion. Never paste into the new app.
    case targetChanged
    /// Paste was posted but the target never acknowledged within the bounded wait.
    case timedOut
    /// Session was cancelled before injection.
    case cancelled

    /// Whether the transcript must remain reachable through the recovery surface.
    /// Only a confirmed insert and a secure-field refusal end the story.
    public var needsRecoverySurface: Bool {
        switch self {
        case .inserted, .secureTarget: false
        case .noTarget, .permissionDenied, .targetChanged, .timedOut, .cancelled: true
        }
    }
}

/// Identity of the editable field focused when recording started.
///
/// Captured at `pressed`, re-validated immediately before paste. If the re-read does not equal
/// this snapshot, the injector returns `.targetChanged` and does not paste. Equality is the whole
/// safety mechanism, so any field added here must be stable for a field the user has not left.
public struct FocusTarget: Sendable, Equatable, Codable {
    public let processID: pid_t
    public let bundleID: String?
    /// Opaque, stable-per-element identity derived from the AX element. Not human-readable and
    /// never contains field contents.
    public let elementSignature: String
    public let isSecure: Bool
    public let isEditable: Bool

    public init(
        processID: pid_t,
        bundleID: String?,
        elementSignature: String,
        isSecure: Bool,
        isEditable: Bool
    ) {
        self.processID = processID
        self.bundleID = bundleID
        self.elementSignature = elementSignature
        self.isSecure = isSecure
        self.isEditable = isEditable
    }

    public var isInjectable: Bool { isEditable && !isSecure }
}
