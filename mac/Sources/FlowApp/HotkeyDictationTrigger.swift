import Foundation
import CoreGraphics
import IOKit.hid
import FlowCore

// HotkeyDictationTrigger — the real global hold-to-talk trigger. Owner: Gil (M1).
//
// A listen-only CGEventTap (spike-proven under the Input Monitoring grant, work log addendum 2):
// the trigger never swallows or modifies user input, and never touches the Accessibility-gated
// event-modification path. The tap source runs on its own dedicated thread so a stalled main
// thread can never get the tap disabled; the shared state box locks because the callback (tap
// thread) races `stop()` (actor executor).
public actor HotkeyDictationTrigger: DictationTrigger {
    public static var requiredPermissions: Set<PermissionKind> { [.inputMonitoring] }

    public nonisolated let events: AsyncStream<TriggerEvent>

    /// Keycodes are HIToolbox virtual keys (kVK_*). Escape is fixed; the trigger key is chosen
    /// at init so the product can rebind without touching the state machine.
    public static let escapeKeycode: Int64 = 53

    /// kVK_RightOption — the product default. Present on every MacBook keyboard (unlike F13) and
    /// not the system dictation/emoji key (unlike fn/Globe), so holding it is side-effect free.
    public static let rightOptionKeycode: Int64 = 61

    private let triggerKeycode: Int64
    private let state: TriggerState
    private var tap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    private var tapRunLoop: CFRunLoop?

    public init(triggerKeycode: Int64) {
        self.triggerKeycode = triggerKeycode
        let (stream, continuation) = AsyncStream<TriggerEvent>.makeStream()
        self.events = stream
        self.state = TriggerState(continuation: continuation, triggerKeycode: triggerKeycode)
    }

    public func start() async throws {
        guard tap == nil else { return }
        guard IOHIDCheckAccess(kIOHIDRequestTypeListenEvent) == kIOHIDAccessTypeGranted else {
            throw TriggerError.permissionDenied(.inputMonitoring)
        }

        // flagsChanged is in the mask because modifier keys (Option, Command, fn, …) never
        // produce keyDown/keyUp from real hardware — their press/release arrives only as a
        // flagsChanged edge. Non-modifier trigger keys still come through keyDown/keyUp.
        let mask: CGEventMask =
            (1 << CGEventType.keyDown.rawValue)
            | (1 << CGEventType.keyUp.rawValue)
            | (1 << CGEventType.flagsChanged.rawValue)
        // The grant is pre-checked above, so a nil tap here is registration failure, not
        // permission — either way we throw rather than sit installed-but-deaf.
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .listenOnly,
            eventsOfInterest: mask,
            callback: { _, type, event, refcon in
                let state = Unmanaged<TriggerState>.fromOpaque(refcon!).takeUnretainedValue()
                state.handle(type: type, event: event)
                return Unmanaged.passUnretained(event)
            },
            userInfo: Unmanaged.passUnretained(state).toOpaque()
        ) else {
            throw TriggerError.registrationFailed("CGEvent.tapCreate returned nil")
        }

        self.tap = tap
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        runLoopSource = source

        // The tap source gets its own thread and run loop, NOT the main run loop. The OS
        // disables a tap whose run loop stalls past ~1s, and the main thread can stall (AX
        // queries against a busy app, UI work) — which was observed to eat the release edge
        // mid-hold, leaving the mic running. A dedicated loop makes tap delivery independent
        // of everything else in the process.
        let sourceBox = UnsafeSendableBox(source)
        tapRunLoop = await withCheckedContinuation { continuation in
            let thread = Thread {
                let runLoop = CFRunLoopGetCurrent()
                CFRunLoopAddSource(runLoop, sourceBox.value, .defaultMode)
                continuation.resume(returning: runLoop)
                CFRunLoopRun()
            }
            thread.name = "flow.hotkey-tap"
            thread.qualityOfService = .userInteractive
            thread.start()
        }

        CGEvent.tapEnable(tap: tap, enable: true)
        state.tapForReenable = tap
    }

    public func stop() async {
        guard let tap else { return }
        CGEvent.tapEnable(tap: tap, enable: false)
        if let runLoopSource, let tapRunLoop {
            CFRunLoopRemoveSource(tapRunLoop, runLoopSource, .defaultMode)
            CFRunLoopStop(tapRunLoop)
        }
        CFMachPortInvalidate(tap)
        self.tap = nil
        runLoopSource = nil
        tapRunLoop = nil
        state.tapForReenable = nil
        state.finish()
    }
}

/// CF run-loop objects cross into the tap thread closure by design; access is sequenced by the
/// continuation handshake, not by shared mutation.
private struct UnsafeSendableBox<T>: @unchecked Sendable {
    let value: T
    init(_ value: T) { self.value = value }
}

/// Shared between the actor and the tap callback via refcon. The hold-to-talk state machine:
///
/// - `physicallyDown` tracks the trigger key itself, so OS auto-repeat key-downs (identified by
///   `kCGKeyboardEventAutorepeat`, and by already being down) can never emit a second `pressed`.
/// - `sessionActive` tracks the logical dictation press. Escape while active emits `cancelled`
///   and clears it — the trigger key's eventual key-up then finds the session inactive and is
///   swallowed, which is exactly the "cancelled always beats a following released" contract.
private final class TriggerState: @unchecked Sendable {
    private let lock = NSLock()
    private let continuation: AsyncStream<TriggerEvent>.Continuation
    private let triggerKeycode: Int64
    private var physicallyDown = false
    private var sessionActive = false
    private var finished = false

    /// Set while started so the callback can recover from kCGEventTapDisabledByTimeout — a
    /// disabled tap is the installed-but-deaf failure the contract forbids.
    var tapForReenable: CFMachPort? {
        get { lock.withLock { _tapForReenable } }
        set { lock.withLock { _tapForReenable = newValue } }
    }
    private var _tapForReenable: CFMachPort?

    init(continuation: AsyncStream<TriggerEvent>.Continuation, triggerKeycode: Int64) {
        self.continuation = continuation
        self.triggerKeycode = triggerKeycode
    }

    func handle(type: CGEventType, event: CGEvent) {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap = tapForReenable { CGEvent.tapEnable(tap: tap, enable: true) }
            // A disabled window may have swallowed the release edge. Resync from the hardware:
            // if we think the trigger key is down but the HID state says it is not, the release
            // was lost — emit it now rather than leaving the session (and the mic) running.
            let emit: TriggerEvent? = lock.withLock {
                guard physicallyDown,
                      !CGEventSource.keyState(.combinedSessionState, key: CGKeyCode(triggerKeycode))
                else { return nil }
                return upEdge()
            }
            if let emit, !lock.withLock({ finished }) {
                continuation.yield(emit)
            }
            return
        }

        let keycode = event.getIntegerValueField(.keyboardEventKeycode)
        let isAutorepeat = event.getIntegerValueField(.keyboardEventAutorepeat) != 0

        let emit: TriggerEvent? = lock.withLock {
            switch (type, keycode) {
            case (.keyDown, triggerKeycode):
                return downEdge(isAutorepeat: isAutorepeat)
            case (.keyUp, triggerKeycode):
                return upEdge()
            case (.flagsChanged, triggerKeycode):
                // Which edge a flagsChanged event is comes from the modifier's own device bit in
                // the event flags: set = the key went down, cleared = it came back up. The
                // device-specific bit (not the generic maskAlternate etc.) is what distinguishes
                // "right Option released" from "left Option still held".
                guard let bit = Self.deviceModifierBit[triggerKeycode] else { return nil }
                let isDown = event.flags.rawValue & bit != 0
                return isDown ? downEdge(isAutorepeat: false) : upEdge()
            case (.keyDown, Self.escape):
                guard sessionActive else { return nil }
                sessionActive = false
                return .cancelled
            default:
                return nil
            }
        }
        if let emit, !lock.withLock({ finished }) {
            continuation.yield(emit)
        }
    }

    /// Callers hold `lock`.
    private func downEdge(isAutorepeat: Bool) -> TriggerEvent? {
        guard !isAutorepeat, !physicallyDown else { return nil }
        physicallyDown = true
        sessionActive = true
        return .pressed
    }

    /// Callers hold `lock`.
    private func upEdge() -> TriggerEvent? {
        physicallyDown = false
        guard sessionActive else { return nil }
        sessionActive = false
        return .released
    }

    func finish() {
        lock.withLock { finished = true }
        continuation.finish()
    }

    private static let escape = HotkeyDictationTrigger.escapeKeycode

    /// NX_DEVICE*KEYMASK bits carried in CGEventFlags, keyed by the modifier's kVK_* keycode.
    /// fn (63) has no left/right variant, so it uses the device-independent secondaryFn mask.
    private static let deviceModifierBit: [Int64: UInt64] = [
        54: 0x0000_0010, // right Command
        55: 0x0000_0008, // left Command
        56: 0x0000_0002, // left Shift
        58: 0x0000_0020, // left Option
        59: 0x0000_0001, // left Control
        60: 0x0000_0004, // right Shift
        61: 0x0000_0040, // right Option
        62: 0x0000_2000, // right Control
        63: CGEventFlags.maskSecondaryFn.rawValue,
    ]
}
