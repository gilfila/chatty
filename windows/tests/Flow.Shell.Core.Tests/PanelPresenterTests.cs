using Flow.Core.Abstractions;
using Flow.Shell.Core;

namespace Flow.Shell.Core.Tests;

/// <summary>
/// The panel is the whole visible product on Windows, so its rules are asserted as rules —
/// swept across every reachable state — rather than case by case.
/// </summary>
public sealed class PanelPresenterTests
{
    private static ShellSnapshot Ready(ShellPhase phase) =>
        new(phase, SpeechReadiness.Ready, ListenerHealth.Ok);

    // ---------------------------------------------------------------------
    // Rule 2 — recoverable text never disappears on a timer
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(InsertionOutcomeKind.RefusedElevatedTarget)]
    [InlineData(InsertionOutcomeKind.TargetChanged)]
    [InlineData(InsertionOutcomeKind.TargetGone)]
    [InlineData(InsertionOutcomeKind.PasteFailed)]
    [InlineData(InsertionOutcomeKind.ClipboardUnavailable)]
    [InlineData(InsertionOutcomeKind.StorageFailed)]
    public void AnOutcomeHoldingTheOnlyCopy_OffersCopyLastAndNeverAutoDismisses(InsertionOutcomeKind kind)
    {
        var view = PanelPresenter.Present(Ready(new ShellPhase.Idle()) with
        {
            LastOutcome = new InsertionOutcome(kind),
            LastTranscript = "the quick brown fox",
        });

        Assert.True(view.IsVisible);
        Assert.Null(view.AutoDismissAfter);
        Assert.Equal(PanelAction.CopyLast, view.Action);
        Assert.Equal("the quick brown fox", view.BodyText);
        Assert.False(view.BodyIsProvisional);
    }

    [Fact]
    public void EveryRecoverableOutcome_ExplainsItselfInWords()
    {
        foreach (var kind in Enum.GetValues<InsertionOutcomeKind>())
        {
            var view = PanelPresenter.Present(Ready(new ShellPhase.Idle()) with
            {
                LastOutcome = new InsertionOutcome(kind),
                LastTranscript = "text",
            });

            Assert.False(string.IsNullOrWhiteSpace(view.Headline));
            Assert.False(string.IsNullOrWhiteSpace(view.Detail));
        }
    }

    [Fact]
    public void ASuccessfulInsert_ConfirmsAndThenGetsOutOfTheWay()
    {
        var view = PanelPresenter.Present(Ready(new ShellPhase.Idle()) with
        {
            LastOutcome = new InsertionOutcome(InsertionOutcomeKind.Inserted),
            LastTranscript = "hello there",
        });

        Assert.Equal(PanelTone.Success, view.Tone);
        Assert.Equal(PanelAction.None, view.Action);
        Assert.NotNull(view.AutoDismissAfter);
    }

    [Fact]
    public void APasswordField_LeavesNothingBehindAndOffersNoCopy()
    {
        var view = PanelPresenter.Present(Ready(new ShellPhase.Idle()) with
        {
            LastOutcome = new InsertionOutcome(InsertionOutcomeKind.RefusedSecureTarget),
            LastTranscript = null,
        });

        Assert.Null(view.BodyText);
        Assert.Equal(PanelAction.None, view.Action);
        Assert.Contains("password", view.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancelling_FadesOnItsOwnBecauseNothingIsAtStake()
    {
        var view = PanelPresenter.Present(Ready(new ShellPhase.Cancelled(CancelReason.UserCancelled)));

        Assert.NotNull(view.AutoDismissAfter);
        Assert.Equal(PanelAction.None, view.Action);
    }

    [Fact]
    public void EveryCancelReason_SaysWhatHappened()
    {
        foreach (var reason in Enum.GetValues<CancelReason>())
        {
            var view = PanelPresenter.Present(Ready(new ShellPhase.Cancelled(reason)));
            Assert.False(string.IsNullOrWhiteSpace(view.Detail));
        }
    }

    // ---------------------------------------------------------------------
    // Rule 3 — a knowable problem is stated before the user speaks
    // ---------------------------------------------------------------------

    [Fact]
    public void HoldingOverAnAdministratorWindow_WarnsWhileListening_NotAfterwards()
    {
        var capture = CaptureAdmissionPolicy.Decide(Flow.Core.Insertion.TargetVerdict.Elevated, "Notepad");

        var view = PanelPresenter.Present(Ready(new ShellPhase.Listening()) with
        {
            Capture = capture,
            PartialText = "make a note",
        });

        Assert.Equal(PanelTone.Caution, view.Tone);
        Assert.Contains("administrator", view.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Notepad", view.Detail, StringComparison.Ordinal);
        Assert.True(view.WaveformActive);
    }

    [Fact]
    public void HoldingOverANormalField_ShowsTheShortcutContractNotAWarning()
    {
        var capture = CaptureAdmissionPolicy.Decide(Flow.Core.Insertion.TargetVerdict.Ok, "Notepad");

        var view = PanelPresenter.Present(Ready(new ShellPhase.Listening()) with
        {
            Capture = capture,
            PartialText = "make a note",
        });

        Assert.Equal(PanelTone.Listening, view.Tone);
        Assert.Contains("Esc", view.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void LivePartialText_IsMarkedProvisionalSoItIsNeverOfferedForCopying()
    {
        var view = PanelPresenter.Present(Ready(new ShellPhase.Listening()) with { PartialText = "half a sen" });

        Assert.True(view.BodyIsProvisional);
        Assert.Equal(PanelAction.None, view.Action);
    }

    // ---------------------------------------------------------------------
    // Setup and failure states
    // ---------------------------------------------------------------------

    [Fact]
    public void ADeadListener_IsSurfacedAboveEverythingElse()
    {
        // A hook that failed to install looks identical to a working one until you hold the key,
        // so it outranks even a speech-model download.
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.Idle(), SpeechReadiness.ModelDownloading, ListenerHealth.Failed));

        Assert.Equal(PanelTone.Error, view.Tone);
        Assert.Equal("Shortcut not working", view.Headline);
        Assert.Equal(PanelAction.Retry, view.Action);
    }

    [Fact]
    public void ModelDownload_ShowsConcreteProgressAndBlocksNothingElse()
    {
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.Idle(), SpeechReadiness.ModelDownloading, ListenerHealth.Ok,
            ModelDownloadFraction: 0.42));

        Assert.Equal(PanelTone.Working, view.Tone);
        Assert.Contains("42%", view.Detail, StringComparison.Ordinal);
        Assert.Null(view.AutoDismissAfter);
    }

    [Fact]
    public void ADeniedMicrophone_ExplainsTheTradeAndLinksToTheFix()
    {
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.Idle(), SpeechReadiness.MicrophoneDenied, ListenerHealth.Ok));

        Assert.Equal(PanelAction.OpenMicrophoneSettings, view.Action);
        Assert.Contains("never saved", view.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnsupportedWindows_SaysSoPlainlyAndOffersNoFalseHope()
    {
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.Idle(), SpeechReadiness.Unsupported, ListenerHealth.Ok));

        Assert.Equal(PanelAction.None, view.Action);
        Assert.Contains("24H2", view.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingHasHappenedYet_ShowsNoPanelAtAll()
    {
        var view = PanelPresenter.Present(Ready(new ShellPhase.Idle()));

        Assert.False(view.IsVisible);
        Assert.Same(PanelView.Hidden, view);
    }

    // ---------------------------------------------------------------------
    // First run
    // ---------------------------------------------------------------------

    [Fact]
    public void FirstRun_TeachesTheGestureAndNamesTheActualKey()
    {
        var view = PanelPresenter.Present(Ready(new ShellPhase.FirstRun()));

        Assert.True(view.IsVisible);
        Assert.Equal("Hold Right Ctrl to dictate", view.Headline);
        Assert.Equal(PanelAction.ChangeShortcut, view.Action);
        Assert.Null(view.AutoDismissAfter);
        Assert.False(view.WaveformActive);
    }

    [Fact]
    public void FirstRun_NamesWhicheverKeyIsActuallyBound()
    {
        var view = PanelPresenter.Present(
            Ready(new ShellPhase.FirstRun()) with { TriggerKey = ShortcutCatalog.VK_SCROLL });

        Assert.Contains("Scroll Lock", view.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstRun_NeverNamesAKeyItWouldRefuseToBind()
    {
        // A stored right-Alt binding must not be taught to the user as if it were valid.
        var view = PanelPresenter.Present(
            Ready(new ShellPhase.FirstRun()) with { TriggerKey = ShortcutCatalog.Resolve(ShortcutCatalog.VK_RMENU) });

        Assert.DoesNotContain("Alt", view.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstRun_YieldsToASetupProblemBecauseTheGestureCannotWorkYet()
    {
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.FirstRun(), SpeechReadiness.MicrophoneDenied, ListenerHealth.Ok));

        Assert.Equal("Microphone blocked", view.Headline);
        Assert.Equal(PanelAction.OpenMicrophoneSettings, view.Action);
    }

    [Fact]
    public void FirstRun_YieldsToADeadListenerToo()
    {
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.FirstRun(), SpeechReadiness.Ready, ListenerHealth.Failed));

        Assert.Equal("Shortcut not working", view.Headline);
    }

    // ---------------------------------------------------------------------
    // Rule 1 — swept across every reachable state
    // ---------------------------------------------------------------------

    public static TheoryData<ShellSnapshot> EveryReachableState()
    {
        var data = new TheoryData<ShellSnapshot>();

        foreach (var readiness in Enum.GetValues<SpeechReadiness>())
        foreach (var listener in Enum.GetValues<ListenerHealth>())
        {
            data.Add(new ShellSnapshot(new ShellPhase.Idle(), readiness, listener));
            data.Add(new ShellSnapshot(new ShellPhase.FirstRun(), readiness, listener));
            data.Add(new ShellSnapshot(new ShellPhase.Listening(), readiness, listener, PartialText: "words"));
            data.Add(new ShellSnapshot(new ShellPhase.Finalizing(), readiness, listener, PartialText: "words"));
            data.Add(new ShellSnapshot(new ShellPhase.Inserting(), readiness, listener, PartialText: "words"));

            foreach (var reason in Enum.GetValues<CancelReason>())
            {
                data.Add(new ShellSnapshot(new ShellPhase.Cancelled(reason), readiness, listener));
            }

            foreach (var kind in Enum.GetValues<InsertionOutcomeKind>())
            {
                data.Add(new ShellSnapshot(new ShellPhase.Idle(), readiness, listener,
                    LastOutcome: new InsertionOutcome(kind), LastTranscript: "words"));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryReachableState))]
    public void ThePanelNeverOffersMoreThanOneAction(ShellSnapshot snapshot)
    {
        var view = PanelPresenter.Present(snapshot);

        // The type enforces at most one action; what this guards is the pairing, so the shell can
        // render a button without ever finding a label missing or an orphan label with no action.
        if (view.Action == PanelAction.None)
        {
            Assert.Null(view.ActionLabel);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(view.ActionLabel));
        }
    }

    [Theory]
    [MemberData(nameof(EveryReachableState))]
    public void AVisiblePanelAlwaysHasAHeadline(ShellSnapshot snapshot)
    {
        var view = PanelPresenter.Present(snapshot);

        if (view.IsVisible)
        {
            Assert.False(string.IsNullOrWhiteSpace(view.Headline));
        }
    }

    [Theory]
    [MemberData(nameof(EveryReachableState))]
    public void APanelOfferingCopyLast_NeverTimesOut(ShellSnapshot snapshot)
    {
        var view = PanelPresenter.Present(snapshot);

        if (view.Action == PanelAction.CopyLast)
        {
            Assert.Null(view.AutoDismissAfter);
            Assert.False(string.IsNullOrWhiteSpace(view.BodyText));
        }
    }

    [Theory]
    [MemberData(nameof(EveryReachableState))]
    public void ProvisionalTextIsNeverPairedWithAnAction(ShellSnapshot snapshot)
    {
        var view = PanelPresenter.Present(snapshot);

        // Offering "Copy last" over text that is still changing would hand the user a truncated
        // sentence and call it their transcript.
        if (view.BodyIsProvisional && view.BodyText is not null)
        {
            Assert.Equal(PanelAction.None, view.Action);
        }
    }

    [Theory]
    [MemberData(nameof(EveryReachableState))]
    public void TheWaveformOnlyMovesWhileActuallyListening(ShellSnapshot snapshot)
    {
        var view = PanelPresenter.Present(snapshot);

        if (view.WaveformActive)
        {
            Assert.IsType<ShellPhase.Listening>(snapshot.Phase);
        }
    }
}
