using Flow.Core.Abstractions;

namespace Flow.Shell.Core;

/// <summary>A raw edge as the low-level keyboard hook sees it, before any normalization.</summary>
public enum RawKeyEdge
{
    Down,
    Up,
}

/// <summary>The normalized hold-to-talk edge the session loop consumes.</summary>
public enum TriggerEdge
{
    /// <summary>Shortcut went down — begin a session.</summary>
    Pressed,

    /// <summary>Shortcut came up — stop capture and finalize.</summary>
    Released,

    /// <summary>Abort. Discard rather than finalize.</summary>
    Cancelled,
}

/// <summary>
/// Turns raw shortcut edges from the Windows keyboard hook into a clean press/release/cancel
/// stream.
/// </summary>
/// <remarks>
/// This exists because Windows gives hold-to-talk no help at all. <c>RegisterHotKey</c> reports a
/// single <c>WM_HOTKEY</c> activation with no release edge, so it cannot express hold-to-talk;
/// the shell must run its own <c>WH_KEYBOARD_LL</c> hook. A raw hook stream is then hostile in
/// three specific ways, and all three are session-integrity bugs rather than cosmetic ones:
///
/// <list type="number">
/// <item><description><b>Auto-repeat.</b> Holding a key produces a continuous run of down edges.
/// Uncollapsed, every repeat starts a new dictation.</description></item>
/// <item><description><b>Lost up edges.</b> If the workstation locks, the secure desktop appears,
/// or the hook is evicted while the key is held, the up edge never arrives. The session must
/// cancel rather than hang holding, and the stray up that may arrive later must not finalize
/// it.</description></item>
/// <item><description><b>Cancel racing release.</b> Escape and the shortcut's own release arrive
/// back to back. Cancel must win, or a session the user aborted still pastes.</description></item>
/// </list>
///
/// <para>
/// Pure and platform-free so all of this is testable without Windows. Not thread-safe: the shell
/// calls it only from the hook's dedicated message-pump thread, which must also return from every
/// callback promptly or Windows silently evicts the hook.
/// </para>
/// </remarks>
public sealed class HoldToTalkGate
{
    /// <summary>
    /// Longest a press may plausibly last. Exceeding it means the up edge was lost, not that the
    /// user is still speaking.
    /// </summary>
    public static readonly TimeSpan DefaultMaxHold = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _maxHold;
    private bool _holding;
    private TimeSpan _heldFor;

    public HoldToTalkGate(TimeSpan? maxHold = null) => _maxHold = maxHold ?? DefaultMaxHold;

    /// <summary>True between an emitted <see cref="TriggerEdge.Pressed"/> and its resolution.</summary>
    public bool IsHolding => _holding;

    /// <summary>Why the in-flight press was cancelled. Null unless the last event was a cancel.</summary>
    public CancelReason? LastCancelReason { get; private set; }

    /// <summary>Feed a raw hook edge. Returns the edge to publish, or null to publish nothing.</summary>
    public TriggerEdge? Edge(RawKeyEdge edge) => edge switch
    {
        RawKeyEdge.Down => Down(),
        RawKeyEdge.Up => Up(),
        _ => null,
    };

    /// <summary>
    /// Abort the in-flight press. Returns <see cref="TriggerEdge.Cancelled"/> when there was one to
    /// abort, and null otherwise so a spurious abort never fabricates an event.
    /// </summary>
    /// <remarks>
    /// After this the shortcut's real up edge is swallowed, because the gate is no longer holding.
    /// That is the "cancel always wins" half of the contract: the user did let go, but they had
    /// already abandoned the session before they did.
    /// </remarks>
    public TriggerEdge? Abort(CancelReason reason)
    {
        if (!_holding)
        {
            return null;
        }

        _holding = false;
        _heldFor = TimeSpan.Zero;
        LastCancelReason = reason;
        return TriggerEdge.Cancelled;
    }

    /// <summary>
    /// Advance the hold watchdog. Returns <see cref="TriggerEdge.Cancelled"/> once the press has
    /// outlived <see cref="DefaultMaxHold"/>, which in practice only happens when the up edge was
    /// lost — so it is reported as a listener failure, not as something the user did.
    /// </summary>
    public TriggerEdge? Tick(TimeSpan elapsed)
    {
        if (!_holding)
        {
            return null;
        }

        _heldFor += elapsed;
        return _heldFor >= _maxHold ? Abort(CancelReason.ListenerFailed) : null;
    }

    private TriggerEdge? Down()
    {
        // Auto-repeat. The consumer must never see two Pressed without a resolution between them.
        if (_holding)
        {
            return null;
        }

        _holding = true;
        _heldFor = TimeSpan.Zero;
        LastCancelReason = null;
        return TriggerEdge.Pressed;
    }

    private TriggerEdge? Up()
    {
        // Either a stray up with no matching down, or the swallowed up that follows a cancel.
        if (!_holding)
        {
            return null;
        }

        _holding = false;
        _heldFor = TimeSpan.Zero;
        return TriggerEdge.Released;
    }
}
