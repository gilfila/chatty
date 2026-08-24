using Flow.Core.Abstractions;
using Flow.Core.Session;
using Flow.Shell.Core;

namespace Flow.Shell.Core.Tests;

/// <summary>
/// The recovery path through the real session machine: a transcript that was saved but could not
/// be typed must reach the clipboard when the user presses Copy last.
/// </summary>
/// <remarks>
/// Everything here except the speech engine and the Win32 services is production code — the real
/// <see cref="DictationSessionMachine"/>, the real transcript store contract, the real
/// <c>TargetGuard</c> and <c>InsertionOrchestrator</c>, the real <see cref="PanelPresenter"/> and
/// <see cref="PanelActionRouter"/>, and the real <see cref="DictationSessionMachine.CopyLast"/>.
///
/// <para>
/// This is the test that proves the recovery promise rather than any single component of it: the
/// plan's invariant is that a failed insertion never loses text, and the only way to show that is
/// to fail an insertion and then get the text back.
/// </para>
/// </remarks>
public sealed class RecoveryEndToEndTests
{
    // ---- Fakes for the two edges of the system we cannot run here -----------

    private sealed class FakeSpeechEngine : ISpeechEngine
    {
        public ISpeechEventSink? Sink { get; private set; }
        public SessionId? Session { get; private set; }
        public SpeechReadiness Readiness { get; set; } = SpeechReadiness.Ready;

        public Task<SpeechReadiness> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(Readiness);

        public void StartSession(SessionId session, ISpeechEventSink sink)
        {
            Session = session;
            Sink = sink;
        }

        public void CompleteAudio(SessionId session) { }

        public void CancelSession(SessionId session) { }
    }

    private sealed class InMemoryStore : ITranscriptStore
    {
        public string? Last { get; private set; }
        public int SaveCount { get; private set; }

        public bool SaveFinal(SessionId session, string rawText, string formattedText)
        {
            Last = formattedText;
            SaveCount++;
            return true;
        }

        public string? GetLast() => Last;
    }

    private sealed class FakeClipboard : IClipboardService
    {
        private uint _sequence = 1;

        public string? Text { get; private set; }

        public string? TryReadText() => Text;

        public ClipboardToken? TrySetText(string text)
        {
            Text = text;
            return new ClipboardToken(++_sequence);
        }

        public uint GetSequenceNumber() => _sequence;
    }

    /// <summary>Foreground moves to a different window between the press and the paste.</summary>
    private sealed class MovingTargetTracker : ITargetTracker
    {
        private readonly TargetDescriptor _atPress = new(
            WindowHandle: 0x1111, ProcessId: 100, ThreadId: 10,
            IsElevated: false, IsSecureField: false, ProcessName: "Notepad");

        private readonly TargetDescriptor _afterMove = new(
            WindowHandle: 0x2222, ProcessId: 200, ThreadId: 20,
            IsElevated: false, IsSecureField: false, ProcessName: "Chrome");

        public bool Moved { get; set; }

        public TargetDescriptor? CaptureForeground() => Moved ? _afterMove : _atPress;

        public bool IsStillForeground(TargetDescriptor captured) => !Moved;
    }

    private sealed class NoopPaste : IPasteInjector
    {
        public bool SendPaste() => true;
    }

    private sealed class RecordingPanel : IPanelPresenter
    {
        public void Show(PanelState state, string text) { }

        public void Hide() { }
    }

    /// <summary>
    /// The shell's recovery surface. <c>CopyLast</c> goes through the real machine, which is the
    /// same call <c>Flow.Windows.RecoverySurface</c> delegates to.
    /// </summary>
    private sealed class ShellRecovery(DictationSessionMachine machine, InMemoryStore store) : IRecoverySurface
    {
        public bool HasRecoverableText => store.GetLast() is not null;

        public bool CopyLast() => machine.CopyLast();

        public bool OpenMicrophoneSettings() => true;

        public bool RetrySetup() => true;

        public bool BeginShortcutCapture() => true;

        public bool HasPendingClipboardRestore => false;

        public ClipboardRestoreResult RestoreClipboard() => ClipboardRestoreResult.NothingPending;
    }

    // ---- The test -----------------------------------------------------------

    [Fact]
    public async Task ATranscriptThatCouldNotBeTyped_ReachesTheClipboardViaTheRecoveryPanel()
    {
        var engine = new FakeSpeechEngine();
        var store = new InMemoryStore();
        var clipboard = new FakeClipboard();
        var targets = new MovingTargetTracker();

        var machine = new DictationSessionMachine(
            engine, store, targets, clipboard, new NoopPaste(), new RecordingPanel(),
            readinessProbe: () => engine.Readiness,
            time: TimeProvider.System);

        var completed = new TaskCompletionSource<InsertionOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        machine.SessionCompleted += (_, outcome) => completed.TrySetResult(outcome);

        // 1. Hold the shortcut over a normal editable field.
        machine.OnKeyPressed();
        Assert.Equal(MachineState.Recording, machine.State);
        var session = Assert.IsType<SessionId>(engine.Session);

        // 2. Speak.
        engine.Sink!.OnPartial(session, "let's move the review");

        // 3. Focus moves away while Flow is still listening.
        targets.Moved = true;

        // 4. Release, and the engine produces its one final.
        machine.OnKeyReleased();
        engine.Sink!.OnFinal(session, "let's move the review to Thursday");

        var outcome = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 5. Flow refused to type into the window the user moved to, and the transcript is durable.
        Assert.Equal(InsertionOutcomeKind.TargetChanged, outcome.Kind);
        Assert.True(outcome.TextRecoverable);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal("let's move the review to Thursday", store.GetLast());
        Assert.Null(clipboard.Text); // nothing was pasted, so nothing was borrowed

        // 6. The panel the shell would render offers Copy last over that exact text.
        var view = PanelPresenter.Present(new ShellSnapshot(
            new ShellPhase.Idle(),
            SpeechReadiness.Ready,
            ListenerHealth.Ok,
            LastOutcome: outcome,
            LastTranscript: store.GetLast()));

        Assert.True(view.IsVisible);
        Assert.Equal(PanelAction.CopyLast, view.Action);
        Assert.Equal("let's move the review to Thursday", view.BodyText);
        Assert.False(view.BodyIsProvisional);
        Assert.Null(view.AutoDismissAfter);

        // 7. The user presses it.
        var recovery = new ShellRecovery(machine, store);
        Assert.True(PanelActionRouter.IsActionEnabled(view, recovery));

        var result = PanelActionRouter.Invoke(view, view.Action, recovery);

        // 8. The same stored transcript is on the clipboard.
        Assert.Equal(PanelActionResult.Copied, result);
        Assert.Equal(store.GetLast(), clipboard.Text);
        Assert.Equal("let's move the review to Thursday", clipboard.Text);
    }

    [Fact]
    public async Task APasswordFieldNeverStartsASessionAndLeavesNothingToRecover()
    {
        var engine = new FakeSpeechEngine();
        var store = new InMemoryStore();
        var clipboard = new FakeClipboard();

        var secure = new SecureTargetTracker();
        var machine = new DictationSessionMachine(
            engine, store, secure, clipboard, new NoopPaste(), new RecordingPanel(),
            readinessProbe: () => engine.Readiness,
            time: TimeProvider.System);

        machine.OnKeyPressed();

        Assert.Equal(MachineState.Idle, machine.State);
        Assert.Null(engine.Session);
        Assert.Null(store.GetLast());
        Assert.Null(clipboard.Text);

        var recovery = new ShellRecovery(machine, store);
        Assert.False(recovery.HasRecoverableText);

        await Task.CompletedTask;
    }

    private sealed class SecureTargetTracker : ITargetTracker
    {
        private readonly TargetDescriptor _password = new(
            WindowHandle: 0x3333, ProcessId: 300, ThreadId: 30,
            IsElevated: false, IsSecureField: true, ProcessName: "Chrome");

        public TargetDescriptor? CaptureForeground() => _password;

        public bool IsStillForeground(TargetDescriptor captured) => true;
    }
}
