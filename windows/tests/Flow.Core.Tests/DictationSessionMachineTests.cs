using Flow.Core.Abstractions;
using Flow.Core.Session;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Flow.Core.Tests;

public sealed class Harness
{
    public readonly FakeSpeechEngine Engine = new();
    public readonly FakeStore Store = new();
    public readonly FakeTargets Targets = new();
    public readonly FakeClipboard Clipboard = new();
    public readonly FakePaste Paste = new();
    public readonly FakePanel Panel = new();
    public readonly FakeTimeProvider Time = new();
    public readonly DictationSessionMachine Machine;
    public readonly List<(SessionId Id, InsertionOutcome Outcome)> Outcomes = [];
    private readonly AutoResetEvent _completed = new(false);

    public Harness()
    {
        Machine = new DictationSessionMachine(
            Engine, Store, Targets, Clipboard, Paste, Panel,
            () => Engine.Readiness, Time);
        Machine.SessionCompleted += (id, o) =>
        {
            lock (Outcomes) Outcomes.Add((id, o));
            _completed.Set();
        };
    }

    /// <summary>Advances past the paste settle delay so a pending insertion finishes.</summary>
    public void SettlePaste() => Time.Advance(TimeSpan.FromSeconds(1));

    public InsertionOutcome WaitOutcome()
    {
        Assert.True(_completed.WaitOne(TimeSpan.FromSeconds(5)), "no session outcome arrived");
        lock (Outcomes) return Outcomes[^1].Outcome;
    }

    public SessionId CurrentSession => Engine.Started[^1];
}

public class DictationSessionMachineTests
{
    [Fact]
    public void HappyPath_PressSpeakRelease_InsertsAndHidesPanel()
    {
        var h = new Harness();

        h.Machine.OnKeyPressed();
        Assert.Equal(MachineState.Recording, h.Machine.State);
        var id = h.CurrentSession;

        h.Engine.Sink!.OnPartial(id, "hello");
        Assert.Equal(PanelState.Recording, h.Panel.Current);
        Assert.Equal("hello", h.Panel.LastText);

        h.Machine.OnKeyReleased();
        Assert.Equal(MachineState.Finalizing, h.Machine.State);
        Assert.Equal([id], h.Engine.Completed);

        h.Engine.Sink!.OnFinal(id, "hello world ");
        h.SettlePaste();

        var outcome = h.WaitOutcome();
        Assert.Equal(InsertionOutcomeKind.Inserted, outcome.Kind);
        Assert.Equal(MachineState.Idle, h.Machine.State);
        Assert.Equal(PanelState.Hidden, h.Panel.Current);
        Assert.Single(h.Store.Saved);
        Assert.Equal("hello world", h.Store.Saved[0].Formatted);
        Assert.Equal(1, h.Paste.PasteCount);
    }

    [Fact]
    public void Transcript_IsPersisted_BeforePasteAttempt()
    {
        var h = new Harness();
        h.Paste.OnPaste = () => Assert.Single(h.Store.Saved);

        h.Machine.OnKeyPressed();
        h.Machine.OnKeyReleased();
        h.Engine.Sink!.OnFinal(h.CurrentSession, "durable first");
        h.SettlePaste();

        Assert.Equal(InsertionOutcomeKind.Inserted, h.WaitOutcome().Kind);
        Assert.Equal(1, h.Paste.PasteCount);
    }

    [Fact]
    public void LateFinal_AfterCancel_IsDiscarded()
    {
        var h = new Harness();
        h.Machine.OnKeyPressed();
        var id = h.CurrentSession;
        h.Machine.OnKeyReleased();

        h.Machine.Cancel(CancelReason.DesktopLocked);
        Assert.Equal([id], h.Engine.Cancelled);

        h.Engine.Sink!.OnFinal(id, "too late");
        h.SettlePaste();

        Assert.Empty(h.Store.Saved);
        Assert.Equal(0, h.Paste.PasteCount);
        Assert.Equal(MachineState.Idle, h.Machine.State);
    }

    [Fact]
    public void DuplicateFinal_InsertsExactlyOnce()
    {
        var h = new Harness();
        h.Machine.OnKeyPressed();
        var id = h.CurrentSession;
        h.Machine.OnKeyReleased();

        h.Engine.Sink!.OnFinal(id, "once");
        h.Engine.Sink!.OnFinal(id, "twice");
        h.SettlePaste();

        Assert.Equal(InsertionOutcomeKind.Inserted, h.WaitOutcome().Kind);
        Assert.Single(h.Store.Saved);
        Assert.Equal("once", h.Store.Saved[0].Formatted);
        Assert.Equal(1, h.Paste.PasteCount);
    }

    [Fact]
    public void MissingFinal_TimesOut_WithNoInsert()
    {
        var h = new Harness();
        h.Machine.OnKeyPressed();
        var id = h.CurrentSession;
        h.Machine.OnKeyReleased();

        h.Time.Advance(TimeSpan.FromSeconds(6));

        var outcome = h.WaitOutcome();
        Assert.Equal(InsertionOutcomeKind.NoSpeechResult, outcome.Kind);
        Assert.Equal([id], h.Engine.Cancelled);
        Assert.Empty(h.Store.Saved);
        Assert.Equal(0, h.Paste.PasteCount);
        Assert.Equal(MachineState.Idle, h.Machine.State);

        h.Engine.Sink!.OnFinal(id, "after timeout");
        Assert.Empty(h.Store.Saved);
    }

    [Fact]
    public void NewPress_WhileFinalizing_SupersedesOldSession()
    {
        var h = new Harness();
        h.Machine.OnKeyPressed();
        var first = h.CurrentSession;
        h.Machine.OnKeyReleased();

        h.Machine.OnKeyPressed();
        var second = h.CurrentSession;
        Assert.NotEqual(first, second);
        Assert.Contains(first, h.Engine.Cancelled);

        h.Engine.Sink!.OnFinal(first, "stale text");
        Assert.Empty(h.Store.Saved);

        h.Machine.OnKeyReleased();
        h.Engine.Sink!.OnFinal(second, "fresh text");
        h.SettlePaste();

        Assert.Equal(InsertionOutcomeKind.Inserted, h.WaitOutcome().Kind);
        Assert.Single(h.Store.Saved);
        Assert.Equal("fresh text", h.Store.Saved[0].Formatted);
    }

    [Fact]
    public void StorageFailure_BlocksInsert_ButKeepsCopyLast()
    {
        var h = new Harness();
        h.Store.FailNextSave = true;

        h.Machine.OnKeyPressed();
        h.Machine.OnKeyReleased();
        h.Engine.Sink!.OnFinal(h.CurrentSession, "precious words");

        var outcome = h.WaitOutcome();
        Assert.Equal(InsertionOutcomeKind.StorageFailed, outcome.Kind);
        Assert.Equal(0, h.Paste.PasteCount);
        Assert.Equal(MachineState.Idle, h.Machine.State);

        Assert.True(h.Machine.CopyLast());
        Assert.Equal("precious words", h.Clipboard.Text);
    }

    [Fact]
    public void StorageThrow_IsTreatedAsStorageFailure()
    {
        var h = new Harness();
        h.Store.Throw = true;

        h.Machine.OnKeyPressed();
        h.Machine.OnKeyReleased();
        h.Engine.Sink!.OnFinal(h.CurrentSession, "words");

        Assert.Equal(InsertionOutcomeKind.StorageFailed, h.WaitOutcome().Kind);
        Assert.Equal(0, h.Paste.PasteCount);
    }

    [Fact]
    public void SecureField_AtCapture_RefusesToStart()
    {
        var h = new Harness();
        h.Targets.Foreground = h.Targets.Foreground! with { IsSecureField = true };

        h.Machine.OnKeyPressed();

        Assert.Equal(MachineState.Idle, h.Machine.State);
        Assert.Empty(h.Engine.Started);
        Assert.Equal(PanelState.Error, h.Panel.Current);
    }

    [Fact]
    public void ElevatedTarget_AtCapture_RefusesToStart()
    {
        var h = new Harness();
        h.Targets.Foreground = h.Targets.Foreground! with { IsElevated = true };

        h.Machine.OnKeyPressed();

        Assert.Equal(MachineState.Idle, h.Machine.State);
        Assert.Empty(h.Engine.Started);
    }

    [Fact]
    public void SpeechNotReady_RefusesToStart()
    {
        var h = new Harness();
        h.Engine.Readiness = SpeechReadiness.ModelDownloading;

        h.Machine.OnKeyPressed();

        Assert.Equal(MachineState.Idle, h.Machine.State);
        Assert.Empty(h.Engine.Started);
        Assert.Equal(PanelState.Error, h.Panel.Current);
    }

    [Fact]
    public void FocusChange_BeforePaste_RefusesAndLeavesClipboardUntouched()
    {
        var h = new Harness();
        h.Clipboard.Text = "user data";
        h.Clipboard.Sequence = 10;

        h.Machine.OnKeyPressed();
        h.Machine.OnKeyReleased();

        h.Targets.Foreground = FakeTargets.Editor(hwnd: 999);
        h.Targets.ForegroundMatches = false;

        h.Engine.Sink!.OnFinal(h.CurrentSession, "dictated");
        h.SettlePaste();

        var outcome = h.WaitOutcome();
        Assert.Equal(InsertionOutcomeKind.TargetChanged, outcome.Kind);
        Assert.True(outcome.TextRecoverable);
        Assert.Equal(0, h.Paste.PasteCount);
        Assert.Equal("user data", h.Clipboard.Text);
        Assert.Single(h.Store.Saved);
    }

    [Fact]
    public void TargetBecomesSecure_BeforePaste_Refuses()
    {
        var h = new Harness();
        h.Machine.OnKeyPressed();
        h.Machine.OnKeyReleased();

        h.Targets.Foreground = h.Targets.Foreground! with { IsSecureField = true };

        h.Engine.Sink!.OnFinal(h.CurrentSession, "secret");
        h.SettlePaste();

        Assert.Equal(InsertionOutcomeKind.RefusedSecureTarget, h.WaitOutcome().Kind);
        Assert.Equal(0, h.Paste.PasteCount);
    }

    [Fact]
    public void ClipboardChangedByOtherApp_AfterPaste_IsNeverOverwritten()
    {
        var h = new Harness();
        h.Clipboard.Text = "original";
        h.Clipboard.Sequence = 5;
        h.Paste.OnPaste = () => h.Clipboard.ExternalWrite("other app's copy");

        h.Machine.OnKeyPressed();
        h.Machine.OnKeyReleased();
        h.Engine.Sink!.OnFinal(h.CurrentSession, "dictated");
        h.SettlePaste();

        Assert.Equal(InsertionOutcomeKind.Inserted, h.WaitOutcome().Kind);
        Assert.Equal("other app's copy", h.Clipboard.Text);
    }

    [Fact]
    public void ClipboardBackup_IsRestored_WhenUnchangedAfterPaste()
    {
        var h = new Harness();
        h.Clipboard.Text = "user's copy";
        h.Clipboard.Sequence = 5;

        h.Machine.OnKeyPressed();
        h.Machine.OnKeyReleased();
        h.Engine.Sink!.OnFinal(h.CurrentSession, "dictated");
        h.SettlePaste();

        Assert.Equal(InsertionOutcomeKind.Inserted, h.WaitOutcome().Kind);
        Assert.Equal("user's copy", h.Clipboard.Text);
        Assert.Contains("dictated", h.Clipboard.SetHistory);
    }

    [Fact]
    public void PasteFailure_ReportsRecoverableOutcome()
    {
        var h = new Harness();
        h.Paste.Fail = true;

        h.Machine.OnKeyPressed();
        h.Machine.OnKeyReleased();
        h.Engine.Sink!.OnFinal(h.CurrentSession, "kept safe");
        h.SettlePaste();

        var outcome = h.WaitOutcome();
        Assert.Equal(InsertionOutcomeKind.PasteFailed, outcome.Kind);
        Assert.True(outcome.TextRecoverable);
        Assert.Single(h.Store.Saved);
        Assert.True(h.Machine.CopyLast());
        Assert.Equal("kept safe", h.Clipboard.Text);
    }

    [Fact]
    public void EarlyFinal_WhileKeyHeld_DefersInsertUntilRelease()
    {
        var h = new Harness();
        h.Machine.OnKeyPressed();
        var id = h.CurrentSession;

        h.Engine.Sink!.OnFinal(id, "early final");
        Assert.Equal(0, h.Paste.PasteCount);
        Assert.Empty(h.Store.Saved);
        Assert.Equal(MachineState.Recording, h.Machine.State);

        h.Machine.OnKeyReleased();
        h.SettlePaste();

        Assert.Equal(InsertionOutcomeKind.Inserted, h.WaitOutcome().Kind);
        Assert.Equal("early final", h.Store.Saved[0].Formatted);
        Assert.Equal(1, h.Paste.PasteCount);
    }

    [Fact]
    public void AutoRepeatPresses_WhileRecording_AreIgnored()
    {
        var h = new Harness();
        h.Machine.OnKeyPressed();
        h.Machine.OnKeyPressed();
        h.Machine.OnKeyPressed();

        Assert.Single(h.Engine.Started);
        Assert.Equal(MachineState.Recording, h.Machine.State);
    }

    [Fact]
    public void SpeechError_CancelsSession_AndLateEventsAreDiscarded()
    {
        var h = new Harness();
        h.Machine.OnKeyPressed();
        var id = h.CurrentSession;

        h.Engine.Sink!.OnSpeechError(id, "engine died");
        Assert.Equal(MachineState.Idle, h.Machine.State);
        Assert.Equal(PanelState.Error, h.Panel.Current);

        h.Engine.Sink!.OnPartial(id, "ghost");
        h.Engine.Sink!.OnFinal(id, "ghost");
        Assert.Empty(h.Store.Saved);
        Assert.Equal(0, h.Paste.PasteCount);
    }

    [Fact]
    public void StalePartial_FromPreviousSession_DoesNotTouchPanel()
    {
        var h = new Harness();
        h.Machine.OnKeyPressed();
        var first = h.CurrentSession;
        h.Machine.Cancel(CancelReason.UserCancelled);

        h.Machine.OnKeyPressed();
        h.Engine.Sink!.OnPartial(first, "stale");

        Assert.Equal(PanelState.Recording, h.Panel.Current);
        Assert.Equal("", h.Panel.LastText);
    }

    [Fact]
    public void ErrorPanel_HidesAfterDelay_WhenIdle()
    {
        var h = new Harness();
        h.Engine.Readiness = SpeechReadiness.Failed;
        h.Machine.OnKeyPressed();
        Assert.Equal(PanelState.Error, h.Panel.Current);

        h.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(PanelState.Hidden, h.Panel.Current);
    }

    [Fact]
    public void CopyLast_WithNoHistory_ReturnsFalse()
    {
        var h = new Harness();
        Assert.False(h.Machine.CopyLast());
    }

    [Fact]
    public void FinalDeliveredSynchronouslyFromCancel_IsNeverPublished()
    {
        var h = new Harness();
        h.Engine.SyncFinalOnCancel = "flushed on teardown";

        h.Machine.OnKeyPressed();
        h.Machine.OnKeyReleased();
        h.Machine.Cancel(CancelReason.DesktopLocked);
        h.SettlePaste();

        Assert.Empty(h.Store.Saved);
        Assert.Equal(0, h.Paste.PasteCount);
        Assert.Equal(MachineState.Idle, h.Machine.State);
    }

    [Fact]
    public void FinalDeliveredSynchronouslyFromTimeoutCancel_IsNeverPublished()
    {
        var h = new Harness();
        h.Engine.SyncFinalOnCancel = "flushed on timeout teardown";

        h.Machine.OnKeyPressed();
        h.Machine.OnKeyReleased();
        h.Time.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(InsertionOutcomeKind.NoSpeechResult, h.WaitOutcome().Kind);
        Assert.Empty(h.Store.Saved);
        Assert.Equal(0, h.Paste.PasteCount);
    }

    [Fact]
    public void FinalDeliveredSynchronouslyFromCompleteAudio_InsertsOnce_WithNoStaleTimer()
    {
        var h = new Harness();
        h.Engine.SyncFinalOnComplete = "instant final";

        h.Machine.OnKeyPressed();
        h.Machine.OnKeyReleased();
        h.SettlePaste();

        Assert.Equal(InsertionOutcomeKind.Inserted, h.WaitOutcome().Kind);
        Assert.Single(h.Store.Saved);
        Assert.Equal(1, h.Paste.PasteCount);

        // A stale final-timeout must not fire and fabricate a second outcome.
        h.Time.Advance(TimeSpan.FromSeconds(10));
        lock (h.Outcomes) Assert.Single(h.Outcomes);
        Assert.Equal(MachineState.Idle, h.Machine.State);
    }

    [Fact]
    public void EarlyDuplicateFinals_WhileRecording_FirstWins()
    {
        var h = new Harness();
        h.Machine.OnKeyPressed();
        var id = h.CurrentSession;

        h.Engine.Sink!.OnFinal(id, "first");
        h.Engine.Sink!.OnFinal(id, "second");
        h.Machine.OnKeyReleased();
        h.SettlePaste();

        Assert.Equal(InsertionOutcomeKind.Inserted, h.WaitOutcome().Kind);
        Assert.Single(h.Store.Saved);
        Assert.Equal("first", h.Store.Saved[0].Formatted);
        Assert.Contains(id, h.Engine.Cancelled); // engine session closed before insert
    }
}
