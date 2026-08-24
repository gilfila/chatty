import Foundation
import FlowCore

/// Drivable `DictationTrigger` for tests. Owner: Gil.
///
/// Lets M2 and M3 tests script `press → release`, `press → cancel`, and the auto-repeat and
/// cancel-wins cases without a real global hotkey or Input Monitoring grant.
public final class FakeDictationTrigger: DictationTrigger, @unchecked Sendable {
    public static var requiredPermissions: Set<PermissionKind> { [] }

    public let events: AsyncStream<TriggerEvent>
    private let continuation: AsyncStream<TriggerEvent>.Continuation
    private let lock = NSLock()
    private var isDown = false
    private var started = false
    private var startError: (any Error)?

    public init(startError: (any Error)? = nil) {
        (events, continuation) = AsyncStream<TriggerEvent>.makeStream()
        self.startError = startError
    }

    public func start() async throws {
        if let startError { throw startError }
        lock.withLock { started = true }
    }

    public func stop() async {
        lock.withLock { started = false }
        continuation.finish()
    }

    // MARK: - Test driving

    /// Honours the contract that auto-repeat cannot produce two `pressed` in a row.
    public func press() {
        let shouldEmit = lock.withLock { () -> Bool in
            guard started, !isDown else { return false }
            isDown = true
            return true
        }
        if shouldEmit { continuation.yield(.pressed) }
    }

    public func release() {
        let shouldEmit = lock.withLock { () -> Bool in
            guard isDown else { return false }
            isDown = false
            return true
        }
        if shouldEmit { continuation.yield(.released) }
    }

    /// Cancel wins: no `released` follows for this press.
    public func cancel() {
        let shouldEmit = lock.withLock { () -> Bool in
            guard isDown else { return false }
            isDown = false
            return true
        }
        if shouldEmit { continuation.yield(.cancelled) }
    }
}
