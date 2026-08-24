import AppKit
import ApplicationServices
import Foundation
import FlowCore
import FlowDictation

// SystemTextInjection — the live AppKit/AX adapters behind SafeTextInjector. Owner: Gil (M3).
//
// SafeTextInjector (FlowDictation) owns every safety decision — secure-field refusal,
// revalidation before paste, guarded clipboard restore. These types only answer the four
// platform questions it asks: what is focused, what is on the pasteboard, post Cmd-V, and
// wait out the paste. All AX and NSPasteboard calls hop to the main actor.

/// Focus identity via the Accessibility API. `captureTarget` snapshots the focused AX element;
/// `validate` re-fetches and compares element identity (CFEqual on AXUIElement tokens), which is
/// what makes `.targetChanged` detection real rather than string matching.
final class AXFocusTargetInspector: FocusTargetInspecting, @unchecked Sendable {
    private let lock = NSLock()
    /// Only one dictation session is live at a time, so one retained element suffices.
    private var captured: (signature: String, element: AXUIElement)?

    func captureTarget() async -> TargetCapture {
        await MainActor.run {
            guard AXIsProcessTrusted() else { return .permissionDenied }
            guard let element = Self.focusedElement() else { return .noTarget }

            var pid: pid_t = 0
            AXUIElementGetPid(element, &pid)
            let role = Self.stringAttribute(element, kAXRoleAttribute)
            let subrole = Self.stringAttribute(element, kAXSubroleAttribute)

            let isSecure = subrole == "AXSecureTextField"
            var settable = DarwinBoolean(false)
            AXUIElementIsAttributeSettable(element, kAXValueAttribute as CFString, &settable)
            let editableRoles = ["AXTextField", "AXTextArea", "AXComboBox", "AXSearchField"]
            let isEditable = settable.boolValue || editableRoles.contains(role ?? "")

            let signature = UUID().uuidString
            lock.withLock { captured = (signature, element) }

            return .target(FocusTarget(
                processID: pid,
                bundleID: NSRunningApplication(processIdentifier: pid)?.bundleIdentifier,
                elementSignature: signature,
                isSecure: isSecure,
                isEditable: isEditable
            ))
        }
    }

    func validate(_ target: FocusTarget) async -> TargetValidation {
        let captured = lock.withLock { self.captured }
        guard let captured, captured.signature == target.elementSignature else {
            return .targetChanged
        }
        return await MainActor.run {
            guard AXIsProcessTrusted() else { return .permissionDenied }
            guard let current = Self.focusedElement() else { return .noTarget }
            return CFEqual(current, captured.element) ? .valid : .targetChanged
        }
    }

    @MainActor
    private static func focusedElement() -> AXUIElement? {
        var value: CFTypeRef?
        let error = AXUIElementCopyAttributeValue(
            AXUIElementCreateSystemWide(),
            kAXFocusedUIElementAttribute as CFString,
            &value
        )
        guard error == .success, let value, CFGetTypeID(value) == AXUIElementGetTypeID() else {
            return nil
        }
        return (value as! AXUIElement)
    }

    @MainActor
    private static func stringAttribute(_ element: AXUIElement, _ name: String) -> String? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success else {
            return nil
        }
        return value as? String
    }
}

/// The general NSPasteboard, snapshot/restore included so the injector can put the user's
/// clipboard back when its own write turns out to be the last one.
struct SystemPasteboard: PasteboardAccessing {
    func snapshot() async throws -> PasteboardSnapshot {
        await MainActor.run {
            let items = (NSPasteboard.general.pasteboardItems ?? []).map { item in
                PasteboardItemSnapshot(representations: item.types.compactMap { type in
                    item.data(forType: type).map {
                        PasteboardRepresentation(type: type.rawValue, data: $0)
                    }
                })
            }
            return PasteboardSnapshot(items: items)
        }
    }

    func writeText(_ text: String) async throws -> Int {
        await MainActor.run {
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            pasteboard.setString(text, forType: .string)
            return pasteboard.changeCount
        }
    }

    func changeCount() async -> Int {
        await MainActor.run { NSPasteboard.general.changeCount }
    }

    func restore(_ snapshot: PasteboardSnapshot) async throws {
        await MainActor.run {
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            let items = snapshot.items.map { item in
                let restored = NSPasteboardItem()
                for representation in item.representations {
                    restored.setData(
                        representation.data,
                        forType: NSPasteboard.PasteboardType(representation.type)
                    )
                }
                return restored
            }
            pasteboard.writeObjects(items)
        }
    }
}

enum PasteCommandError: Error {
    case eventCreationFailed
}

/// Posts a synthetic Cmd-V at the HID level. Runs under the app's Accessibility grant — the same
/// grant the AX inspector already requires, so this introduces no new permission.
struct CGPasteCommandPoster: PasteCommandPosting {
    func postPaste() async throws {
        try await MainActor.run {
            let source = CGEventSource(stateID: .combinedSessionState)
            let vKeycode: CGKeyCode = 9
            guard
                let down = CGEvent(keyboardEventSource: source, virtualKey: vKeycode, keyDown: true),
                let up = CGEvent(keyboardEventSource: source, virtualKey: vKeycode, keyDown: false)
            else {
                throw PasteCommandError.eventCreationFailed
            }
            down.flags = .maskCommand
            up.flags = .maskCommand
            down.post(tap: .cghidEventTap)
            up.post(tap: .cghidEventTap)
        }
    }
}

/// P0 has no acknowledgement channel from the target app, so completion is a bounded wait: give
/// the target the timeout to consume the paste, then report done. The injector's revalidation
/// before posting is what keeps this honest — the paste went to the field we verified.
struct BoundedWaitPasteCompletion: PasteCompletionWaiting {
    func waitForCompletion(timeout: Duration) async -> Bool {
        try? await Task.sleep(for: timeout)
        return true
    }
}
