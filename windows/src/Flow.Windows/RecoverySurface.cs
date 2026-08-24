using System.Diagnostics;
using Flow.Core.Abstractions;
using Flow.Core.Session;
using Flow.Shell.Core;

namespace Flow.Windows;

/// <summary>
/// What the panel's button can actually do. Wires <see cref="PanelActionRouter"/> to the real
/// transcript store, the Windows settings pages, and setup re-probing.
/// </summary>
/// <remarks>
/// The panel offers at most one action, and this is the only thing standing between that offer and
/// a real effect. Copy last delegates to <see cref="DictationSessionMachine.CopyLast"/> so the
/// panel button and the tray menu item are the same operation rather than two implementations that
/// can drift.
/// </remarks>
public sealed class RecoverySurface : IRecoverySurface
{
    private readonly DictationSessionMachine _machine;
    private readonly ITranscriptStore _store;
    private readonly Func<SpeechReadiness> _readiness;
    private readonly Func<bool> _restartListener;
    private readonly ShortcutSettings _settings;
    private readonly Func<ushort, bool> _applyTrigger;

    private volatile string? _lastSeen;

    public RecoverySurface(
        DictationSessionMachine machine,
        ITranscriptStore store,
        Func<SpeechReadiness> readiness,
        Func<bool> restartListener,
        ShortcutSettings settings,
        Func<ushort, bool> applyTrigger)
    {
        _machine = machine;
        _store = store;
        _readiness = readiness;
        _restartListener = restartListener;
        _settings = settings;
        _applyTrigger = applyTrigger;
    }

    /// <summary>Raised after the trigger changes, so the first-run card can name the new key.</summary>
    public event Action<ushort>? TriggerChanged;

    /// <summary>
    /// Set by the composition root once the fidelity path is wired. Null (the default) means
    /// the surface reports no pending restore and the panel never offers the action — which
    /// is correct while the fidelity route stays prohibited from production.
    /// </summary>
    public ClipboardRestoreRecovery? ClipboardRecovery { get; set; }

    public bool HasPendingClipboardRestore => ClipboardRecovery?.HasPendingRestore ?? false;

    public ClipboardRestoreResult RestoreClipboard() =>
        ClipboardRecovery?.Retry() ?? ClipboardRestoreResult.NothingPending;

    /// <summary>
    /// Record the transcript the panel is currently showing, so the button's enabled state matches
    /// what is actually recoverable even before the store has been consulted.
    /// </summary>
    public void NoteTranscript(string? text) => _lastSeen = string.IsNullOrEmpty(text) ? null : text;

    public bool HasRecoverableText => _store.GetLast() is not null || _lastSeen is not null;

    public bool CopyLast() => _machine.CopyLast();

    public bool OpenMicrophoneSettings() => Launch("ms-settings:privacy-microphone");

    public bool RetrySetup()
    {
        // Both halves have to come back healthy: a working microphone with a dead keyboard hook
        // still means the shortcut does nothing.
        var listenerOk = _restartListener();
        return listenerOk && _readiness() == SpeechReadiness.Ready;
    }

    /// <summary>
    /// Move to the next vetted trigger key and remember it.
    /// </summary>
    /// <remarks>
    /// Cycling rather than capturing a live keystroke: see <see cref="ShortcutCatalog.Next"/>. The
    /// listener is updated first, so a failure to persist the preference still leaves the user with
    /// a working shortcut for this session rather than a setting that disagrees with the hook.
    /// </remarks>
    public bool BeginShortcutCapture()
    {
        var next = ShortcutCatalog.Next(_settings.TriggerKey);
        if (!_applyTrigger(next)) return false;

        _settings.SetTrigger(next);
        TriggerChanged?.Invoke(next);
        return true;
    }

    private static bool Launch(string uri)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            // A missing settings page is not worth taking the app down for; the panel reports it.
            return false;
        }
    }
}
