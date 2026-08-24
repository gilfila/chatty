import Foundation

/// TCC grants this app can need. Owner: Gil (M1).
///
/// `inputMonitoring` and `accessibility` are separate grants in System Settings and are NOT
/// interchangeable. Which of them a hotkey needs depends on the chosen API, so the app asks only
/// for `DictationTrigger.requiredPermissions` of the implementation actually in use.
// CodingKeyRepresentable so `[PermissionKind: PermissionStatus]` encodes as a JSON object
// ({"microphone": "granted"}) instead of Codable's flat key/value array. Additive, not
// source-breaking for FlowCoreContract.
public enum PermissionKind: String, Sendable, Codable, CaseIterable, CodingKeyRepresentable {
    case microphone
    case inputMonitoring
    case accessibility

    /// Deep link into the correct System Settings pane for recovery after a denial.
    public var settingsURLString: String {
        switch self {
        case .microphone:
            "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone"
        case .inputMonitoring:
            "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent"
        case .accessibility:
            "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"
        }
    }

    /// Shown verbatim to the user before the OS prompt, so the request is never unexplained.
    public var rationale: String {
        switch self {
        case .microphone:
            "Flow needs the microphone to hear what you dictate. Audio is transcribed on this Mac and is never recorded to disk or uploaded."
        case .inputMonitoring:
            "Flow needs Input Monitoring to notice your push-to-talk shortcut while another app is focused."
        case .accessibility:
            "Flow needs Accessibility to type finished text into the field you were using. Without it, Flow still keeps every transcript and you can paste it yourself."
        }
    }
}

public enum PermissionStatus: String, Sendable, Codable, Equatable {
    case notDetermined
    case granted
    case denied
    case restricted
    /// The selected API does not need this grant at all — do not prompt, do not show as missing.
    case notRequired
}

/// A snapshot of every grant, for the menu bar and for `flow diagnose`.
public struct PermissionState: Sendable, Codable, Equatable {
    public var statuses: [PermissionKind: PermissionStatus]

    public init(statuses: [PermissionKind: PermissionStatus] = [:]) {
        self.statuses = statuses
    }

    public subscript(kind: PermissionKind) -> PermissionStatus {
        get { statuses[kind] ?? .notDetermined }
        set { statuses[kind] = newValue }
    }

    /// Grants that are required, still missing, and worth prompting for.
    public func blocking(required: Set<PermissionKind>) -> [PermissionKind] {
        required
            .filter { self[$0] != .granted && self[$0] != .notRequired }
            .sorted { $0.rawValue < $1.rawValue }
    }

    /// Dictation can run without Accessibility — the transcript is simply kept for manual paste
    /// instead of injected. Losing the mic or the hotkey, by contrast, is fatal to the loop.
    public func canDictate(triggerPermissions: Set<PermissionKind>) -> Bool {
        guard self[.microphone] == .granted else { return false }
        return triggerPermissions
            .filter { $0 != .accessibility }
            .allSatisfy { self[$0] == .granted || self[$0] == .notRequired }
    }
}
