import Foundation

/// Identity for one press-to-release dictation attempt.
///
/// Every audio frame, partial, final transcript and injection outcome carries the ID of the
/// session it belongs to. The pipeline MUST discard any event whose ID is not the current
/// session — this is how late results from a cancelled or superseded session are prevented from
/// being pasted into a field the user has since moved on from.
public struct DictationSessionID: Hashable, Sendable, Codable, CustomStringConvertible {
    public let rawValue: UUID

    public init(rawValue: UUID = UUID()) {
        self.rawValue = rawValue
    }

    public var description: String { rawValue.uuidString }
}

/// The P0 state machine. Owner: Gil (FlowApp drives it; FlowDictation only reports into it).
///
/// ```
///                 ┌──────────────────────── cancelled ◄─── (Escape, key-up before speech,
///                 │                              │           app quit, mic interruption)
///                 ▼                              │
///   idle ──► requestingPermissions ──► listening ──► finalizing ──► inserting ──► idle
///     ▲               │                    │             │              │
///     │               └──────────┬─────────┴─────────────┴──────────────┘
///     │                          ▼
///     └────────────────── recoverableFailure
/// ```
///
/// Invariants:
/// 1. `requestingPermissions` is entered only for a grant the *selected* API actually needs.
/// 2. A transcript is persisted on the `finalizing` → `inserting` edge, BEFORE any paste is
///    attempted. Reaching `recoverableFailure` from `inserting` therefore never loses text.
/// 3. `cancelled` and `recoverableFailure` both return to `idle`; neither is terminal.
/// 4. Only one session may be non-`idle` at a time.
public enum DictationState: Sendable, Equatable {
    case idle
    case requestingPermissions(PermissionKind)
    case listening(DictationSessionID)
    case finalizing(DictationSessionID)
    case inserting(DictationSessionID)
    case cancelled(DictationSessionID)
    case recoverableFailure(DictationFailure)
}

/// Why a session stopped short. Every case must be presentable to the user with a recovery step.
public enum DictationFailure: Sendable, Equatable, Codable {
    case permissionDenied(PermissionKind)
    case audioUnavailable(String)
    case transcriptionFailed(String)
    case injectionFailed(InjectionOutcome)
    case internalError(String)
}

/// Callbacks from a running session. Owner: Gil. `FlowApp` implements this to drive menu-bar and
/// HUD state; the dictation pipeline calls it.
///
/// All methods are called on the main actor so UI can update without hopping.
@MainActor
public protocol DictationSessionDelegate: AnyObject {
    func dictationSession(_ id: DictationSessionID, didChangeState state: DictationState)

    /// Volatile, unstable text. Replaces any previously reported partial for this session.
    /// Never persisted, never injected.
    func dictationSession(_ id: DictationSessionID, didProducePartial text: String)

    /// Exactly one per completed session, already persisted by the time this is called.
    func dictationSession(_ id: DictationSessionID, didFinalize transcript: Transcript)

    func dictationSession(_ id: DictationSessionID, didAttemptInjection outcome: InjectionOutcome)

    func dictationSession(_ id: DictationSessionID, didFail failure: DictationFailure)
}
