using System.Runtime.InteropServices;

namespace Flow.Windows.Interop;

/// <summary>
/// Native surface the transcript panel needs on top of <see cref="NativeMethods"/>.
/// </summary>
/// <remarks>
/// Kept in its own class rather than appended to <see cref="NativeMethods"/> because the two are
/// edited by different workstreams and a shared file is a collision waiting to happen. Nothing
/// here is declared twice — this is strictly the panel's additions.
/// </remarks>
internal static class PanelNativeMethods
{
    // ---- DPI ----------------------------------------------------------------

    /// <summary>
    /// Per-monitor DPI for a window. The app manifest declares PerMonitorV2, so Windows will not
    /// scale the panel for us — every dimension has to be computed from this.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hWnd);

    internal const int DefaultDpi = 96;

    // ---- Fonts --------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LOGFONTW
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint CreateFontIndirectW(ref LOGFONTW lplf);

    internal const int FW_NORMAL = 400;
    internal const int FW_SEMIBOLD = 600;
    internal const byte DEFAULT_CHARSET = 1;
    internal const byte CLEARTYPE_QUALITY = 5;

    // ---- Device contexts and double buffering -------------------------------

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(
        nint hdcDest, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    internal const uint SRCCOPY = 0x00CC0020;

    // ---- Drawing ------------------------------------------------------------

    [DllImport("gdi32.dll")]
    internal static extern int SetBkMode(nint hdc, int mode);

    internal const int TRANSPARENT = 1;

    [DllImport("gdi32.dll")]
    internal static extern nint CreatePen(int style, int width, uint color);

    internal const int PS_SOLID = 0;

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RoundRect(nint hdc, int left, int top, int right, int bottom, int w, int h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Ellipse(nint hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    internal static extern nint GetStockObject(int i);

    internal const int NULL_PEN = 8;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint hWnd, out NativeMethods.RECT lpRect);

    // DT_CALCRECT measures instead of drawing — used to size the panel to its content.
    internal const uint DT_CALCRECT = 0x0400;
    internal const uint DT_SINGLELINE = 0x0020;
    internal const uint DT_VCENTER = 0x0004;
    internal const uint DT_CENTER = 0x0001;

    // ---- Windows 11 chrome --------------------------------------------------

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int cbAttr);

    /// <summary>Windows 11 rounded corners. Ignored (harmlessly) on Windows 10.</summary>
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    internal const int DWMWCP_ROUND = 2;

    internal const int DWMWA_BORDER_COLOR = 34;

    // ---- Tray anchoring -----------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

    /// <summary>
    /// Screen rectangle of our own tray icon. This is what the panel anchors to, so it lands
    /// beside the clock wherever the user has put the taskbar.
    /// </summary>
    [DllImport("shell32.dll")]
    internal static extern int Shell_NotifyIconGetRect(
        ref NOTIFYICONIDENTIFIER identifier, out NativeMethods.RECT iconLocation);

    // ---- Monitors -----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public NativeMethods.RECT rcMonitor;
        public NativeMethods.RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(NativeMethods.POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO lpmi);

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    // ---- Timers -------------------------------------------------------------

    [DllImport("user32.dll")]
    internal static extern nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool KillTimer(nint hWnd, nuint uIDEvent);

    internal const uint WM_TIMER = 0x0113;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_MOUSELEAVE = 0x02A3;
    internal const uint WM_ERASEBKGND = 0x0014;
    internal const uint WM_DPICHANGED = 0x02E0;
}
