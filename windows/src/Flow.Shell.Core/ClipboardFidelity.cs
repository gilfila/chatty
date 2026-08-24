namespace Flow.Shell.Core;

/// <summary>One preserved clipboard format: its id and its exact bytes.</summary>
public sealed record ClipboardEntry(uint Format, byte[] Data);

/// <summary>A full-fidelity snapshot: every format needed to reproduce the clipboard
/// byte-losslessly, plus the sequence number identifying the state it captured.</summary>
public sealed record ClipboardSnapshot(IReadOnlyList<ClipboardEntry> Entries, uint SequenceNumber);

/// <summary>
/// Raw clipboard verbs, implemented over Win32 by the shell and by a fake in tests.
/// Read/enumerate/empty/set are only valid between TryOpen and Close. Allocation is
/// independent of the open state so restores can preflight before touching anything.
/// </summary>
public interface IRawClipboard
{
    bool TryOpen();
    void Close();
    uint GetSequenceNumber();

    /// <summary>All formats currently on the clipboard, in enumeration order.</summary>
    IReadOnlyList<uint> EnumerateFormats();

    /// <summary>The format's content as bytes, or null when it cannot be read losslessly
    /// (delayed-render owner failed, not a global-memory handle, zero size).</summary>
    byte[]? TryReadFormatBytes(uint format);

    /// <summary>Materializes an entry into a native allocation ready to be placed on the
    /// clipboard. Null on allocation failure. Does not require the clipboard.</summary>
    object? TryAllocate(ClipboardEntry entry);

    /// <summary>Frees an allocation that was not consumed by a successful set.</summary>
    void FreeAllocation(object allocation);

    bool TryEmpty();

    /// <summary>Places an allocation on the clipboard. On success the system owns the
    /// allocation; on failure the caller must free it.</summary>
    bool TrySetAllocated(uint format, object allocation);
}

/// <summary>
/// All-or-nothing clipboard preservation, per the locked product decision and QA rules:
///
/// - Capture enumerates every format first. If ANY format cannot be captured and later
///   restored byte-losslessly, capture refuses — and because capture happens before Flow
///   writes its transcript, refusal means the paste path is never entered and the user's
///   clipboard is never touched. The transcript stays reachable through Copy Last.
/// - GDI-handle formats that Windows synthesizes from a captured global-memory twin
///   (CF_BITMAP/CF_PALETTE from CF_DIB, CF_METAFILEPICT from CF_ENHMETAFILE) are skipped
///   as covered: restoring the twin makes Windows regenerate them on demand.
/// - Restore preflights every allocation BEFORE emptying the clipboard, so an allocation
///   failure leaves the current contents untouched. After the atomic sequence re-check,
///   any individual set failure makes the whole restore report failure, never success.
///
/// Ordering contract for the paste path: TryCapture MUST run before Flow writes its
/// transcript to the clipboard — Flow's own transcript write is the first EmptyClipboard
/// in the flow, so capture refusal has to gate it. A null capture means no automatic
/// paste at all, transcript to Copy Last only.
///
/// About the post-empty window during restore: the user's original content is durable in
/// the snapshot's bytes, not on the clipboard, from the moment capture succeeds. While
/// the clipboard is held open no other process can write, so a failed set is retried
/// in place ("demonstrably completed" = every entry set, with retries, inside one open).
/// If restore still returns false the caller MUST retain the snapshot — the data is not
/// lost, and the restore can be re-attempted; only discarding the snapshot loses it.
/// </summary>
public sealed class ClipboardFidelityPolicy(IRawClipboard raw)
{
    /// <summary>Attempts per entry inside the held-open clipboard before the restore
    /// reports failure. No foreign writer can interleave, so retrying is race-free.</summary>
    public int SetRetryAttempts { get; init; } = 3;

    /// <summary>Null means "cannot preserve losslessly — do not enter the paste path".
    /// <paramref name="refusalReason"/> names the offending format for diagnostics.</summary>
    public ClipboardSnapshot? TryCapture(out string? refusalReason)
    {
        refusalReason = null;
        if (!raw.TryOpen())
        {
            refusalReason = "clipboard unavailable";
            return null;
        }
        try
        {
            var formats = raw.EnumerateFormats();
            var entries = new List<ClipboardEntry>();
            foreach (var format in formats)
            {
                switch (Classify(format, formats))
                {
                    case FormatClass.Unpreservable:
                        refusalReason = $"format 0x{format:X} cannot be preserved";
                        return null;
                    case FormatClass.CoveredBySynthesis:
                        continue;
                }
                var data = raw.TryReadFormatBytes(format);
                if (data is null)
                {
                    refusalReason = $"format 0x{format:X} could not be read";
                    return null;
                }
                entries.Add(new ClipboardEntry(format, data));
            }
            return new ClipboardSnapshot(entries, raw.GetSequenceNumber());
        }
        finally
        {
            raw.Close();
        }
    }

    /// <summary>
    /// Restores the snapshot only if the sequence number still equals
    /// <paramref name="expectedSequence"/>, checked while the clipboard is held open so no
    /// other process can write between the check and the restore. False means the clipboard
    /// was left as it was (preflight/sequence/open failure) or — only after the atomic
    /// check passed — that a write failed and restoration must be reported as failed.
    /// </summary>
    public bool TryRestoreIfSequenceMatches(ClipboardSnapshot snapshot, uint expectedSequence)
    {
        // Preflight everything before touching the clipboard: an allocation failure must
        // never cost the user their current clipboard contents.
        var allocations = new List<(uint Format, object Allocation)>(snapshot.Entries.Count);
        foreach (var entry in snapshot.Entries)
        {
            var allocation = raw.TryAllocate(entry);
            if (allocation is null)
            {
                FreeAll(allocations);
                return false;
            }
            allocations.Add((entry.Format, allocation));
        }

        if (!raw.TryOpen())
        {
            FreeAll(allocations);
            return false;
        }
        var consumed = 0;
        try
        {
            if (raw.GetSequenceNumber() != expectedSequence) return false;
            if (!raw.TryEmpty()) return false;

            foreach (var (format, allocation) in allocations)
            {
                var set = false;
                for (var attempt = 0; attempt < SetRetryAttempts && !set; attempt++)
                    set = raw.TrySetAllocated(format, allocation);
                if (!set) return false;
                consumed++;
            }
            return true;
        }
        finally
        {
            raw.Close();
            FreeAll(allocations, skip: consumed);
        }
    }

    private void FreeAll(List<(uint Format, object Allocation)> allocations, int skip = 0)
    {
        for (var i = skip; i < allocations.Count; i++)
            raw.FreeAllocation(allocations[i].Allocation);
    }

    private enum FormatClass { Preservable, CoveredBySynthesis, Unpreservable }

    /// <summary>
    /// Builds on <see cref="ClipboardFormatPolicy.IsPreservable"/> (the raw global-memory
    /// classification) with two orchestration-level refinements:
    /// GDI-handle formats that Windows synthesizes from a captured twin are "covered"
    /// rather than refused, and CF_ENHMETAFILE is preservable because the raw clipboard
    /// implementation serializes it via GetEnhMetaFileBits/SetEnhMetaFileBits — a
    /// documented byte-lossless round trip, unlike a raw handle copy.
    /// </summary>
    private static FormatClass Classify(uint format, IReadOnlyList<uint> allFormats) => format switch
    {
        ClipboardFormatPolicy.CF_BITMAP or ClipboardFormatPolicy.CF_PALETTE =>
            allFormats.Contains(ClipboardFormatPolicy.CF_DIB) || allFormats.Contains(ClipboardFormatPolicy.CF_DIBV5)
                ? FormatClass.CoveredBySynthesis
                : FormatClass.Unpreservable,
        ClipboardFormatPolicy.CF_METAFILEPICT =>
            allFormats.Contains(ClipboardFormatPolicy.CF_ENHMETAFILE)
                ? FormatClass.CoveredBySynthesis
                : FormatClass.Unpreservable,
        ClipboardFormatPolicy.CF_ENHMETAFILE => FormatClass.Preservable,
        _ => ClipboardFormatPolicy.IsPreservable(format) ? FormatClass.Preservable : FormatClass.Unpreservable,
    };
}
