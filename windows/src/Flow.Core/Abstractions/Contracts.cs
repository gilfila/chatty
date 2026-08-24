namespace Flow.Core.Abstractions;

/// <summary>Identifies one hold-to-talk session. Events carrying a stale id are discarded.</summary>
public readonly record struct SessionId(long Value)
{
    public override string ToString() => $"s{Value}";
}

/// <summary>Why a session ended without publishing text.</summary>
public enum CancelReason
{
    UserCancelled,
    ListenerFailed,
    DesktopLocked,
    SecureDesktop,
    InputLanguageChanged,
    AudioDeviceChanged,
    TargetUnavailable,
    SpeechFailed,
    Superseded,
}

/// <summary>Snapshot of the foreground field captured when recording starts.</summary>
public sealed record TargetDescriptor(
    nint WindowHandle,
    int ProcessId,
    int ThreadId,
    bool IsElevated,
    bool IsSecureField,
    string ProcessName);

public enum InsertionOutcomeKind
{
    Inserted,
    RefusedSecureTarget,
    RefusedElevatedTarget,
    TargetChanged,
    TargetGone,
    PasteFailed,
    ClipboardUnavailable,
    NoSpeechResult,
    StorageFailed,
    Cancelled,
}

/// <summary>Result of one completed session. Text is always recoverable via Copy Last
/// for every kind except NoSpeechResult and Cancelled.</summary>
public sealed record InsertionOutcome(InsertionOutcomeKind Kind, string? Detail = null)
{
    public bool TextRecoverable => Kind is not (InsertionOutcomeKind.NoSpeechResult or InsertionOutcomeKind.Cancelled);
}

// ---------------------------------------------------------------------------
// Speech (implemented by the speech workstream)
// ---------------------------------------------------------------------------

public enum SpeechReadiness { Ready, ModelDownloading, Unsupported, MicrophoneDenied, Failed }

/// <summary>Push sink the speech engine drives. Implementations must tolerate calls
/// from any thread and any time — stale-session filtering is the sink's job.</summary>
public interface ISpeechEventSink
{
    void OnPartial(SessionId session, string volatileText);
    void OnFinal(SessionId session, string finalText);
    void OnSpeechError(SessionId session, string error);
}

public interface ISpeechEngine
{
    /// <summary>Model/permission readiness. Must be Ready before dictation is accepted.</summary>
    Task<SpeechReadiness> EnsureReadyAsync(CancellationToken ct);

    /// <summary>Begin streaming recognition for the session. Audio must never be written
    /// to disk or leave the machine.</summary>
    void StartSession(SessionId session, ISpeechEventSink sink);

    /// <summary>Audio input is complete; engine should emit exactly one final for the session.</summary>
    void CompleteAudio(SessionId session);

    /// <summary>Hard stop. No partial or final for this session may be emitted afterwards
    /// (the sink discards them regardless).</summary>
    void CancelSession(SessionId session);
}

// ---------------------------------------------------------------------------
// Transcript persistence (implemented by the speech workstream)
// ---------------------------------------------------------------------------

public interface ITranscriptStore
{
    /// <summary>Durably persist the final transcript. Must return only after the text
    /// would survive a crash. Returns false on storage failure.</summary>
    bool SaveFinal(SessionId session, string rawText, string formattedText);

    /// <summary>Most recent final transcript, if any (backs the Copy Last action).</summary>
    string? GetLast();
}

// ---------------------------------------------------------------------------
// Shell services (implemented by the Windows shell)
// ---------------------------------------------------------------------------

public interface ITargetTracker
{
    /// <summary>Foreground target at this instant, or null if it cannot be resolved.</summary>
    TargetDescriptor? CaptureForeground();

    /// <summary>True when <paramref name="captured"/> is still the foreground target.</summary>
    bool IsStillForeground(TargetDescriptor captured);
}

public sealed record ClipboardToken(uint SequenceNumber);

public interface IClipboardService
{
    /// <summary>Best-effort backup of current text content; null when the clipboard holds
    /// no text or cannot be read.</summary>
    string? TryReadText();

    /// <summary>Places text on the clipboard. Returns a token capturing the clipboard
    /// sequence number after our write, or null on failure.</summary>
    ClipboardToken? TrySetText(string text);

    /// <summary>Current clipboard sequence number.</summary>
    uint GetSequenceNumber();
}

public interface IPasteInjector
{
    /// <summary>Synthesizes the paste chord into the foreground window. Returns false if
    /// injection could not be performed.</summary>
    bool SendPaste();
}

public enum PanelState { Recording, Working, Error, Hidden }

public interface IPanelPresenter
{
    void Show(PanelState state, string text);
    void Hide();
}
