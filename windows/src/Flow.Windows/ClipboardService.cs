using System.Runtime.InteropServices;
using Flow.Core.Abstractions;
using static Flow.Windows.Interop.NativeMethods;

namespace Flow.Windows;

/// <summary>
/// Win32 clipboard access with bounded retries — other processes routinely hold the
/// clipboard open for a few milliseconds, so a single failed OpenClipboard is not an error.
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    private const int OpenAttempts = 10;
    private const int OpenRetryDelayMs = 15;

    public string? TryReadText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        if (!TryOpenClipboard()) return null;
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == 0) return null;
            var ptr = GlobalLock(handle);
            if (ptr == 0) return null;
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public ClipboardToken? TrySetText(string text)
    {
        var bytes = (nuint)((text.Length + 1) * sizeof(char));
        var hMem = GlobalAlloc(GMEM_MOVEABLE, bytes);
        if (hMem == 0) return null;

        var ptr = GlobalLock(hMem);
        if (ptr == 0)
        {
            GlobalFree(hMem);
            return null;
        }
        unsafe
        {
            fixed (char* src = text)
            {
                Buffer.MemoryCopy(src, (void*)ptr, (long)bytes, text.Length * sizeof(char));
            }
            ((char*)ptr)[text.Length] = '\0';
        }
        GlobalUnlock(hMem);

        if (!TryOpenClipboard())
        {
            GlobalFree(hMem);
            return null;
        }
        try
        {
            EmptyClipboard();
            if (SetClipboardData(CF_UNICODETEXT, hMem) == 0)
            {
                GlobalFree(hMem); // ownership not transferred on failure
                return null;
            }
            // Capture the sequence number while the clipboard is still open: nobody else
            // can write yet, so the token provably identifies OUR write. Capturing after
            // CloseClipboard could adopt another app's racing write as our own.
            return new ClipboardToken(GetClipboardSequenceNumber());
        }
        finally
        {
            CloseClipboard();
        }
    }

    public uint GetSequenceNumber() => GetClipboardSequenceNumber();

    private static bool TryOpenClipboard()
    {
        for (var i = 0; i < OpenAttempts; i++)
        {
            if (OpenClipboard(0)) return true;
            Thread.Sleep(OpenRetryDelayMs);
        }
        return false;
    }
}
