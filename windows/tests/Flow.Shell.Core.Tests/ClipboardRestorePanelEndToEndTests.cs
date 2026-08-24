using Flow.Core.Abstractions;

namespace Flow.Shell.Core.Tests;

/// <summary>
/// End-to-end coverage of the "Restore clipboard" recovery flow through the real presenter,
/// router, recovery holder, and fidelity policy — only the raw clipboard is fake:
/// a forced restore failure surfaces the action; a retry clears it only after actual
/// restoration; a foreign clipboard write removes it without any overwrite attempt.
/// (The Windows RecoverySurface delegates to ClipboardRestoreRecovery exactly as the
/// surface here does; its Win32 side is Pollen's real-machine gate.)
/// </summary>
public sealed class ClipboardRestorePanelEndToEndTests
{
    private const uint CF_UNICODETEXT = 13;

    private sealed class EndToEndRawClipboard : Flow.Shell.Core.IRawClipboard
    {
        public uint Sequence = 60;
        public bool SetAlwaysFails;
        public int EmptyCount;
        public int SetAttempts;
        public readonly List<uint> RestoredFormats = [];

        public bool TryOpen() => true;
        public void Close() { }
        public uint GetSequenceNumber() => Sequence;
        public IReadOnlyList<uint> EnumerateFormats() => [];
        public byte[]? TryReadFormatBytes(uint format) => null;
        public object? TryAllocate(Flow.Shell.Core.ClipboardEntry entry) => entry;
        public void FreeAllocation(object allocation) { }

        public bool TryEmpty()
        {
            EmptyCount++;
            return true;
        }

        public bool TrySetAllocated(uint format, object allocation)
        {
            SetAttempts++;
            if (SetAlwaysFails) return false;
            RestoredFormats.Add(format);
            return true;
        }
    }

    /// <summary>The same delegation the Windows RecoverySurface performs.</summary>
    private sealed class Surface(Flow.Shell.Core.ClipboardRestoreRecovery recovery) : Flow.Shell.Core.IRecoverySurface
    {
        public bool HasRecoverableText => false;
        public bool CopyLast() => false;
        public bool OpenMicrophoneSettings() => false;
        public bool RetrySetup() => false;
        public bool BeginShortcutCapture() => false;
        public bool HasPendingClipboardRestore => recovery.HasPendingRestore;
        public Flow.Shell.Core.ClipboardRestoreResult RestoreClipboard() => recovery.Retry();
    }

    private static Flow.Shell.Core.ShellSnapshot IdleSnapshot(bool pending) => new(
        new Flow.Shell.Core.ShellPhase.Idle(),
        SpeechReadiness.Ready,
        Flow.Shell.Core.ListenerHealth.Ok,
        ClipboardRestorePending: pending);

    private static (Flow.Shell.Core.ClipboardRestoreRecovery Recovery, Surface Surface, EndToEndRawClipboard Raw) Make()
    {
        var raw = new EndToEndRawClipboard();
        var recovery = new Flow.Shell.Core.ClipboardRestoreRecovery(
            new Flow.Shell.Core.ClipboardFidelityPolicy(raw), raw);
        return (recovery, new Surface(recovery), raw);
    }

    private static Flow.Shell.Core.ClipboardSnapshot OriginalClipboard() =>
        new([new Flow.Shell.Core.ClipboardEntry(CF_UNICODETEXT, [42])], 60);

    [Fact]
    public void ForcedRestoreFailure_MakesRestoreClipboardVisibleAndEnabled()
    {
        var (recovery, surface, raw) = Make();
        raw.SetAlwaysFails = true;

        // The paste path's restore fails persistently → the shell holds the snapshot.
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);
        Assert.False(policy.TryRestoreIfSequenceMatches(OriginalClipboard(), 60));
        recovery.HoldForRetry(OriginalClipboard(), raw.GetSequenceNumber());

        var view = Flow.Shell.Core.PanelPresenter.Present(IdleSnapshot(pending: recovery.HasPendingRestore));

        Assert.True(view.IsVisible);
        Assert.Equal(Flow.Shell.Core.PanelAction.RestoreClipboard, view.Action);
        Assert.Equal("Restore clipboard", view.ActionLabel);
        Assert.Null(view.AutoDismissAfter); // stays until the user acts
        Assert.True(Flow.Shell.Core.PanelActionRouter.IsActionEnabled(view, surface));
    }

    [Fact]
    public void Retry_ClearsTheAction_OnlyAfterSuccessfulRestoration()
    {
        var (recovery, surface, raw) = Make();
        recovery.HoldForRetry(OriginalClipboard(), 60);
        raw.SetAlwaysFails = true;

        var view = Flow.Shell.Core.PanelPresenter.Present(IdleSnapshot(recovery.HasPendingRestore));

        // Still failing: the click reports failure and the action survives.
        Assert.Equal(
            Flow.Shell.Core.PanelActionResult.Failed,
            Flow.Shell.Core.PanelActionRouter.Invoke(view, Flow.Shell.Core.PanelAction.RestoreClipboard, surface));
        Assert.True(recovery.HasPendingRestore);
        Assert.True(
            Flow.Shell.Core.PanelPresenter.Present(IdleSnapshot(recovery.HasPendingRestore)).Action
                == Flow.Shell.Core.PanelAction.RestoreClipboard);

        // Fixed: the same click path restores for real and the card goes away.
        raw.SetAlwaysFails = false;
        view = Flow.Shell.Core.PanelPresenter.Present(IdleSnapshot(recovery.HasPendingRestore));
        Assert.Equal(
            Flow.Shell.Core.PanelActionResult.ClipboardRestored,
            Flow.Shell.Core.PanelActionRouter.Invoke(view, Flow.Shell.Core.PanelAction.RestoreClipboard, surface));
        Assert.False(recovery.HasPendingRestore);
        Assert.Equal([CF_UNICODETEXT], raw.RestoredFormats);
        Assert.False(Flow.Shell.Core.PanelPresenter.Present(IdleSnapshot(recovery.HasPendingRestore)).IsVisible);
    }

    [Fact]
    public void ForeignClipboardWrite_RemovesTheAction_WithoutAnyOverwriteAttempt()
    {
        var (recovery, surface, raw) = Make();
        recovery.HoldForRetry(OriginalClipboard(), 60);
        var view = Flow.Shell.Core.PanelPresenter.Present(IdleSnapshot(recovery.HasPendingRestore));

        raw.Sequence = 61; // another app wrote after the failure — their content wins

        Assert.Equal(
            Flow.Shell.Core.PanelActionResult.ClipboardRestoreSuperseded,
            Flow.Shell.Core.PanelActionRouter.Invoke(view, Flow.Shell.Core.PanelAction.RestoreClipboard, surface));
        Assert.False(recovery.HasPendingRestore);
        Assert.Equal(0, raw.EmptyCount);   // the foreign write was never touched
        Assert.Equal(0, raw.SetAttempts);
        Assert.False(Flow.Shell.Core.PanelPresenter.Present(IdleSnapshot(recovery.HasPendingRestore)).IsVisible);
    }

    [Fact]
    public void StaleClick_AfterRecoveryCleared_IsUnavailableNotHonoured()
    {
        var (recovery, surface, raw) = Make();
        recovery.HoldForRetry(OriginalClipboard(), 60);
        var staleView = Flow.Shell.Core.PanelPresenter.Present(IdleSnapshot(recovery.HasPendingRestore));

        Assert.Equal(Flow.Shell.Core.ClipboardRestoreResult.Restored, recovery.Retry()); // resolved through another path (e.g. tray)
        raw.SetAttempts = 0;
        raw.EmptyCount = 0;

        Assert.Equal(
            Flow.Shell.Core.PanelActionResult.Unavailable,
            Flow.Shell.Core.PanelActionRouter.Invoke(staleView, Flow.Shell.Core.PanelAction.RestoreClipboard, surface));
        Assert.Equal(0, raw.SetAttempts);
        Assert.Equal(0, raw.EmptyCount);
        Assert.False(Flow.Shell.Core.PanelActionRouter.IsActionEnabled(staleView, surface));
    }
}
