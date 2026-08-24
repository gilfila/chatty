using System.Runtime.InteropServices;
using Flow.Shell.Core;
using static Flow.Windows.Interop.NativeMethods;
using static Flow.Windows.Interop.ClipboardNativeMethods;

namespace Flow.Windows;

/// <summary>
/// The Win32 half of the clipboard fidelity seam: raw verbs only, no policy.
/// </summary>
/// <remarks>
/// Every decision about what may be preserved, what must be refused, and when it is safe to empty
/// the clipboard lives in <see cref="ClipboardFidelityPolicy"/>, which is portable and tested. This
/// class exists to do exactly what it is told and to report honestly when it cannot — the one thing
/// it must never do is quietly succeed at less than it was asked for.
///
/// <para>
/// That is why it replaces the earlier snapshot service rather than extending it. That version
/// silently skipped formats it could not carry, which produces a partial snapshot the policy layer
/// now forbids: restoring one drops whatever was skipped while reporting success.
/// <see cref="TryReadFormatBytes"/> returns null instead, and the policy turns that into a refusal
/// before anything is written.
/// </para>
///
/// <para>
/// Not thread-safe, and it must not be: the Windows clipboard is a machine-wide lock, so all use
/// is serialized by the paste path holding it open across a capture or a restore.
/// </para>
/// </remarks>
public sealed class WindowsRawClipboard : IRawClipboard
{
    private const uint CF_ENHMETAFILE = 14;

    /// <summary>The clipboard is a global lock; another app holding it briefly is normal.</summary>
    private const int OpenAttempts = 10;

    private const int OpenRetryDelayMs = 15;

    /// <summary>A native allocation waiting to be placed on the clipboard.</summary>
    /// <remarks>
    /// Carries how to free it, because the two kinds are freed differently and getting that wrong
    /// leaks a GDI object or corrupts the heap.
    /// </remarks>
    private sealed record Allocation(nint Handle, bool IsEnhMetafile);

    public bool TryOpen()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (OpenClipboard(0)) return true;
            Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }

    public void Close() => CloseClipboard();

    public uint GetSequenceNumber() => GetClipboardSequenceNumber();

    public IReadOnlyList<uint> EnumerateFormats()
    {
        var formats = new List<uint>();
        uint format = 0;

        while ((format = EnumClipboardFormats(format)) != 0)
        {
            formats.Add(format);
        }

        return formats;
    }

    public byte[]? TryReadFormatBytes(uint format)
    {
        var handle = GetClipboardData(format);

        // A delayed-render owner that failed to produce its data. Null, not empty — the caller
        // must not treat "the owner gave us nothing" as "there was nothing there".
        if (handle == 0) return null;

        return format == CF_ENHMETAFILE
            ? ReadEnhMetafile(handle)
            : ReadGlobal(handle);
    }

    private static byte[]? ReadEnhMetafile(nint handle)
    {
        var size = GetEnhMetaFileBits(handle, 0, null);
        if (size == 0) return null;

        var bits = new byte[size];
        return GetEnhMetaFileBits(handle, size, bits) == size ? bits : null;
    }

    private static byte[]? ReadGlobal(nint handle)
    {
        var size = (int)GlobalSize(handle);
        if (size <= 0) return null;

        var pointer = GlobalLock(handle);
        if (pointer == 0) return null;

        try
        {
            var data = new byte[size];
            Marshal.Copy(pointer, data, 0, size);
            return data;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    public object? TryAllocate(ClipboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Format == CF_ENHMETAFILE)
        {
            var metafile = SetEnhMetaFileBits((uint)entry.Data.Length, entry.Data);
            return metafile == 0 ? null : new Allocation(metafile, IsEnhMetafile: true);
        }

        var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)entry.Data.Length);
        if (handle == 0) return null;

        var pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            GlobalFree(handle);
            return null;
        }

        try
        {
            Marshal.Copy(entry.Data, 0, pointer, entry.Data.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return new Allocation(handle, IsEnhMetafile: false);
    }

    public void FreeAllocation(object allocation)
    {
        if (allocation is not Allocation owned) return;

        if (owned.IsEnhMetafile)
        {
            DeleteEnhMetaFile(owned.Handle);
        }
        else
        {
            GlobalFree(owned.Handle);
        }
    }

    public bool TryEmpty() => EmptyClipboard();

    public bool TrySetAllocated(uint format, object allocation)
    {
        if (allocation is not Allocation owned) return false;

        // On success Windows owns the allocation and freeing it here would be a double free; on
        // failure it is still ours, and the policy layer frees it.
        return SetClipboardData(format, owned.Handle) != 0;
    }
}
