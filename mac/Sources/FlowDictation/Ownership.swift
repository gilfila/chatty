import FlowCore

/// # FlowDictation — owner: Tony S (M2)
///
/// This target is deliberately empty apart from this note. Per the delivery plan, the
/// `Transcriber`, `Formatter`, `TranscriptStore`, and `TextInjector` protocols are Tony S's to
/// define; Gil does not pre-empt their shapes here.
///
/// What FlowCore already guarantees you, so you do not have to invent it:
/// - `AudioFrameSource` — your input. `FlowTestSupport.FakeAudioFrameSource` scripts it in the
///   real 48kHz-mono shape of this Mac's built-in mic, including the stop-must-finish contract.
/// - `DictationSessionID` — attach to every partial/final; discard events from a stale session.
/// - `Transcript` — the persisted value type. `rawText` and `formattedText` are both required.
/// - `InjectionOutcome` — the exhaustive result your `TextInjector` returns.
///   `needsRecoverySurface` encodes which outcomes must keep the transcript reachable.
/// - `FocusTarget` — snapshot at recording start, `==` re-validated before paste.
///   `isInjectable` is `isEditable && !isSecure`.
///
/// If you need a boundary type that FlowApp also has to render, ask Gil to add it to FlowCore
/// rather than declaring it here — FlowApp does not depend on FlowDictation for value types.
public enum FlowDictation {
    /// Bumped when a FlowCore contract changes in a way M2 must react to.
    public static let contractVersion = 1
}
