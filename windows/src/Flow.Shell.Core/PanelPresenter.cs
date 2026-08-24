using Flow.Core.Abstractions;

namespace Flow.Shell.Core;

/// <summary>Colour and weight of the panel. Never the only signal — every tone also has words.</summary>
public enum PanelTone
{
    Idle,
    Listening,
    Working,
    Success,
    Caution,
    Error,
}

/// <summary>The single action the panel may offer. There is never more than one.</summary>
public enum PanelAction
{
    None,
    CopyLast,
    OpenMicrophoneSettings,
    Retry,

    /// <summary>Pick a different hold-to-talk key. Only offered during first-run setup.</summary>
    ChangeShortcut,

    /// <summary>Put the user's original clipboard contents back after a restore failure.
    /// Offered until it succeeds, is superseded by a foreign write, or the app exits.</summary>
    RestoreClipboard,
}

/// <summary>Health of the global keyboard listener. Shell-owned; no Windows consent gate exists.</summary>
public enum ListenerHealth
{
    Ok,
    NotInstalled,
    Failed,
}

/// <summary>Where the shell is in the hold-to-talk loop.</summary>
public abstract record ShellPhase
{
    private ShellPhase() { }

    public sealed record Idle : ShellPhase;

    public sealed record Listening : ShellPhase;

    public sealed record Finalizing : ShellPhase;

    public sealed record Inserting : ShellPhase;

    public sealed record Cancelled(CancelReason Reason) : ShellPhase;

    /// <summary>
    /// Flow has never completed a dictation on this machine, so the user does not yet know the
    /// shortcut. Shown until their first successful dictation rather than for a fixed number of
    /// launches — the card exists to teach one gesture, and it has done its job the moment the
    /// gesture works.
    /// </summary>
    public sealed record FirstRun : ShellPhase;
}

/// <summary>Everything the top-right panel renders, as one immutable value.</summary>
/// <param name="BodyIsProvisional">
/// True while the body is a live partial. The panel renders provisional text dimmer and never
/// offers it for copying, so the user is never handed text that is about to change.
/// </param>
/// <param name="AutoDismissAfter">
/// How long the panel lingers before hiding itself, or null when it must stay until the user acts.
/// Anything holding recoverable text is null — the panel never takes the last copy of the user's
/// words off screen on a timer.
/// </param>
public sealed record PanelView(
    bool IsVisible,
    PanelTone Tone,
    string Headline,
    string Detail,
    string? BodyText,
    bool BodyIsProvisional,
    bool WaveformActive,
    PanelAction Action,
    string? ActionLabel,
    TimeSpan? AutoDismissAfter)
{
    public static readonly PanelView Hidden = new(
        false, PanelTone.Idle, string.Empty, string.Empty, null, false, false, PanelAction.None, null, null);
}

/// <summary>Everything the presenter needs in order to decide what the panel shows.</summary>
public sealed record ShellSnapshot(
    ShellPhase Phase,
    SpeechReadiness Readiness,
    ListenerHealth Listener,
    double ModelDownloadFraction = 0,
    CaptureDecision? Capture = null,
    string? PartialText = null,
    string? LastTranscript = null,
    InsertionOutcome? LastOutcome = null,
    ushort TriggerKey = ShortcutCatalog.Default,
    bool ClipboardRestorePending = false);

/// <summary>
/// Maps shell state to exactly what the top-right panel shows. Pure, so the interaction design is
/// testable rather than scattered through XAML event handlers.
/// </summary>
/// <remarks>
/// Three rules hold across every branch, and the test suite asserts each of them directly against
/// every reachable state rather than case by case.
///
/// <list type="number">
/// <item><description><b>One action at a time.</b> The panel is a glanceable strip beside the
/// clock, not a dialog. It offers at most one button, so there is never a decision to make
/// mid-sentence.</description></item>
/// <item><description><b>Recoverable text never auto-hides.</b> Any state holding the only copy of
/// what the user said has a null auto-dismiss and a Copy last action. Success and cancellation
/// fade on their own, because nothing is at stake.</description></item>
/// <item><description><b>A problem Flow already knows about is said before the user speaks.</b>
/// An administrator window or a missing text field is on screen the moment the shortcut goes down,
/// not twenty seconds later.</description></item>
/// </list>
/// </remarks>
public static class PanelPresenter
{
    private static readonly TimeSpan SuccessLinger = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan CancelLinger = TimeSpan.FromMilliseconds(1200);

    public static PanelView Present(ShellSnapshot snapshot) => snapshot.Phase switch
    {
        ShellPhase.Listening => Listening(snapshot),
        ShellPhase.Finalizing => Working("Turning that into text…", snapshot.PartialText),
        ShellPhase.Inserting => Working("Inserting…", snapshot.PartialText),
        ShellPhase.Cancelled cancelled => Cancelled(cancelled.Reason),
        ShellPhase.FirstRun => FirstRun(snapshot),
        _ => Idle(snapshot),
    };

    /// <summary>
    /// The one thing a new user has to learn. Setup problems outrank it, because teaching a
    /// gesture that cannot work yet is worse than saying nothing.
    /// </summary>
    private static PanelView FirstRun(ShellSnapshot snapshot)
    {
        if (Setup(snapshot) is { } blocked) return blocked;

        var key = ShortcutCatalog.Describe(snapshot.TriggerKey);

        return new PanelView(
            IsVisible: true,
            Tone: PanelTone.Idle,
            Headline: $"Hold {key} to dictate",
            Detail: "Hold it down, say something, then let go. Flow types it where your cursor already is.",
            BodyText: null,
            BodyIsProvisional: false,
            WaveformActive: false,
            Action: PanelAction.ChangeShortcut,
            ActionLabel: "Change shortcut",
            // Stays until the user has actually done it once.
            AutoDismissAfter: null);
    }

    private static PanelView Idle(ShellSnapshot snapshot)
    {
        // A broken clipboard outranks even setup. It is the user's own data, damaged by Flow
        // borrowing it to paste, and it is one click from being fixed — whereas a setup problem
        // persists and will resurface the moment this clears. Hiding a one-click repair of the
        // user's data behind a permission prompt would be the wrong way round.
        if (snapshot.ClipboardRestorePending)
        {
            return ClipboardRepair(snapshot);
        }

        // Setup outranks the rest. Without these the shortcut does nothing at all, and a
        // shortcut that silently does nothing is the one failure this app must never have.
        if (Setup(snapshot) is { } setup)
        {
            return setup;
        }

        if (snapshot.LastOutcome is { } outcome)
        {
            return outcome.TextRecoverable && outcome.Kind != InsertionOutcomeKind.Inserted
                ? Recovery(outcome, snapshot.LastTranscript)
                : Finished(outcome, snapshot.LastTranscript);
        }

        return PanelView.Hidden;
    }

    /// <summary>
    /// Flow borrowed the clipboard, could not put it back, and is still holding the only copy of
    /// what was there. The snapshot lives in memory only, so this action is the user's one route
    /// back and the panel keeps it until it succeeds or Flow exits.
    /// </summary>
    private static PanelView ClipboardRepair(ShellSnapshot snapshot)
    {
        var hasTranscript = !string.IsNullOrEmpty(snapshot.LastTranscript);

        return new PanelView(
            IsVisible: true,
            Tone: PanelTone.Caution,
            Headline: "Your clipboard needs restoring",
            Detail: hasTranscript
                // Only one action fits, and the user's own data outranks Flow's output — the
                // transcript is still reachable from the tray menu, so say where it went.
                ? "Flow borrowed your clipboard to paste and could not put it back. Your transcript is in the Flow menu."
                : "Flow borrowed your clipboard to paste and could not put it back.",
            BodyText: null,
            BodyIsProvisional: false,
            WaveformActive: false,
            Action: PanelAction.RestoreClipboard,
            ActionLabel: "Restore clipboard",
            AutoDismissAfter: null);
    }

    private static PanelView? Setup(ShellSnapshot snapshot)
    {
        // Listener first: it is the only one of these the user cannot discover for themselves,
        // because a dead hook looks exactly like a working one until you hold the key.
        if (snapshot.Listener is ListenerHealth.NotInstalled or ListenerHealth.Failed)
        {
            return new PanelView(
                IsVisible: true,
                Tone: PanelTone.Error,
                Headline: "Shortcut not working",
                Detail: "Flow could not watch for your shortcut, so hold-to-talk will not respond.",
                BodyText: null,
                BodyIsProvisional: false,
                WaveformActive: false,
                Action: PanelAction.Retry,
                ActionLabel: "Try again",
                AutoDismissAfter: null);
        }

        return snapshot.Readiness switch
        {
            SpeechReadiness.ModelDownloading => new PanelView(
                IsVisible: true,
                Tone: PanelTone.Working,
                Headline: "Getting speech ready",
                Detail: $"Windows is downloading the on-device model — {Percent(snapshot.ModelDownloadFraction)}%.",
                BodyText: null,
                BodyIsProvisional: false,
                WaveformActive: false,
                Action: PanelAction.None,
                ActionLabel: null,
                AutoDismissAfter: null),

            SpeechReadiness.MicrophoneDenied => new PanelView(
                IsVisible: true,
                Tone: PanelTone.Error,
                Headline: "Microphone blocked",
                Detail: "Flow needs the microphone to hear you. Audio is turned into text on this PC and is never saved or uploaded.",
                BodyText: null,
                BodyIsProvisional: false,
                WaveformActive: false,
                Action: PanelAction.OpenMicrophoneSettings,
                ActionLabel: "Open microphone settings",
                AutoDismissAfter: null),

            SpeechReadiness.Unsupported => new PanelView(
                IsVisible: true,
                Tone: PanelTone.Error,
                Headline: "Not available on this PC",
                Detail: "Flow needs Windows 11 24H2 or newer for on-device speech.",
                BodyText: null,
                BodyIsProvisional: false,
                WaveformActive: false,
                Action: PanelAction.None,
                ActionLabel: null,
                AutoDismissAfter: null),

            SpeechReadiness.Failed => new PanelView(
                IsVisible: true,
                Tone: PanelTone.Error,
                Headline: "Speech unavailable",
                Detail: "Windows could not start on-device speech.",
                BodyText: null,
                BodyIsProvisional: false,
                WaveformActive: false,
                Action: PanelAction.Retry,
                ActionLabel: "Try again",
                AutoDismissAfter: null),

            _ => null,
        };
    }

    private static PanelView Listening(ShellSnapshot snapshot)
    {
        // Rule 3: if Flow already knows the paste cannot land, it says so now rather than after.
        var recordOnly = snapshot.Capture is { Admission: TargetAdmission.RecordOnly };

        return new PanelView(
            IsVisible: true,
            Tone: recordOnly ? PanelTone.Caution : PanelTone.Listening,
            Headline: "Listening",
            Detail: recordOnly ? snapshot.Capture!.Value.Reason : "Release to insert · Esc to cancel",
            BodyText: snapshot.PartialText,
            BodyIsProvisional: true,
            WaveformActive: true,
            Action: PanelAction.None,
            ActionLabel: null,
            AutoDismissAfter: null);
    }

    private static PanelView Working(string headline, string? partial) => new(
        IsVisible: true,
        Tone: PanelTone.Working,
        Headline: headline,
        Detail: string.Empty,
        BodyText: partial,
        BodyIsProvisional: true,
        WaveformActive: false,
        Action: PanelAction.None,
        ActionLabel: null,
        AutoDismissAfter: null);

    private static PanelView Cancelled(CancelReason reason) => new(
        IsVisible: true,
        Tone: PanelTone.Idle,
        Headline: "Cancelled",
        Detail: CancelDetail(reason),
        BodyText: null,
        BodyIsProvisional: false,
        WaveformActive: false,
        Action: PanelAction.None,
        ActionLabel: null,
        AutoDismissAfter: CancelLinger);

    private static string CancelDetail(CancelReason reason) => reason switch
    {
        CancelReason.UserCancelled => "Nothing was typed or kept.",
        CancelReason.ListenerFailed => "Flow lost the shortcut and stopped listening.",
        CancelReason.DesktopLocked => "Windows locked, so Flow stopped listening.",
        CancelReason.SecureDesktop => "Windows showed a secure screen, so Flow stopped listening.",
        CancelReason.InputLanguageChanged => "The keyboard layout changed, so Flow stopped listening.",
        CancelReason.AudioDeviceChanged => "The microphone changed, so Flow stopped listening.",
        CancelReason.TargetUnavailable => "The text field went away, so Flow stopped listening.",
        CancelReason.SpeechFailed => "Speech stopped unexpectedly.",
        CancelReason.Superseded => "A newer dictation replaced this one.",
        _ => string.Empty,
    };

    private static PanelView Finished(InsertionOutcome outcome, string? transcript)
    {
        var inserted = outcome.Kind == InsertionOutcomeKind.Inserted;

        return new PanelView(
            IsVisible: true,
            Tone: inserted ? PanelTone.Success : PanelTone.Idle,
            Headline: inserted ? "Inserted" : "Done",
            Detail: Explain(outcome.Kind),
            BodyText: inserted ? transcript : null,
            BodyIsProvisional: false,
            WaveformActive: false,
            Action: PanelAction.None,
            ActionLabel: null,
            AutoDismissAfter: SuccessLinger);
    }

    private static PanelView Recovery(InsertionOutcome outcome, string? transcript) => new(
        IsVisible: true,
        Tone: outcome.Kind == InsertionOutcomeKind.RefusedSecureTarget ? PanelTone.Idle : PanelTone.Caution,
        Headline: outcome.Kind == InsertionOutcomeKind.RefusedSecureTarget ? "Not kept" : "Saved, not typed",
        Detail: Explain(outcome.Kind),
        BodyText: transcript,
        BodyIsProvisional: false,
        WaveformActive: false,
        Action: transcript is null ? PanelAction.None : PanelAction.CopyLast,
        ActionLabel: transcript is null ? null : "Copy last",
        // Rule 2: this panel is holding the only copy of the user's words. It does not time out.
        AutoDismissAfter: null);

    /// <summary>One sentence saying what happened and where the text is. Never blames the user.</summary>
    private static string Explain(InsertionOutcomeKind kind) => kind switch
    {
        InsertionOutcomeKind.Inserted => "Inserted.",
        InsertionOutcomeKind.RefusedSecureTarget => "That was a password field. Flow did not type or keep it.",
        InsertionOutcomeKind.RefusedElevatedTarget => "That app runs as administrator, so Windows blocks typing into it. Use Copy last.",
        InsertionOutcomeKind.TargetChanged => "Focus moved to a different field, so Flow did not type there. Use Copy last.",
        InsertionOutcomeKind.TargetGone => "The text field went away, so Flow kept the text for you.",
        InsertionOutcomeKind.PasteFailed => "The app did not accept the paste. Use Copy last.",
        InsertionOutcomeKind.ClipboardUnavailable => "Flow could not reach the clipboard. The text is still here.",
        InsertionOutcomeKind.NoSpeechResult => "Flow did not catch anything.",
        InsertionOutcomeKind.StorageFailed => "Flow could not save that transcript.",
        InsertionOutcomeKind.Cancelled => "Cancelled.",
        _ => string.Empty,
    };

    private static int Percent(double fraction) => (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
}
