using System.Runtime.InteropServices;

namespace Flow.Windows.Interop;

/// <summary>
/// Clipboard and enhanced-metafile calls the fidelity implementation needs on top of
/// <see cref="NativeMethods"/>.
/// </summary>
/// <remarks>
/// Its own class rather than appended to <see cref="NativeMethods"/>, because that file is edited
/// by more than one workstream and a shared file is a collision waiting to happen.
/// </remarks>
internal static class ClipboardNativeMethods
{
    /// <summary>Walks the formats currently on the clipboard. Pass 0 to start.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint EnumClipboardFormats(uint format);

    [DllImport("kernel32.dll")]
    internal static extern nuint GlobalSize(nint hMem);

    // ---- Enhanced metafiles -------------------------------------------------
    //
    // CF_ENHMETAFILE is a GDI handle, not global memory, so it cannot be copied byte-for-byte
    // like the rest. It can still be preserved losslessly by serializing it to bits and
    // recreating it, which is what these three calls are for. Without this, every copy out of an
    // Office application — which puts an EMF alongside the DIB and RTF — would fail capture and
    // Flow would refuse to paste.

    /// <summary>Size in bytes when <paramref name="lpData"/> is null; bytes written otherwise.</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern uint GetEnhMetaFileBits(nint hemf, uint nSize, byte[]? lpData);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint SetEnhMetaFileBits(uint nSize, byte[] lpData);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteEnhMetaFile(nint hemf);
}
