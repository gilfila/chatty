using Flow.Core.Abstractions;
using Flow.Core.Session;
using Flow.Shell.Core;

namespace Flow.Windows;

/// <summary>
/// Composition root. The speech engine and transcript store below are placeholders that
/// keep the shell runnable stand-alone; the speech workstream's SpeechAnalyzer-equivalent
/// adapter (Windows on-device streaming recognition) and encrypted store replace them here
/// at integration. Audio never touches this project — the shell only sees text events.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var engine = new PlaceholderSpeechEngine();
        var store = new InMemoryTranscriptStore();
        var targets = new ForegroundTargetTracker();
        var clipboard = new ClipboardService();
        var paste = new PasteInjector();
        var panel = new TranscriptPanelWindow();
        var settings = ShortcutSettings.Load();

        var machine = new DictationSessionMachine(
            engine, store, targets, clipboard, paste, panel,
            readinessProbe: () => engine.CachedReadiness,
            time: TimeProvider.System);

        panel.Start();

        using var listener = new KeyboardHookListener(settings.TriggerKey);

        var recovery = new RecoverySurface(
            machine,
            store,
            readiness: () => engine.CachedReadiness,
            restartListener: listener.Restart,
            settings: settings,
            applyTrigger: listener.SetTrigger);

        // Shell state the panel needs but the session machine does not model.
        ShellSnapshot Snapshot(ShellPhase phase, InsertionOutcome? outcome = null, string? transcript = null) =>
            new(phase,
                engine.CachedReadiness,
                listener.IsInstalled ? ListenerHealth.Ok : ListenerHealth.Failed,
                LastOutcome: outcome,
                LastTranscript: transcript,
                TriggerKey: settings.TriggerKey,
                ClipboardRestorePending: recovery.HasPendingClipboardRestore);

        // A clipboard Flow borrowed and could not put back is the user's own data, and the
        // snapshot holding it lives in memory only — so the moment it becomes pending, the panel
        // has to surface the repair, and the moment it clears, the panel has to stop offering it.
        void ShowClipboardRepairState() =>
            panel.Render(PanelPresenter.Present(Snapshot(new ShellPhase.Idle())));

        void ShowFirstRunIfNeeded()
        {
            if (!settings.HasCompletedFirstDictation)
            {
                panel.Render(PanelPresenter.Present(Snapshot(new ShellPhase.FirstRun())));
            }
        }

        // Re-render the card so it names the key the user just switched to.
        recovery.TriggerChanged += _ => ShowFirstRunIfNeeded();

        if (recovery.ClipboardRecovery is { } clipboardRecovery)
        {
            clipboardRecovery.PendingChanged += _ => ShowClipboardRepairState();
        }

        // The panel's one button has to reach a real effect. The router decides whether the press
        // is still valid for what is on screen, then performs it against the recovery surface.
        panel.ActionInvoked += action =>
        {
            var view = panel.CurrentView;
            var result = PanelActionRouter.Invoke(view, action, recovery);
            if (result == PanelActionResult.Copied)
            {
                panel.Render(view with { Headline = "Copied", Detail = "The transcript is on your clipboard." });
            }
            else if (result == PanelActionResult.ClipboardRestored)
            {
                panel.Render(new PanelView(
                    IsVisible: true,
                    Tone: PanelTone.Success,
                    Headline: "Clipboard restored",
                    Detail: "Your original clipboard content is back.",
                    BodyText: null,
                    BodyIsProvisional: false,
                    WaveformActive: false,
                    Action: PanelAction.None,
                    ActionLabel: null,
                    AutoDismissAfter: TimeSpan.FromMilliseconds(1600)));
            }
            else if (result == PanelActionResult.ClipboardRestoreSuperseded)
            {
                // Somebody put something on the clipboard deliberately. Flow dropped its snapshot
                // rather than overwrite it, and says so instead of silently giving up.
                panel.Render(view with
                {
                    Tone = PanelTone.Idle,
                    Headline = "Clipboard already changed",
                    Detail = "Something else copied since then, so Flow left it alone.",
                    Action = PanelAction.None,
                    ActionLabel = null,
                    AutoDismissAfter = TimeSpan.FromMilliseconds(2400),
                });
            }
            else if (result == PanelActionResult.Failed && view.Action == PanelAction.RestoreClipboard)
            {
                // The repair stays available: this snapshot is the only copy of their content.
                panel.Render(view with { Detail = "That did not work. Your clipboard content is still here." });
            }
            else if (result is PanelActionResult.ClipboardUnavailable or PanelActionResult.Failed)
            {
                panel.Render(view with { Detail = "That did not work. The text is still here." });
            }
        };

        // Render the outcome panel from the shell rather than from Core. The session machine only
        // speaks the thin IPanelPresenter contract, and giving Core a dependency on PanelView would
        // invert the project reference — Flow.Shell.Core already depends on Flow.Core. Mapping the
        // completion event here keeps Core UI-agnostic and makes recovery reachable in the product.
        machine.SessionCompleted += (_, outcome) =>
        {
            var text = store.GetLast();
            if (outcome.TextRecoverable) recovery.NoteTranscript(text);

            panel.Render(PanelPresenter.Present(Snapshot(new ShellPhase.Idle(), outcome, text)));

            // The first-run card teaches one gesture. It retires the moment the gesture works,
            // not after a fixed number of launches.
            if (outcome.Kind == InsertionOutcomeKind.Inserted)
            {
                settings.MarkFirstDictationComplete();
            }
        };

        listener.TriggerPressed += machine.OnKeyPressed;
        listener.TriggerReleased += machine.OnKeyReleased;
        listener.TriggerCancelled += reason => machine.Cancel(reason);
        listener.ListenerFailed += _ => machine.Cancel(CancelReason.ListenerFailed);
        listener.Start();
        ShowFirstRunIfNeeded();

        using var host = new ShellHost();
        host.CopyLastRequested += () => machine.CopyLast();
        host.DesktopLocked += () => machine.Cancel(CancelReason.DesktopLocked);
        host.Run();

        panel.Dispose();
    }
}

/// <summary>Stand-in until the on-device speech adapter lands: reports itself unavailable
/// so the shell shows a truthful error state instead of pretending to transcribe.</summary>
internal sealed class PlaceholderSpeechEngine : ISpeechEngine
{
    public SpeechReadiness CachedReadiness => SpeechReadiness.Failed;

    public Task<SpeechReadiness> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(CachedReadiness);
    public void StartSession(SessionId session, ISpeechEventSink sink) { }
    public void CompleteAudio(SessionId session) { }
    public void CancelSession(SessionId session) { }
}

/// <summary>Stand-in until the encrypted, bounded transcript store lands.</summary>
internal sealed class InMemoryTranscriptStore : ITranscriptStore
{
    private string? _last;

    public bool SaveFinal(SessionId session, string rawText, string formattedText)
    {
        _last = formattedText;
        return true;
    }

    public string? GetLast() => _last;
}
