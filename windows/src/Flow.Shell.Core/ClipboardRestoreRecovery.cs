namespace Flow.Shell.Core;

/// <summary>
/// Holds the user's original clipboard snapshot after a persistent restore failure and
/// drives the user-facing "Restore clipboard" retry action, per the QA requirement:
///
/// - The snapshot lives in THIS OBJECT'S MEMORY ONLY. It must never be persisted, logged,
///   or serialized — there is deliberately no API here that exposes the bytes, only retry.
/// - The retry action stays available until it succeeds or the app exits.
/// - A foreign clipboard write while a retry is pending supersedes the recovery: another
///   app (or the user) put new content there deliberately, and the never-overwrite-later-
///   writes invariant outranks recovery. The pending snapshot is dropped without touching
///   the clipboard.
/// </summary>
public sealed class ClipboardRestoreRecovery(ClipboardFidelityPolicy policy, IRawClipboard raw)
{
    private readonly object _gate = new();
    private ClipboardSnapshot? _pending;
    private uint _expectedSequence;

    /// <summary>True while a failed restore is waiting for the user's retry — the shell
    /// surfaces the "Restore clipboard" action exactly while this is true.</summary>
    public bool HasPendingRestore
    {
        get { lock (_gate) return _pending is not null; }
    }

    /// <summary>Raised with the new HasPendingRestore value whenever it changes.</summary>
    public event Action<bool>? PendingChanged;

    /// <summary>Called by the paste path when a restore attempt has failed after its
    /// in-open retries. <paramref name="sequenceAfterFailure"/> is the clipboard sequence
    /// observed after the failed attempt — a later mismatch means a foreign write.</summary>
    public void HoldForRetry(ClipboardSnapshot snapshot, uint sequenceAfterFailure)
    {
        lock (_gate)
        {
            _pending = snapshot;
            _expectedSequence = sequenceAfterFailure;
        }
        PendingChanged?.Invoke(true);
    }

    /// <summary>The user's "Restore clipboard" action. StillFailing keeps the snapshot and
    /// the action alive; Superseded and Restored both retire the action.</summary>
    public ClipboardRestoreResult Retry()
    {
        ClipboardSnapshot? snapshot;
        uint expected;
        lock (_gate)
        {
            snapshot = _pending;
            expected = _expectedSequence;
        }
        if (snapshot is null) return ClipboardRestoreResult.NothingPending;

        // A foreign write since the failure means the clipboard is no longer in the broken
        // state Flow left it in — recovery must not clobber it. (The policy's own atomic
        // in-open check still guards the race between this look and the restore.)
        if (raw.GetSequenceNumber() != expected)
        {
            ClearPending();
            return ClipboardRestoreResult.Superseded;
        }

        if (policy.TryRestoreIfSequenceMatches(snapshot, expected))
        {
            ClearPending();
            return ClipboardRestoreResult.Restored;
        }
        return ClipboardRestoreResult.StillFailing;
    }

    private void ClearPending()
    {
        lock (_gate)
        {
            _pending = null;
        }
        PendingChanged?.Invoke(false);
    }
}
