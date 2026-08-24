using Flow.Core.Abstractions;
using Flow.Shell.Core;

namespace Flow.Shell.Core.Tests;

/// <summary>
/// The panel's one button must do something real. These cover when it is offered, when it is
/// enabled, and what actually happens when it is pressed.
/// </summary>
public sealed class PanelActionRouterTests
{
    private sealed class FakeSurface : IRecoverySurface
    {
        public bool HasRecoverableText { get; set; } = true;
        public bool CopySucceeds { get; set; } = true;
        public bool SettingsSucceeds { get; set; } = true;
        public bool RetrySucceeds { get; set; } = true;
        public bool CaptureSucceeds { get; set; } = true;

        public int CopyCalls { get; private set; }
        public int SettingsCalls { get; private set; }
        public int RetryCalls { get; private set; }
        public int CaptureCalls { get; private set; }

        public bool CopyLast()
        {
            CopyCalls++;
            return CopySucceeds;
        }

        public bool OpenMicrophoneSettings()
        {
            SettingsCalls++;
            return SettingsSucceeds;
        }

        public bool RetrySetup()
        {
            RetryCalls++;
            return RetrySucceeds;
        }

        public bool BeginShortcutCapture()
        {
            CaptureCalls++;
            return CaptureSucceeds;
        }

        public bool HasPendingClipboardRestore { get; set; }
        public ClipboardRestoreResult RestoreResult { get; set; } = ClipboardRestoreResult.Restored;
        public int RestoreCalls { get; private set; }

        public ClipboardRestoreResult RestoreClipboard()
        {
            RestoreCalls++;
            return RestoreResult;
        }
    }

    private static PanelView RecoveryPanel(string? body = "the quick brown fox") =>
        PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.Idle(), SpeechReadiness.Ready, ListenerHealth.Ok,
            LastOutcome: new InsertionOutcome(InsertionOutcomeKind.TargetChanged),
            LastTranscript: body));

    // ---- Copy last ----------------------------------------------------------

    [Fact]
    public void PressingCopyLast_PutsTheTranscriptOnTheClipboard()
    {
        var surface = new FakeSurface();
        var view = RecoveryPanel();

        Assert.Equal(PanelAction.CopyLast, view.Action);
        Assert.Equal(PanelActionResult.Copied, PanelActionRouter.Invoke(view, PanelAction.CopyLast, surface));
        Assert.Equal(1, surface.CopyCalls);
    }

    [Fact]
    public void AClipboardRefusal_IsReportedRatherThanSwallowed()
    {
        var surface = new FakeSurface { CopySucceeds = false };

        Assert.Equal(
            PanelActionResult.ClipboardUnavailable,
            PanelActionRouter.Invoke(RecoveryPanel(), PanelAction.CopyLast, surface));
    }

    [Fact]
    public void CopyLast_IsUnavailableWhenTheStoreHasDroppedTheText()
    {
        // The panel is still showing text the store no longer holds. Reporting a copy here would
        // tell the user their words are on the clipboard when they are not.
        var surface = new FakeSurface { HasRecoverableText = false };

        Assert.Equal(
            PanelActionResult.Unavailable,
            PanelActionRouter.Invoke(RecoveryPanel(), PanelAction.CopyLast, surface));
        Assert.Equal(0, surface.CopyCalls);
    }

    // ---- Enabled / disabled -------------------------------------------------

    [Fact]
    public void TheButtonIsEnabledWhenThereIsSomethingToCopy()
    {
        Assert.True(PanelActionRouter.IsActionEnabled(RecoveryPanel(), new FakeSurface()));
    }

    [Fact]
    public void TheButtonIsDisabledWhenTheStoreIsEmpty()
    {
        var surface = new FakeSurface { HasRecoverableText = false };

        Assert.False(PanelActionRouter.IsActionEnabled(RecoveryPanel(), surface));
    }

    [Fact]
    public void APanelWithNoActionIsNeverEnabled()
    {
        var listening = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.Listening(), SpeechReadiness.Ready, ListenerHealth.Ok, PartialText: "half a sen"));

        Assert.Equal(PanelAction.None, listening.Action);
        Assert.False(PanelActionRouter.IsActionEnabled(listening, new FakeSurface()));
    }

    [Fact]
    public void AHiddenPanelIsNeverEnabled()
    {
        Assert.False(PanelActionRouter.IsActionEnabled(PanelView.Hidden, new FakeSurface()));
    }

    // ---- Staleness ----------------------------------------------------------

    [Fact]
    public void AClickThatDoesNotMatchTheRenderedPanel_IsIgnored()
    {
        // The panel has moved on to a setup prompt; a Copy last click still in flight must not be
        // honoured against it.
        var setup = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.Idle(), SpeechReadiness.MicrophoneDenied, ListenerHealth.Ok));
        var surface = new FakeSurface();

        Assert.Equal(PanelAction.OpenMicrophoneSettings, setup.Action);
        Assert.Equal(PanelActionResult.Ignored, PanelActionRouter.Invoke(setup, PanelAction.CopyLast, surface));
        Assert.Equal(0, surface.CopyCalls);
    }

    [Fact]
    public void AClickOnAHiddenPanelIsIgnored()
    {
        var surface = new FakeSurface();

        Assert.Equal(
            PanelActionResult.Ignored,
            PanelActionRouter.Invoke(PanelView.Hidden, PanelAction.CopyLast, surface));
        Assert.Equal(0, surface.CopyCalls);
    }

    [Fact]
    public void InvokingNoneIsAlwaysIgnored()
    {
        Assert.Equal(
            PanelActionResult.Ignored,
            PanelActionRouter.Invoke(RecoveryPanel(), PanelAction.None, new FakeSurface()));
    }

    // ---- Setup actions ------------------------------------------------------

    [Fact]
    public void PressingOpenMicrophoneSettings_LaunchesTheSettingsPage()
    {
        var surface = new FakeSurface();
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.Idle(), SpeechReadiness.MicrophoneDenied, ListenerHealth.Ok));

        Assert.Equal(PanelActionResult.SettingsOpened,
            PanelActionRouter.Invoke(view, PanelAction.OpenMicrophoneSettings, surface));
        Assert.Equal(1, surface.SettingsCalls);
    }

    [Fact]
    public void AFailedSettingsLaunchIsReported()
    {
        var surface = new FakeSurface { SettingsSucceeds = false };
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.Idle(), SpeechReadiness.MicrophoneDenied, ListenerHealth.Ok));

        Assert.Equal(PanelActionResult.Failed,
            PanelActionRouter.Invoke(view, PanelAction.OpenMicrophoneSettings, surface));
    }

    [Fact]
    public void PressingTryAgain_RetriesSetup()
    {
        var surface = new FakeSurface();
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.Idle(), SpeechReadiness.Ready, ListenerHealth.Failed));

        Assert.Equal(PanelAction.Retry, view.Action);
        Assert.Equal(PanelActionResult.Retried, PanelActionRouter.Invoke(view, PanelAction.Retry, surface));
        Assert.Equal(1, surface.RetryCalls);
    }

    [Fact]
    public void AFailedRetryIsReported()
    {
        var surface = new FakeSurface { RetrySucceeds = false };
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.Idle(), SpeechReadiness.Ready, ListenerHealth.Failed));

        Assert.Equal(PanelActionResult.Failed, PanelActionRouter.Invoke(view, PanelAction.Retry, surface));
    }

    [Fact]
    public void PressingChangeShortcut_StartsCapture()
    {
        var surface = new FakeSurface();
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.FirstRun(), SpeechReadiness.Ready, ListenerHealth.Ok));

        Assert.Equal(PanelAction.ChangeShortcut, view.Action);
        Assert.Equal(
            PanelActionResult.ShortcutCaptureStarted,
            PanelActionRouter.Invoke(view, PanelAction.ChangeShortcut, surface));
        Assert.Equal(1, surface.CaptureCalls);
    }

    [Fact]
    public void AFailedShortcutCaptureIsReported()
    {
        var surface = new FakeSurface { CaptureSucceeds = false };
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.FirstRun(), SpeechReadiness.Ready, ListenerHealth.Ok));

        Assert.Equal(PanelActionResult.Failed,
            PanelActionRouter.Invoke(view, PanelAction.ChangeShortcut, surface));
    }

    // ---- Sweep --------------------------------------------------------------

    [Theory]
    [MemberData(nameof(PanelPresenterTests.EveryReachableState), MemberType = typeof(PanelPresenterTests))]
    public void EveryOfferedActionDoesSomething(ShellSnapshot snapshot)
    {
        var view = PanelPresenter.Present(snapshot);
        if (view.Action == PanelAction.None) return;

        var surface = new FakeSurface();
        var result = PanelActionRouter.Invoke(view, view.Action, surface);

        // The point of the whole file: a button the panel offers is never inert.
        Assert.NotEqual(PanelActionResult.Ignored, result);
        Assert.True(
            surface.CopyCalls + surface.SettingsCalls + surface.RetryCalls + surface.CaptureCalls
                + surface.RestoreCalls == 1 ||
            result == PanelActionResult.Unavailable,
            $"action {view.Action} reached no recovery behaviour");
    }

    [Theory]
    [MemberData(nameof(PanelPresenterTests.EveryReachableState), MemberType = typeof(PanelPresenterTests))]
    public void CopyLastIsOnlyEverOfferedOverFinalText(ShellSnapshot snapshot)
    {
        var view = PanelPresenter.Present(snapshot);

        if (view.Action == PanelAction.CopyLast)
        {
            Assert.False(view.BodyIsProvisional);
            Assert.False(string.IsNullOrEmpty(view.BodyText));
        }
    }
}
