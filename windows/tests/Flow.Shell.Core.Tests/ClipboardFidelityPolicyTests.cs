namespace Flow.Shell.Core.Tests;

/// <summary>
/// The QA-required proofs for full-fidelity clipboard preservation: any format that cannot
/// round-trip byte-losslessly refuses the paste path BEFORE the clipboard is touched, and a
/// restore that cannot complete never reports success — with preflight guaranteeing that an
/// allocation failure costs the user nothing.
/// </summary>
public sealed class ClipboardFidelityPolicyTests
{
    private const uint CF_BITMAP = 2;
    private const uint CF_METAFILEPICT = 3;
    private const uint CF_DIB = 8;
    private const uint CF_PALETTE = 9;
    private const uint CF_ENHMETAFILE = 14;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_PRIVATE = 0x0201;
    private const uint CF_OWNERDISPLAY = 0x0080;
    private const uint RegisteredRtf = 0xC004;

    private sealed class FakeRawClipboard : Flow.Shell.Core.IRawClipboard
    {
        public List<uint> Formats = [];
        public Dictionary<uint, byte[]?> Readable = [];
        public uint Sequence = 7;
        public bool OpenFails;
        public bool EmptyFails;
        public Func<uint, bool>? SetFails;
        public HashSet<uint> AllocFailsFor = [];

        public int EmptyCount;
        public readonly List<uint> SetCalls = [];
        public readonly List<object> Live = [];
        public int FreedCount;
        public bool IsOpen;

        public bool TryOpen()
        {
            if (OpenFails) return false;
            IsOpen = true;
            return true;
        }

        public void Close() => IsOpen = false;
        public uint GetSequenceNumber() => Sequence;
        public IReadOnlyList<uint> EnumerateFormats() => Formats;

        public byte[]? TryReadFormatBytes(uint format) =>
            Readable.TryGetValue(format, out var data) ? data : null;

        public object? TryAllocate(Flow.Shell.Core.ClipboardEntry entry)
        {
            if (AllocFailsFor.Contains(entry.Format)) return null;
            var allocation = new object();
            Live.Add(allocation);
            return allocation;
        }

        public void FreeAllocation(object allocation)
        {
            Assert.Contains(allocation, Live);
            Live.Remove(allocation);
            FreedCount++;
        }

        public bool TryEmpty()
        {
            Assert.True(IsOpen);
            EmptyCount++;
            return !EmptyFails;
        }

        public bool TrySetAllocated(uint format, object allocation)
        {
            Assert.True(IsOpen);
            SetCalls.Add(format);
            if (SetFails?.Invoke(format) == true) return false;
            Live.Remove(allocation); // consumed by the system
            return true;
        }
    }

    private static Flow.Shell.Core.ClipboardSnapshot Snap(params uint[] formats) =>
        new([.. formats.Select(f => new Flow.Shell.Core.ClipboardEntry(f, [1, 2, 3]))], 7);

    // ---- Capture refusal: the paste path must never start ----------------------------

    [Fact]
    public void PrivateAppFormat_RefusesCapture_WithoutTouchingClipboard()
    {
        var raw = new FakeRawClipboard
        {
            Formats = [CF_UNICODETEXT, CF_PRIVATE],
            Readable = { [CF_UNICODETEXT] = [1] },
        };
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);

        Assert.Null(policy.TryCapture(out var reason));
        Assert.Contains("0x201", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, raw.EmptyCount);
        Assert.Empty(raw.SetCalls);
    }

    [Fact]
    public void OwnerDisplayFormat_RefusesCapture()
    {
        var raw = new FakeRawClipboard { Formats = [CF_OWNERDISPLAY] };
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);

        Assert.Null(policy.TryCapture(out _));
        Assert.Equal(0, raw.EmptyCount);
        Assert.Empty(raw.SetCalls);
    }

    [Fact]
    public void UnreadableDelayedRenderFormat_RefusesCapture()
    {
        var raw = new FakeRawClipboard
        {
            Formats = [CF_UNICODETEXT, RegisteredRtf],
            Readable = { [CF_UNICODETEXT] = [1] }, // RTF owner fails to render
        };
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);

        Assert.Null(policy.TryCapture(out var reason));
        Assert.Contains("could not be read", reason);
        Assert.Equal(0, raw.EmptyCount);
        Assert.Empty(raw.SetCalls);
    }

    [Fact]
    public void BitmapWithoutDib_RefusesCapture()
    {
        var raw = new FakeRawClipboard { Formats = [CF_BITMAP] };
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);

        Assert.Null(policy.TryCapture(out _));
    }

    [Fact]
    public void SynthesizedGdiTwins_AreCoveredNotRefused()
    {
        var raw = new FakeRawClipboard
        {
            Formats = [CF_BITMAP, CF_PALETTE, CF_DIB, CF_METAFILEPICT, CF_ENHMETAFILE, CF_UNICODETEXT],
            Readable =
            {
                [CF_DIB] = [1, 2],
                [CF_ENHMETAFILE] = [3, 4],
                [CF_UNICODETEXT] = [5],
            },
        };
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);

        var snapshot = policy.TryCapture(out _);
        Assert.NotNull(snapshot);
        Assert.Equal([CF_DIB, CF_ENHMETAFILE, CF_UNICODETEXT], snapshot!.Entries.Select(e => e.Format).ToArray());
    }

    [Fact]
    public void EmptyClipboard_IsAValidSnapshot()
    {
        var raw = new FakeRawClipboard();
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);

        var snapshot = policy.TryCapture(out _);
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot!.Entries);
    }

    [Fact]
    public void ClipboardUnavailable_RefusesCapture()
    {
        var raw = new FakeRawClipboard { OpenFails = true };
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);

        Assert.Null(policy.TryCapture(out var reason));
        Assert.Equal("clipboard unavailable", reason);
    }

    // ---- Restore: preflight before empty, honest failure after -----------------------

    [Fact]
    public void AllocationFailure_AbortsBeforeEmpty_AndFreesEverything()
    {
        var raw = new FakeRawClipboard { AllocFailsFor = { CF_DIB } };
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);

        Assert.False(policy.TryRestoreIfSequenceMatches(Snap(CF_UNICODETEXT, CF_DIB), 7));
        Assert.Equal(0, raw.EmptyCount);   // clipboard never touched
        Assert.Empty(raw.SetCalls);
        Assert.Empty(raw.Live);            // preflight allocations all freed
    }

    [Fact]
    public void SequenceMismatch_LeavesClipboardAlone_AndFreesAllocations()
    {
        var raw = new FakeRawClipboard { Sequence = 99 };
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);

        Assert.False(policy.TryRestoreIfSequenceMatches(Snap(CF_UNICODETEXT), 7));
        Assert.Equal(0, raw.EmptyCount);
        Assert.Empty(raw.SetCalls);
        Assert.Empty(raw.Live);
    }

    [Fact]
    public void TransientSetFailure_IsRetriedWithinTheHeldOpenClipboard()
    {
        var raw = new FakeRawClipboard();
        var dibAttempts = 0;
        raw.SetFails = f => f == CF_DIB && dibAttempts++ < 1; // fail once, succeed on the retry
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);

        Assert.True(policy.TryRestoreIfSequenceMatches(Snap(CF_UNICODETEXT, CF_DIB), 7));
        Assert.Equal(2, dibAttempts); // one failed attempt + the successful retry
        Assert.Empty(raw.Live); // every allocation consumed after the retry
    }

    [Fact]
    public void SetFailure_IsRestorationFailure_NotSuccess()
    {
        var raw = new FakeRawClipboard { SetFails = f => f == CF_DIB };
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);

        Assert.False(policy.TryRestoreIfSequenceMatches(Snap(CF_UNICODETEXT, CF_DIB), 7));
        Assert.Empty(raw.Live); // failed allocation freed, not leaked
    }

    [Fact]
    public void EmptyFailure_IsRestorationFailure()
    {
        var raw = new FakeRawClipboard { EmptyFails = true };
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);

        Assert.False(policy.TryRestoreIfSequenceMatches(Snap(CF_UNICODETEXT), 7));
        Assert.Empty(raw.SetCalls);
        Assert.Empty(raw.Live);
    }

    [Fact]
    public void SuccessfulRestore_SetsEveryEntry_InOrder()
    {
        var raw = new FakeRawClipboard();
        var policy = new Flow.Shell.Core.ClipboardFidelityPolicy(raw);

        Assert.True(policy.TryRestoreIfSequenceMatches(Snap(CF_UNICODETEXT, CF_DIB, RegisteredRtf), 7));
        Assert.Equal(1, raw.EmptyCount);
        Assert.Equal([CF_UNICODETEXT, CF_DIB, RegisteredRtf], raw.SetCalls);
        Assert.Empty(raw.Live); // all allocations consumed by the clipboard
    }
}
