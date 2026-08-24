using Flow.Core.Abstractions;

namespace Flow.Core.Insertion;

public enum RestoreDecision { RestoreBackup, LeaveAlone, ClearOurs }

/// <summary>Pure policy for what to do with the clipboard after a paste attempt.
/// Invariant: never overwrite a clipboard change made after Flow's own write.</summary>
public static class ClipboardRestorePolicy
{
    /// <param name="tokenAfterOurWrite">Sequence token captured immediately after Flow set its text.</param>
    /// <param name="currentSequence">Clipboard sequence number now, after the bounded paste wait.</param>
    /// <param name="hadBackup">Whether a text backup existed before Flow's write.</param>
    public static RestoreDecision Decide(ClipboardToken tokenAfterOurWrite, uint currentSequence, bool hadBackup)
    {
        // Someone (or something) wrote to the clipboard after us — hands off.
        if (currentSequence != tokenAfterOurWrite.SequenceNumber) return RestoreDecision.LeaveAlone;

        // Clipboard still holds exactly our write: put the user's text back if we had it.
        // With no backup we leave our transcript in place — it doubles as Copy Last recovery
        // and clearing would destroy information the user may want.
        return hadBackup ? RestoreDecision.RestoreBackup : RestoreDecision.LeaveAlone;
    }
}
