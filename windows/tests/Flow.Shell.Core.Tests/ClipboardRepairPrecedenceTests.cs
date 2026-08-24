using Flow.Core.Abstractions;
using Flow.Shell.Core;

namespace Flow.Shell.Core.Tests;

/// <summary>
/// Where the clipboard repair sits relative to everything else the panel could be showing.
/// </summary>
/// <remarks>
/// The panel offers exactly one action, so precedence is not cosmetic — it decides which recovery
/// the user is given and which they have to go hunting for. The repair holds the only copy of the
/// user's own clipboard content, in memory, so it wins.
///
/// <para>
/// <see cref="ClipboardRestorePanelEndToEndTests"/> covers the repair mechanism end to end
/// (forced failure, foreign write, stale clicks). This file covers only the ordering and the
/// on-screen hygiene, which are presenter decisions.
/// </para>
/// </remarks>
public sealed class ClipboardRepairPrecedenceTests
{
    private static ShellSnapshot Snapshot(
        bool pending,
        SpeechReadiness readiness = SpeechReadiness.Ready,
        ListenerHealth listener = ListenerHealth.Ok,
        string? transcript = null,
        InsertionOutcome? outcome = null) =>
        new(new ShellPhase.Idle(), readiness, listener,
            LastOutcome: outcome,
            LastTranscript: transcript,
            ClipboardRestorePending: pending);

    [Fact]
    public void ABrokenClipboardOutranksADeniedMicrophone()
    {
        // The permission problem persists and will resurface the moment this clears. The user's
        // damaged data is one click from being fixed, so hiding the repair behind setup would be
        // the wrong way round.
        var view = PanelPresenter.Present(Snapshot(pending: true, readiness: SpeechReadiness.MicrophoneDenied));

        Assert.Equal(PanelAction.RestoreClipboard, view.Action);
    }

    [Fact]
    public void ABrokenClipboardOutranksADeadListener()
    {
        var view = PanelPresenter.Present(Snapshot(pending: true, listener: ListenerHealth.Failed));

        Assert.Equal(PanelAction.RestoreClipboard, view.Action);
    }

    [Fact]
    public void ABrokenClipboardOutranksAModelDownload()
    {
        var view = PanelPresenter.Present(Snapshot(pending: true, readiness: SpeechReadiness.ModelDownloading));

        Assert.Equal(PanelAction.RestoreClipboard, view.Action);
    }

    [Fact]
    public void ABrokenClipboardOutranksCopyLast_ButSaysWhereTheTranscriptWent()
    {
        var view = PanelPresenter.Present(Snapshot(
            pending: true,
            transcript: "a transcript that also needs recovering",
            outcome: new InsertionOutcome(InsertionOutcomeKind.TargetChanged)));

        Assert.Equal(PanelAction.RestoreClipboard, view.Action);

        // Only one action fits, so the panel must not silently strand the other recovery.
        Assert.Contains("Flow menu", view.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoTranscriptPendingItDoesNotMentionAMenuThatHasNothingInIt()
    {
        var view = PanelPresenter.Present(Snapshot(pending: true));

        Assert.DoesNotContain("Flow menu", view.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRepairNeverAutoDismisses()
    {
        // The snapshot lives in memory only. A timer that hides this action loses the content.
        var view = PanelPresenter.Present(Snapshot(pending: true));

        Assert.Null(view.AutoDismissAfter);
        Assert.Equal("Restore clipboard", view.ActionLabel);
    }

    [Fact]
    public void TheRepairPanelNeverPutsTheClipboardContentsOnScreen()
    {
        // The snapshot is held in memory precisely so it is never exposed. Rendering it would put
        // whatever the user had copied — a password, a card number — on screen and into any
        // screenshot they take of the problem.
        var view = PanelPresenter.Present(Snapshot(pending: true, transcript: "dictated text"));

        Assert.Null(view.BodyText);
    }

    [Fact]
    public void OnceTheRepairClearsTheNormalPrecedenceReturns()
    {
        var withRepair = Snapshot(pending: true, readiness: SpeechReadiness.MicrophoneDenied);
        var afterRepair = withRepair with { ClipboardRestorePending = false };

        Assert.Equal(PanelAction.RestoreClipboard, PanelPresenter.Present(withRepair).Action);
        Assert.Equal(PanelAction.OpenMicrophoneSettings, PanelPresenter.Present(afterRepair).Action);
    }

    [Fact]
    public void ARepairPendingDuringDictationDoesNotInterruptListening()
    {
        // Speaking outranks a repair the user can do at any time; interrupting the live panel
        // mid-sentence to show a button would be worse than waiting.
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.Listening(), SpeechReadiness.Ready, ListenerHealth.Ok,
            PartialText: "half a sentence",
            ClipboardRestorePending: true));

        Assert.Equal("Listening", view.Headline);
        Assert.Equal(PanelAction.None, view.Action);
        Assert.True(view.WaveformActive);
    }
}
