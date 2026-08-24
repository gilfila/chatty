namespace Flow.Shell.Core.Tests;

/// <summary>
/// The user-facing recovery contract for a persistent restore failure: the original
/// snapshot is held in memory only, the "Restore clipboard" action stays available until
/// it succeeds or the app exits, and a foreign clipboard write supersedes recovery
/// (never-overwrite-later-writes outranks putting the old content back).
/// </summary>
public sealed class ClipboardRestoreRecoveryTests
{
    private const uint CF_UNICODETEXT = 13;

    private sealed class RecoveryFakeRawClipboard : Flow.Shell.Core.IRawClipboard
    {
        public uint Sequence = 40;
        public bool SetAlwaysFails;
        public int EmptyCount;
        public int SetAttempts;

        public bool TryOpen() => true;
        public void Close() { }
        public uint GetSequenceNumber() => Sequence;
        public IReadOnlyList<uint> EnumerateFormats() => [];
        public byte[]? TryReadFormatBytes(uint format) => null;
        public object? TryAllocate(Flow.Shell.Core.ClipboardEntry entry) => new();
        public void FreeAllocation(object allocation) { }

        public bool TryEmpty()
        {
            EmptyCount++;
            return true;
        }

        public bool TrySetAllocated(uint format, object allocation)
        {
            SetAttempts++;
            return !SetAlwaysFails;
        }
    }

    private static Flow.Shell.Core.ClipboardSnapshot Snapshot() =>
        new([new Flow.Shell.Core.ClipboardEntry(CF_UNICODETEXT, [1, 2, 3])], 40);

    private static (Flow.Shell.Core.ClipboardRestoreRecovery Recovery, RecoveryFakeRawClipboard Raw, List<bool> Events) Make()
    {
        var raw = new RecoveryFakeRawClipboard();
        var recovery = new Flow.Shell.Core.ClipboardRestoreRecovery(
            new Flow.Shell.Core.ClipboardFidelityPolicy(raw), raw);
        var events = new List<bool>();
        recovery.PendingChanged += pending => events.Add(pending);
        return (recovery, raw, events);
    }

    [Fact]
    public void HoldForRetry_SurfacesTheRecoveryAction()
    {
        var (recovery, _, events) = Make();
        Assert.False(recovery.HasPendingRestore);

        recovery.HoldForRetry(Snapshot(), 40);

        Assert.True(recovery.HasPendingRestore);
        Assert.Equal([true], events);
    }

    [Fact]
    public void Retry_WhenClipboardUnchanged_RestoresAndClearsTheAction()
    {
        var (recovery, raw, events) = Make();
        recovery.HoldForRetry(Snapshot(), 40);

        Assert.Equal(ClipboardRestoreResult.Restored, recovery.Retry());
        Assert.False(recovery.HasPendingRestore);
        Assert.Equal([true, false], events);
        Assert.Equal(1, raw.EmptyCount);
    }

    [Fact]
    public void Retry_WhilePersistentlyFailing_KeepsTheActionAlive()
    {
        var (recovery, raw, _) = Make();
        raw.SetAlwaysFails = true;
        recovery.HoldForRetry(Snapshot(), 40);

        Assert.Equal(ClipboardRestoreResult.StillFailing, recovery.Retry());
        Assert.True(recovery.HasPendingRestore); // still recoverable, action stays

        raw.SetAlwaysFails = false;
        Assert.Equal(ClipboardRestoreResult.Restored, recovery.Retry()); // until it succeeds
        Assert.False(recovery.HasPendingRestore);
    }

    [Fact]
    public void Retry_AfterForeignWrite_DropsRecoveryWithoutTouchingClipboard()
    {
        var (recovery, raw, events) = Make();
        recovery.HoldForRetry(Snapshot(), 40);

        raw.Sequence = 41; // another app wrote — their content wins

        Assert.Equal(ClipboardRestoreResult.Superseded, recovery.Retry());
        Assert.False(recovery.HasPendingRestore);
        Assert.Equal(0, raw.EmptyCount);
        Assert.Equal(0, raw.SetAttempts);
        Assert.Equal([true, false], events);
    }

    [Fact]
    public void Retry_WithNothingPending_IsANoOp()
    {
        var (recovery, raw, _) = Make();
        Assert.Equal(Flow.Shell.Core.ClipboardRestoreResult.NothingPending, recovery.Retry());
        Assert.Equal(0, raw.EmptyCount);
    }
}
