import AVFoundation
import ApplicationServices
import Foundation
import FlowCore
import IOKit.hid

// FlowApp — resident menu-bar agent. Owner: Gil (M1).
//
// Current slice: the permission coordinator's probe/request core, runnable from the signed
// bundle so TCC attributes prompts and grants to Flow.app (not the terminal). The NSApplication
// + LSUIElement menu bar, hotkey registration, AVAudioEngine capture, and HUD land next.
//
// Usage (run the binary INSIDE dist/Flow.app — a bare swift-build binary inherits the
// terminal's TCC grants and proves nothing):
//   FlowApp permissions probe     print the PermissionState snapshot as JSON, never prompts
//   FlowApp permissions request   trigger the OS prompts for all three grants, then print JSON

@MainActor
func currentPermissionState() -> PermissionState {
    var state = PermissionState()

    state[.microphone] = switch AVCaptureDevice.authorizationStatus(for: .audio) {
    case .authorized: .granted
    case .denied: .denied
    case .restricted: .restricted
    case .notDetermined: .notDetermined
    @unknown default: .notDetermined
    }

    state[.inputMonitoring] = switch IOHIDCheckAccess(kIOHIDRequestTypeListenEvent) {
    case kIOHIDAccessTypeGranted: .granted
    case kIOHIDAccessTypeDenied: .denied
    default: .notDetermined
    }

    // AX has no not-determined query: untrusted covers both "never asked" and "revoked".
    // Report .denied — the recovery path (Settings deep link) is identical either way.
    state[.accessibility] = AXIsProcessTrusted() ? .granted : .denied

    return state
}

@MainActor
func printSnapshot() throws {
    struct Snapshot: Codable {
        var bundleIdentifier: String?
        var bundlePath: String
        var permissions: PermissionState
    }
    let snapshot = Snapshot(
        bundleIdentifier: Bundle.main.bundleIdentifier,
        bundlePath: Bundle.main.bundlePath,
        permissions: currentPermissionState()
    )
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
    let json = String(decoding: try encoder.encode(snapshot), as: UTF8.self)
    print(json)

    // Launching via `open` (required for correct TCC attribution — a shell-spawned child is
    // billed to the terminal, not to Flow.app) detaches stdout, so leave the evidence on disk.
    let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Flow", isDirectory: true)
    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    try json.write(to: dir.appendingPathComponent("permission-snapshot.json"), atomically: true, encoding: .utf8)
}

/// Captures ~3 seconds from the live microphone through `LiveAudioFrameSource` and leaves the
/// evidence in Application Support (same reason as the permission snapshot: `open` detaches
/// stdout). Peak/RMS are included so a run that captured only digital silence is distinguishable
/// from one that heard the room.
func runAudioProbe(seconds: Double) async throws {
    struct Probe: Codable {
        var bundleIdentifier: String?
        var sampleRate: Double
        var channels: UInt32
        var frames: Int
        var sampleFrames: Int
        var capturedSeconds: Double
        var peak: Float
        var rms: Float
        var hostTimesMonotonic: Bool
        var error: String?
    }

    let source = LiveAudioFrameSource()
    let format = source.captureFormat
    var probe = Probe(
        bundleIdentifier: Bundle.main.bundleIdentifier,
        sampleRate: format.sampleRate,
        channels: format.channelCount,
        frames: 0, sampleFrames: 0, capturedSeconds: 0,
        peak: 0, rms: 0, hostTimesMonotonic: true
    )

    do {
        let stream = try await source.startCapture(session: DictationSessionID())
        let stopper = Task {
            try? await Task.sleep(for: .seconds(seconds))
            await source.stopCapture()
        }
        var sumSquares = 0.0
        var lastHostTime: UInt64 = 0
        for try await frame in stream {
            probe.frames += 1
            probe.sampleFrames += Int(frame.buffer.frameLength)
            if frame.hostTime <= lastHostTime { probe.hostTimesMonotonic = false }
            lastHostTime = frame.hostTime
            if let data = frame.buffer.floatChannelData {
                for channel in 0..<Int(frame.buffer.format.channelCount) {
                    for i in 0..<Int(frame.buffer.frameLength) {
                        let sample = data[channel][i]
                        probe.peak = max(probe.peak, abs(sample))
                        sumSquares += Double(sample * sample)
                    }
                }
            }
        }
        stopper.cancel()
        let totalSamples = probe.sampleFrames * Int(format.channelCount)
        probe.rms = totalSamples > 0 ? Float((sumSquares / Double(totalSamples)).squareRoot()) : 0
        probe.capturedSeconds = format.sampleRate > 0
            ? Double(probe.sampleFrames) / format.sampleRate : 0
    } catch {
        probe.error = String(describing: error)
    }

    let encoder = JSONEncoder()
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
    let json = String(decoding: try encoder.encode(probe), as: UTF8.self)
    print(json)
    let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Flow", isDirectory: true)
    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    try json.write(to: dir.appendingPathComponent("audio-probe.json"), atomically: true, encoding: .utf8)
}

/// Hotkey permission spike: can this signed bundle observe global keyboard events through a
/// listen-only CGEventTap under its Input Monitoring grant?
///
/// Two-level evidence: `CGEvent.tapCreate` returns nil without the grant, so a non-nil tap is
/// the structural proof; events recorded during the window are the delivery proof. Listen-only
/// is deliberate — the dictation trigger never needs to swallow or modify events, and
/// `.listenOnly` keeps us off the Accessibility-gated modification path.
@MainActor
func runHotkeyProbe(seconds: Double) throws {
    struct Probe: Codable {
        var bundleIdentifier: String?
        var tapCreated: Bool
        var keyDowns: Int
        var keyUps: Int
        var flagsChanged: Int
        var keycodes: [Int64]
        var windowSeconds: Double
    }

    final class EventLog: @unchecked Sendable {
        private let lock = NSLock()
        private(set) var events: [(type: CGEventType, keycode: Int64)] = []
        func append(type: CGEventType, keycode: Int64) {
            lock.lock()
            events.append((type, keycode))
            lock.unlock()
        }
    }

    let log = EventLog()
    let mask: CGEventMask =
        (1 << CGEventType.keyDown.rawValue)
        | (1 << CGEventType.keyUp.rawValue)
        | (1 << CGEventType.flagsChanged.rawValue)

    let tap = CGEvent.tapCreate(
        tap: .cgSessionEventTap,
        place: .headInsertEventTap,
        options: .listenOnly,
        eventsOfInterest: mask,
        callback: { _, type, event, refcon in
            let log = Unmanaged<EventLog>.fromOpaque(refcon!).takeUnretainedValue()
            log.append(type: type, keycode: event.getIntegerValueField(.keyboardEventKeycode))
            return Unmanaged.passUnretained(event)
        },
        userInfo: Unmanaged.passUnretained(log).toOpaque()
    )

    if let tap {
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetCurrent(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        CFRunLoopRunInMode(.defaultMode, seconds, false)
        CGEvent.tapEnable(tap: tap, enable: false)
    }

    let events = log.events
    let probe = Probe(
        bundleIdentifier: Bundle.main.bundleIdentifier,
        tapCreated: tap != nil,
        keyDowns: events.count(where: { $0.type == .keyDown }),
        keyUps: events.count(where: { $0.type == .keyUp }),
        flagsChanged: events.count(where: { $0.type == .flagsChanged }),
        keycodes: Array(Set(events.map(\.keycode))).sorted(),
        windowSeconds: seconds
    )

    let encoder = JSONEncoder()
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
    let json = String(decoding: try encoder.encode(probe), as: UTF8.self)
    print(json)
    let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Flow", isDirectory: true)
    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    try json.write(to: dir.appendingPathComponent("hotkey-probe.json"), atomically: true, encoding: .utf8)
}

/// Runs the real HotkeyDictationTrigger (F13) for a fixed window and records exactly what it
/// emits, in order. This is the live proof of the DictationTrigger contract: one `pressed` per
/// hold regardless of auto-repeat, `released` on key-up, `cancelled` on Escape with the
/// following key-up swallowed.
func runTriggerProbe(seconds: Double) async throws {
    struct Probe: Codable {
        var bundleIdentifier: String?
        var triggerKeycode: Int64
        var startError: String?
        var emitted: [String]
        var windowSeconds: Double
    }

    let trigger = HotkeyDictationTrigger(triggerKeycode: 105)
    var probe = Probe(
        bundleIdentifier: Bundle.main.bundleIdentifier,
        triggerKeycode: 105,
        emitted: [],
        windowSeconds: seconds
    )

    do {
        try await trigger.start()
        let collector = Task {
            var seen: [String] = []
            for await event in trigger.events {
                seen.append(String(describing: event))
            }
            return seen
        }
        // The tap source lives on the main run loop; pump it for the window, then stop.
        await MainActor.run {
            CFRunLoopRunInMode(.defaultMode, seconds, false)
        }
        await trigger.stop()
        probe.emitted = await collector.value
    } catch {
        probe.startError = String(describing: error)
    }

    let encoder = JSONEncoder()
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
    let json = String(decoding: try encoder.encode(probe), as: UTF8.self)
    print(json)
    let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Flow", isDirectory: true)
    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    try json.write(to: dir.appendingPathComponent("trigger-probe.json"), atomically: true, encoding: .utf8)
}

/// All CLI subcommands, off the main actor. Resident mode never enters here.
func dispatch(_ arguments: [String]) async throws {
    switch arguments {
case ["permissions", "probe"]:
    try await MainActor.run { try printSnapshot() }

case ["permissions", "request"]:
    // Microphone: the only one of the three with a real in-place OS dialog.
    _ = await AVCaptureDevice.requestAccess(for: .audio)

    // Input Monitoring and Accessibility never grant inline — the OS sends the user to System
    // Settings and the answer arrives on relaunch. Fire both prompts, then report what we have.
    await MainActor.run {
        _ = IOHIDRequestAccess(kIOHIDRequestTypeListenEvent)
        // Literal because the kAXTrustedCheckOptionPrompt global is not concurrency-safe under
        // Swift 6; the key's value is stable API ("AXTrustedCheckOptionPrompt").
        _ = AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": true] as CFDictionary)
    }
    try await MainActor.run { try printSnapshot() }

case ["audio", "probe"]:
    try await runAudioProbe(seconds: 3)

case ["hotkey", "probe"]:
    try await MainActor.run { try runHotkeyProbe(seconds: 10) }

case ["trigger", "probe"]:
    try await runTriggerProbe(seconds: 10)

    default:
        print("FlowApp — resident menu-bar agent (no arguments) plus M1 proof subcommands.")
        print("FlowCore contract version: \(FlowCoreContract.version)")
        print("Subcommands: `permissions probe`, `permissions request`, `audio probe`, `hotkey probe`, `trigger probe`")
    }
}

// Entry point. Deliberately synchronous: an async main would wrap everything in a MainActor job,
// and `NSApplication.run()` inside a never-returning job starves the main queue — no other
// main-actor task (the trigger event pump, UI updates) would ever be scheduled. From synchronous
// top-level code the run loop drains the main queue normally.
let arguments = Array(CommandLine.arguments.dropFirst())

if arguments.isEmpty {
    // Plain `open Flow.app` → the resident menu-bar agent. Never returns.
    runResidentApp()
} else {
    nonisolated(unsafe) var exitCode: Int32?
    Task {
        do {
            try await dispatch(arguments)
            exitCode = 0
        } catch {
            FileHandle.standardError.write(Data("FlowApp error: \(error)\n".utf8))
            exitCode = 1
        }
    }
    // Pump until the subcommand finishes; both the CGEventTap sources and main-actor jobs need
    // this loop. `exitCode` is only touched from the main thread.
    while exitCode == nil {
        CFRunLoopRunInMode(.defaultMode, 0.05, true)
    }
    exit(exitCode ?? 1)
}
