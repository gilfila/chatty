import Foundation

/// Version of the published M0 boundary (`AudioFrameSource`, `DictationTrigger`,
/// `PermissionState`, `DictationSessionDelegate`, `Transcript`, `InjectionOutcome`, `FocusTarget`).
///
/// Owner: Gil. Bump on any source-breaking change and say so in the channel — M1 and M2 build
/// against this in parallel, so a silent contract change costs the other side a rebuild loop.
public enum FlowCoreContract {
    public static let version = 1
}
