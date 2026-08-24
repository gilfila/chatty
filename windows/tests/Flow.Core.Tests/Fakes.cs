using Flow.Core.Abstractions;

namespace Flow.Core.Tests;

public sealed class FakeSpeechEngine : ISpeechEngine
{
    public readonly List<SessionId> Started = [];
    public readonly List<SessionId> Completed = [];
    public readonly List<SessionId> Cancelled = [];
    public ISpeechEventSink? Sink;
    public SpeechReadiness Readiness = SpeechReadiness.Ready;

    /// <summary>Text delivered synchronously (reentrantly) from inside CancelSession,
    /// modeling an engine that flushes a final while being torn down.</summary>
    public string? SyncFinalOnCancel;

    /// <summary>Text delivered synchronously (reentrantly) from inside CompleteAudio.</summary>
    public string? SyncFinalOnComplete;

    public Task<SpeechReadiness> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(Readiness);

    public void StartSession(SessionId session, ISpeechEventSink sink)
    {
        Started.Add(session);
        Sink = sink;
    }

    public void CompleteAudio(SessionId session)
    {
        Completed.Add(session);
        if (SyncFinalOnComplete is { } text) Sink!.OnFinal(session, text);
    }

    public void CancelSession(SessionId session)
    {
        Cancelled.Add(session);
        if (SyncFinalOnCancel is { } text) Sink!.OnFinal(session, text);
    }
}

public sealed class FakeStore : ITranscriptStore
{
    public readonly List<(SessionId Id, string Raw, string Formatted)> Saved = [];
    public bool FailNextSave;
    public bool Throw;

    public bool SaveFinal(SessionId session, string raw, string formatted)
    {
        if (Throw) throw new IOException("disk unavailable");
        if (FailNextSave) { FailNextSave = false; return false; }
        Saved.Add((session, raw, formatted));
        return true;
    }

    public string? GetLast() => Saved.Count == 0 ? null : Saved[^1].Formatted;
}

public sealed class FakeTargets : ITargetTracker
{
    public TargetDescriptor? Foreground = Editor();
    public bool ForegroundMatches = true;

    public static TargetDescriptor Editor(nint hwnd = 100) =>
        new(hwnd, ProcessId: 42, ThreadId: 7, IsElevated: false, IsSecureField: false, ProcessName: "notepad");

    public TargetDescriptor? CaptureForeground() => Foreground;
    public bool IsStillForeground(TargetDescriptor captured) => ForegroundMatches;
}

public sealed class FakeClipboard : IClipboardService
{
    public string? Text;
    public uint Sequence;
    public bool FailSet;
    public readonly List<string> SetHistory = [];

    public string? TryReadText() => Text;

    public ClipboardToken? TrySetText(string text)
    {
        if (FailSet) return null;
        Text = text;
        Sequence++;
        SetHistory.Add(text);
        return new ClipboardToken(Sequence);
    }

    public uint GetSequenceNumber() => Sequence;

    /// <summary>Simulates another application writing to the clipboard.</summary>
    public void ExternalWrite(string text)
    {
        Text = text;
        Sequence++;
    }
}

public sealed class FakePaste : IPasteInjector
{
    public int PasteCount;
    public bool Fail;
    public Action? OnPaste;

    public bool SendPaste()
    {
        PasteCount++;
        OnPaste?.Invoke();
        return !Fail;
    }
}

public sealed class FakePanel : IPanelPresenter
{
    public readonly List<(PanelState State, string Text)> ShowCalls = [];
    public int HideCount;
    public PanelState Current = PanelState.Hidden;
    public string LastText = "";

    public void Show(PanelState state, string text)
    {
        ShowCalls.Add((state, text));
        Current = state;
        LastText = text;
    }

    public void Hide()
    {
        HideCount++;
        Current = PanelState.Hidden;
    }
}
