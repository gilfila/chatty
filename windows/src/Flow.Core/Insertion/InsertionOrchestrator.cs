using Flow.Core.Abstractions;

namespace Flow.Core.Insertion;

/// <summary>Carries dictated text into the captured foreground field via clipboard + paste,
/// enforcing the target-guard and clipboard-restore invariants. The transcript is already
/// durable before this runs; every failure path here leaves it reachable via Copy Last.</summary>
public sealed class InsertionOrchestrator(
    ITargetTracker targets,
    IClipboardService clipboard,
    IPasteInjector paste,
    TimeProvider time)
{
    /// <summary>How long the target application gets to consume the clipboard before Flow
    /// considers restoring it.</summary>
    public TimeSpan PasteSettleDelay { get; init; } = TimeSpan.FromMilliseconds(600);

    public async Task<InsertionOutcome> InsertAsync(TargetDescriptor captured, string text, CancellationToken ct)
    {
        var current = targets.CaptureForeground();
        var verdict = TargetGuard.EvaluateForPaste(captured, current, current is not null && targets.IsStillForeground(captured));
        switch (verdict)
        {
            case TargetVerdict.SecureField:
                return new InsertionOutcome(InsertionOutcomeKind.RefusedSecureTarget);
            case TargetVerdict.Elevated:
                return new InsertionOutcome(InsertionOutcomeKind.RefusedElevatedTarget);
            case TargetVerdict.Unresolvable:
                return new InsertionOutcome(
                    current is null || current.WindowHandle == captured.WindowHandle
                        ? InsertionOutcomeKind.TargetGone
                        : InsertionOutcomeKind.TargetChanged);
        }

        var backup = clipboard.TryReadText();
        var token = clipboard.TrySetText(text);
        if (token is null)
            return new InsertionOutcome(InsertionOutcomeKind.ClipboardUnavailable);

        var pasted = paste.SendPaste();

        // Give the target time to read the clipboard, whether or not injection reported
        // success — a false negative that still pasted must not race the restore.
        try { await Task.Delay(PasteSettleDelay, time, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* proceed to restore decision regardless */ }

        var decision = ClipboardRestorePolicy.Decide(token, clipboard.GetSequenceNumber(), backup is not null);
        if (decision == RestoreDecision.RestoreBackup)
            clipboard.TrySetText(backup!);

        return pasted
            ? new InsertionOutcome(InsertionOutcomeKind.Inserted)
            : new InsertionOutcome(InsertionOutcomeKind.PasteFailed, "paste injection failed");
    }
}
