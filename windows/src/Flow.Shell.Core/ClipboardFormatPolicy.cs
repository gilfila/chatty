namespace Flow.Shell.Core;

/// <summary>
/// Which Windows clipboard formats Flow can snapshot and put back byte-for-byte.
/// </summary>
/// <remarks>
/// Flow borrows the clipboard to paste, so it owes the user everything that was on it — not just
/// the text. The locked product decision is that text-only restore is not shippable.
///
/// <para>
/// The dividing line is what the handle on the clipboard actually is. Formats whose handle is
/// global memory can be copied out with <c>GlobalSize</c>/<c>GlobalLock</c> and written back
/// verbatim. Formats whose handle is a GDI object, an owner-display callback, or a private
/// app-managed allocation cannot: <c>GlobalSize</c> on them is meaningless, and copying whatever
/// it returns would either fail or write garbage back onto the user's clipboard.
/// </para>
///
/// <para>
/// So those are skipped rather than mangled. Skipping is safe for the image case in practice,
/// because an app that publishes <c>CF_BITMAP</c> almost always publishes <c>CF_DIB</c> alongside
/// it, and the DIB carries the same pixels in global memory.
/// </para>
///
/// <para>Pure and platform-free so the classification is testable without Windows.</para>
/// </remarks>
public static class ClipboardFormatPolicy
{
    // Predefined formats whose handle is global memory — safe to copy and restore.
    public const uint CF_TEXT = 1;
    public const uint CF_OEMTEXT = 7;
    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_HDROP = 15;
    public const uint CF_LOCALE = 16;
    public const uint CF_DIBV5 = 17;

    // Predefined formats whose handle is a GDI object, not global memory.
    public const uint CF_BITMAP = 2;
    public const uint CF_METAFILEPICT = 3;
    public const uint CF_PALETTE = 9;
    public const uint CF_ENHMETAFILE = 14;

    // Owner-display formats: the owning app paints them, there is nothing to copy.
    public const uint CF_OWNERDISPLAY = 0x0080;
    public const uint CF_DSPTEXT = 0x0081;
    public const uint CF_DSPBITMAP = 0x0082;
    public const uint CF_DSPMETAFILEPICT = 0x0083;
    public const uint CF_DSPENHMETAFILE = 0x008E;

    // Private formats: lifetime is the owning app's, and it frees them on EmptyClipboard.
    public const uint CF_PRIVATEFIRST = 0x0200;
    public const uint CF_PRIVATELAST = 0x02FF;

    // GDI object handles. Like the predefined GDI formats, these are not global memory.
    public const uint CF_GDIOBJFIRST = 0x0300;
    public const uint CF_GDIOBJLAST = 0x03FF;

    /// <summary>Registered formats — "HTML Format", "Rich Text Format", and every app's own.</summary>
    public const uint CF_REGISTEREDFIRST = 0xC000;

    /// <summary>
    /// True when the format's handle is global memory, so Flow can snapshot and restore it exactly.
    /// </summary>
    public static bool IsPreservable(uint format) => format switch
    {
        0 => false,

        // GDI object handles. CF_DIB carries the same pixels in global memory.
        CF_BITMAP or CF_PALETTE or CF_METAFILEPICT or CF_ENHMETAFILE => false,

        // Owner-painted; there is no buffer behind these.
        CF_OWNERDISPLAY or CF_DSPTEXT or CF_DSPBITMAP or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE => false,

        // Owner-managed lifetime — freed by the owner on EmptyClipboard.
        >= CF_PRIVATEFIRST and <= CF_PRIVATELAST => false,

        // Also GDI handles, and the range a straight "not predefined" test silently lets through.
        >= CF_GDIOBJFIRST and <= CF_GDIOBJLAST => false,

        _ => true,
    };

    /// <summary>
    /// Whether a snapshot faithfully represents what was on the clipboard, or whether something
    /// was present that Flow could not carry.
    /// </summary>
    /// <remarks>
    /// Used to tell the user the truth when their clipboard cannot be fully restored, rather than
    /// silently handing back less than they had.
    /// </remarks>
    public static bool IsCompleteSnapshot(IEnumerable<uint> formatsOnClipboard)
    {
        ArgumentNullException.ThrowIfNull(formatsOnClipboard);
        return formatsOnClipboard.All(IsPreservable);
    }
}
