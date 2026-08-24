namespace Flow.Shell.Core;

/// <summary>What actually happened when the panel's action was invoked.</summary>
public enum PanelActionResult
{
    /// <summary>The click did not correspond to the action the panel was offering.</summary>
    Ignored,

    /// <summary>The panel was offering the action but it was not usable — nothing to act on.</summary>
    Unavailable,

    /// <summary>The transcript is on the clipboard.</summary>
    Copied,

    /// <summary>There was a transcript to copy but the clipboard refused it.</summary>
    ClipboardUnavailable,

    /// <summary>The Windows microphone privacy page was opened.</summary>
    SettingsOpened,

    /// <summary>Setup was retried.</summary>
    Retried,

    /// <summary>Shortcut capture started; the next allowed key the user holds becomes the trigger.</summary>
    ShortcutCaptureStarted,

    /// <summary>The user's original clipboard content is back.</summary>
    ClipboardRestored,

    /// <summary>
    /// Something else wrote to the clipboard while the repair was pending, so Flow dropped the
    /// snapshot rather than overwrite a deliberate write. The action retires.
    /// </summary>
    ClipboardRestoreSuperseded,

    /// <summary>The action was recognised but the recovery surface failed to carry it out.</summary>
    Failed,
}

/// <summary>Outcome of a "Restore clipboard" attempt.</summary>
public enum ClipboardRestoreResult
{
    /// <summary>The user's content is back on the clipboard.</summary>
    Restored,

    /// <summary>The write failed again. The snapshot is kept and the action stays available.</summary>
    StillFailing,

    /// <summary>A foreign write superseded the repair. The snapshot was dropped, untouched.</summary>
    Superseded,

    /// <summary>There was nothing pending to restore.</summary>
    NothingPending,
}

/// <summary>What the shell can actually do on the user's behalf when the panel's button is pressed.</summary>
public interface IRecoverySurface
{
    /// <summary>Whether there is a retained transcript to put back on the clipboard.</summary>
    bool HasRecoverableText { get; }

    /// <summary>Copy the most recent transcript. False when the clipboard could not be written.</summary>
    bool CopyLast();

    /// <summary>Open the Windows microphone privacy page. False when it could not be launched.</summary>
    bool OpenMicrophoneSettings();

    /// <summary>Re-probe speech readiness and the keyboard listener. False when it still fails.</summary>
    bool RetrySetup();

    /// <summary>
    /// Begin capturing a replacement hold-to-talk key. False when capture could not be started.
    /// </summary>
    bool BeginShortcutCapture();

    /// <summary>Whether Flow is holding a clipboard snapshot it failed to restore.</summary>
    bool HasPendingClipboardRestore { get; }

    /// <summary>Put the user's original clipboard content back.</summary>
    ClipboardRestoreResult RestoreClipboard();
}

/// <summary>
/// Carries a panel button press through to a real effect.
/// </summary>
/// <remarks>
/// This exists so the panel's one button cannot be decorative. The panel raises an intent; this
/// decides whether the intent is currently valid and performs it against the recovery surface, and
/// the result comes back so the panel can say what happened rather than silently doing nothing.
///
/// <para>
/// The staleness check matters more than it looks. The panel is repainted from a snapshot, and a
/// click arrives asynchronously — so a click can land after the state has moved on. Comparing the
/// invoked action against the action the rendered view was actually offering is what stops a
/// Copy last press from being honoured against a panel that has since become a setup prompt.
/// </para>
/// </remarks>
public static class PanelActionRouter
{
    public static PanelActionResult Invoke(PanelView view, PanelAction invoked, IRecoverySurface surface)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(surface);

        // A click that does not match what is on screen right now is stale. Drop it.
        if (invoked == PanelAction.None || view.Action != invoked || !view.IsVisible)
        {
            return PanelActionResult.Ignored;
        }

        return invoked switch
        {
            PanelAction.CopyLast => CopyLast(view, surface),
            PanelAction.OpenMicrophoneSettings => surface.OpenMicrophoneSettings()
                ? PanelActionResult.SettingsOpened
                : PanelActionResult.Failed,
            PanelAction.Retry => surface.RetrySetup()
                ? PanelActionResult.Retried
                : PanelActionResult.Failed,
            PanelAction.ChangeShortcut => surface.BeginShortcutCapture()
                ? PanelActionResult.ShortcutCaptureStarted
                : PanelActionResult.Failed,
            PanelAction.RestoreClipboard => RestoreClipboard(surface),
            _ => PanelActionResult.Ignored,
        };
    }

    private static PanelActionResult RestoreClipboard(IRecoverySurface surface)
    {
        if (!surface.HasPendingClipboardRestore) return PanelActionResult.Unavailable;

        return surface.RestoreClipboard() switch
        {
            ClipboardRestoreResult.Restored => PanelActionResult.ClipboardRestored,
            ClipboardRestoreResult.Superseded => PanelActionResult.ClipboardRestoreSuperseded,
            ClipboardRestoreResult.NothingPending => PanelActionResult.Unavailable,
            _ => PanelActionResult.Failed,
        };
    }

    private static PanelActionResult CopyLast(PanelView view, IRecoverySurface surface)
    {
        // Both conditions, not either: the panel must be showing text, and the store must still
        // hold it. A panel that shows text the store has since dropped must not report a copy.
        if (string.IsNullOrEmpty(view.BodyText) || !surface.HasRecoverableText)
        {
            return PanelActionResult.Unavailable;
        }

        return surface.CopyLast() ? PanelActionResult.Copied : PanelActionResult.ClipboardUnavailable;
    }

    /// <summary>
    /// Whether the panel's button should render as usable. Drives the disabled state so a button
    /// is never offered in a form that cannot do anything.
    /// </summary>
    public static bool IsActionEnabled(PanelView view, IRecoverySurface surface)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(surface);

        if (!view.IsVisible || view.Action == PanelAction.None) return false;

        return view.Action switch
        {
            PanelAction.CopyLast => !string.IsNullOrEmpty(view.BodyText) && surface.HasRecoverableText,
            PanelAction.RestoreClipboard => surface.HasPendingClipboardRestore,
            _ => true,
        };
    }
}
