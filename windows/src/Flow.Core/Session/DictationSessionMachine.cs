using Flow.Core.Abstractions;
using Flow.Core.Insertion;

namespace Flow.Core.Session;

public enum MachineState { Idle, Recording, Finalizing, Inserting }

/// <summary>
/// Coordinates one hold-to-talk dictation session end to end:
/// press → capture target → stream partials → release → exactly one final →
/// persist → guarded insert. All entry points are thread-safe; events carrying
/// a stale session id are discarded, so a cancelled or superseded session can
/// never publish text.
/// </summary>
public sealed class DictationSessionMachine : ISpeechEventSink
{
    private readonly ISpeechEngine _engine;
    private readonly ITranscriptStore _store;
    private readonly ITargetTracker _targets;
    private readonly IClipboardService _clipboard;
    private readonly IPanelPresenter _panel;
    private readonly InsertionOrchestrator _insertion;
    private readonly Func<SpeechReadiness> _readiness;
    private readonly Func<string, string> _format;
    private readonly TimeProvider _time;

    private readonly object _gate = new();
    private long _nextSessionId = 1;
    private SessionId? _current;
    private MachineState _state = MachineState.Idle;
    private TargetDescriptor? _capturedTarget;
    private string _lastPartial = "";
    private string? _pendingFinal;
    private string? _lastFinalInMemory;
    private ITimer? _finalTimeout;
    private ITimer? _panelHideTimer;

    public TimeSpan FinalTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ErrorPanelDuration { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Raised after every completed session with its outcome (for diagnostics).</summary>
    public event Action<SessionId, InsertionOutcome>? SessionCompleted;

    public DictationSessionMachine(
        ISpeechEngine engine,
        ITranscriptStore store,
        ITargetTracker targets,
        IClipboardService clipboard,
        IPasteInjector paste,
        IPanelPresenter panel,
        Func<SpeechReadiness> readinessProbe,
        TimeProvider time,
        Func<string, string>? formatter = null)
    {
        _engine = engine;
        _store = store;
        _targets = targets;
        _clipboard = clipboard;
        _panel = panel;
        _readiness = readinessProbe;
        _time = time;
        _format = formatter ?? (s => s.Trim());
        _insertion = new InsertionOrchestrator(targets, clipboard, paste, time);
    }

    public MachineState State { get { lock (_gate) return _state; } }

    // -----------------------------------------------------------------------
    // Key edges (from the keyboard listener)
    // -----------------------------------------------------------------------

    public void OnKeyPressed()
    {
        lock (_gate)
        {
            if (_state == MachineState.Finalizing)
                CancelLocked(CancelReason.Superseded, showError: false);
            else if (_state != MachineState.Idle)
                return; // Recording: auto-repeat; Inserting: bounded, ignore.

            var readiness = _readiness();
            if (readiness != SpeechReadiness.Ready)
            {
                ShowTransientErrorLocked(readiness switch
                {
                    SpeechReadiness.ModelDownloading => "Speech model still downloading…",
                    SpeechReadiness.MicrophoneDenied => "Microphone access is denied",
                    SpeechReadiness.Unsupported => "On-device speech needs Windows 11 24H2",
                    _ => "Speech engine unavailable",
                });
                return;
            }

            var target = _targets.CaptureForeground();
            switch (TargetGuard.EvaluateForCapture(target))
            {
                case TargetVerdict.SecureField:
                    ShowTransientErrorLocked("Flow won't dictate into a password field");
                    return;
                case TargetVerdict.Elevated:
                    ShowTransientErrorLocked("Target runs as administrator — use Copy Last instead");
                    return;
                case TargetVerdict.Unresolvable:
                    ShowTransientErrorLocked("No text field in focus");
                    return;
            }

            var id = new SessionId(_nextSessionId++);
            _current = id;
            _state = MachineState.Recording;
            _capturedTarget = target;
            _lastPartial = "";
            _pendingFinal = null;
            CancelPanelHideLocked();
            _panel.Show(PanelState.Recording, "");
            _engine.StartSession(id, this);
        }
    }

    public void OnKeyReleased()
    {
        lock (_gate)
        {
            if (_state != MachineState.Recording || _current is not { } id) return;

            if (_pendingFinal is not null)
            {
                // Final arrived while the key was still held; insert now that the
                // trigger modifier is up and can't corrupt the paste chord. Close the
                // engine session first so it stops capturing audio.
                var pending = _pendingFinal;
                _pendingFinal = null;
                _engine.CancelSession(id);
                BeginInsertLocked(id, pending);
                return;
            }

            _state = MachineState.Finalizing;
            _panel.Show(PanelState.Working, _lastPartial);
            _engine.CompleteAudio(id);

            // CompleteAudio may deliver the final synchronously and re-enter this lock;
            // only arm the timeout if the session is still waiting.
            if (_current == id && _state == MachineState.Finalizing)
            {
                _finalTimeout?.Dispose();
                _finalTimeout = _time.CreateTimer(_ => OnFinalTimeout(id), null, FinalTimeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Cancel from any external condition: Escape, desktop lock, secure desktop,
    /// listener failure, input-language or audio-device change.</summary>
    public void Cancel(CancelReason reason)
    {
        lock (_gate)
        {
            if (_state is MachineState.Idle or MachineState.Inserting) return;
            CancelLocked(reason, showError: reason != CancelReason.UserCancelled);
        }
    }

    /// <summary>Copies the most recent final transcript back onto the clipboard.</summary>
    public bool CopyLast()
    {
        var text = _store.GetLast() ?? Volatile.Read(ref _lastFinalInMemory);
        return text is not null && _clipboard.TrySetText(text) is not null;
    }

    // -----------------------------------------------------------------------
    // ISpeechEventSink (from the speech engine, any thread)
    // -----------------------------------------------------------------------

    public void OnPartial(SessionId session, string volatileText)
    {
        lock (_gate)
        {
            if (session != _current || _state is not (MachineState.Recording or MachineState.Finalizing)) return;
            _lastPartial = volatileText;
            _panel.Show(_state == MachineState.Recording ? PanelState.Recording : PanelState.Working, volatileText);
        }
    }

    public void OnFinal(SessionId session, string finalText)
    {
        lock (_gate)
        {
            if (session != _current) return;
            switch (_state)
            {
                case MachineState.Recording:
                    // Engine finalized early (e.g. silence limit) while the key is held.
                    // Defer insertion until release so the held modifier can't join the
                    // paste chord. First final wins; a duplicate never overwrites it.
                    _pendingFinal ??= finalText;
                    return;
                case MachineState.Finalizing:
                    BeginInsertLocked(session, finalText);
                    return;
                default:
                    return; // duplicate or late final — discarded
            }
        }
    }

    public void OnSpeechError(SessionId session, string error)
    {
        lock (_gate)
        {
            if (session != _current || _state is not (MachineState.Recording or MachineState.Finalizing)) return;
            CancelLocked(CancelReason.SpeechFailed, showError: true, errorText: $"Speech failed: {error}");
        }
    }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    private void OnFinalTimeout(SessionId id)
    {
        lock (_gate)
        {
            if (id != _current || _state != MachineState.Finalizing) return;
            _current = null;
            _state = MachineState.Idle;
            _finalTimeout?.Dispose();
            _finalTimeout = null;
            _engine.CancelSession(id); // after the fence closes — a reentrant final is discarded
            ShowTransientErrorLocked("No speech detected");
        }
        SessionCompleted?.Invoke(id, new InsertionOutcome(InsertionOutcomeKind.NoSpeechResult));
    }

    private void BeginInsertLocked(SessionId id, string rawFinal)
    {
        _finalTimeout?.Dispose();
        _finalTimeout = null;
        _state = MachineState.Inserting;
        var formatted = _format(rawFinal);
        Volatile.Write(ref _lastFinalInMemory, formatted);
        var captured = _capturedTarget!;

        if (!TrySave(id, rawFinal, formatted))
        {
            // Invariant: no insertion attempt unless the transcript is durable.
            _current = null;
            _state = MachineState.Idle;
            ShowTransientErrorLocked("Couldn't save transcript — text is on Copy Last only");
            SessionCompleted?.Invoke(id, new InsertionOutcome(InsertionOutcomeKind.StorageFailed));
            return;
        }

        _panel.Show(PanelState.Working, formatted);
        _ = RunInsertAsync(id, captured, formatted);
    }

    private bool TrySave(SessionId id, string raw, string formatted)
    {
        try { return _store.SaveFinal(id, raw, formatted); }
        catch { return false; }
    }

    private async Task RunInsertAsync(SessionId id, TargetDescriptor captured, string text)
    {
        InsertionOutcome outcome;
        try
        {
            outcome = await _insertion.InsertAsync(captured, text, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = new InsertionOutcome(InsertionOutcomeKind.PasteFailed, ex.Message);
        }

        lock (_gate)
        {
            if (_current == id)
            {
                _current = null;
                _state = MachineState.Idle;
            }
            if (outcome.Kind == InsertionOutcomeKind.Inserted)
            {
                CancelPanelHideLocked();
                _panel.Hide();
            }
            else
            {
                ShowTransientErrorLocked(outcome.Kind switch
                {
                    InsertionOutcomeKind.RefusedSecureTarget => "Refused: password field — use Copy Last",
                    InsertionOutcomeKind.RefusedElevatedTarget => "Refused: administrator window — use Copy Last",
                    InsertionOutcomeKind.TargetChanged => "Focus moved — text saved to Copy Last",
                    InsertionOutcomeKind.TargetGone => "Target closed — text saved to Copy Last",
                    InsertionOutcomeKind.ClipboardUnavailable => "Clipboard busy — text saved to Copy Last",
                    _ => "Paste failed — text saved to Copy Last",
                });
            }
        }
        SessionCompleted?.Invoke(id, outcome);
    }

    private void CancelLocked(CancelReason reason, bool showError, string? errorText = null)
    {
        // Invalidate the session BEFORE telling the engine: if CancelSession synchronously
        // re-enters with a final, the stale-id fence must already be closed.
        var id = _current;
        _current = null;
        _state = MachineState.Idle;
        _pendingFinal = null;
        _finalTimeout?.Dispose();
        _finalTimeout = null;
        if (id is { } sid) _engine.CancelSession(sid);
        if (showError) ShowTransientErrorLocked(errorText ?? $"Dictation cancelled ({reason})");
        else { CancelPanelHideLocked(); _panel.Hide(); }
    }

    private void ShowTransientErrorLocked(string text)
    {
        CancelPanelHideLocked();
        _panel.Show(PanelState.Error, text);
        _panelHideTimer = _time.CreateTimer(_ => HidePanelIfIdle(), null, ErrorPanelDuration, Timeout.InfiniteTimeSpan);
    }

    private void HidePanelIfIdle()
    {
        lock (_gate)
        {
            if (_state == MachineState.Idle) _panel.Hide();
            CancelPanelHideLocked();
        }
    }

    private void CancelPanelHideLocked()
    {
        _panelHideTimer?.Dispose();
        _panelHideTimer = null;
    }
}
