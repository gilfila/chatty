import Foundation

/// Hold-to-talk edges from the global shortcut. Owner: Gil (M1).
public enum TriggerEvent: Sendable, Equatable {
    /// Shortcut went down — begin a session.
    case pressed
    /// Shortcut came up — stop capture and finalize.
    case released
    /// Escape, or any abort that must discard rather than finalize.
    case cancelled
}

/// The global push-to-talk shortcut. Owner: Gil (M1).
///
/// Contract:
/// - `events` never emits two `pressed` without an intervening `released`/`cancelled`. The
///   consumer does not have to debounce key auto-repeat.
/// - `cancelled` always wins: after it, no `released` is emitted for that press.
/// - `start()` throws `TriggerError.permissionDenied` rather than silently registering nothing —
///   a hotkey that appears installed but never fires is the worst failure mode here.
public protocol DictationTrigger: AnyObject, Sendable {
    var events: AsyncStream<TriggerEvent> { get }
    /// The permissions this trigger implementation actually needs. Recorded per-implementation so
    /// the app prompts for the minimum set rather than speculatively asking for all three.
    static var requiredPermissions: Set<PermissionKind> { get }

    func start() async throws
    func stop() async
}

public enum TriggerError: Error, Sendable, Equatable {
    case permissionDenied(PermissionKind)
    case shortcutUnavailable(String)
    case registrationFailed(String)
}
