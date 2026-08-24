namespace Flow.Shell.Core;

/// <summary>A screen rectangle in physical pixels. Mirrors Win32 <c>RECT</c>.</summary>
public readonly record struct PanelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>Which screen edge the taskbar occupies, derived from monitor versus work area.</summary>
public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>Where the panel goes and how big its pieces are.</summary>
/// <remarks>
/// Pulled out of the window so it can be tested. None of this is verifiable by looking at a
/// screenshot on one machine anyway — the cases that matter are a taskbar moved to the left edge,
/// a second monitor at a different scale factor, and a tray icon that Windows declines to locate.
/// Arithmetic is the only way to cover those without a wall of hardware.
/// </remarks>
public static class PanelLayout
{
    public const int DefaultDpi = 96;

    /// <summary>
    /// Scale a design pixel to a physical pixel. The app manifest declares PerMonitorV2, so
    /// Windows does no scaling for us and an unscaled constant renders at two-thirds size on a
    /// 150% display.
    /// </summary>
    public static int Scale(int designPixels, int dpi) =>
        designPixels * (dpi <= 0 ? DefaultDpi : dpi) / DefaultDpi;

    /// <summary>Which edge the taskbar sits on, inferred from the space the work area gives up.</summary>
    public static TaskbarEdge EdgeOf(PanelRect monitor, PanelRect work)
    {
        var bottom = monitor.Bottom - work.Bottom;
        var top = work.Top - monitor.Top;
        var left = work.Left - monitor.Left;
        var right = monitor.Right - work.Right;

        var largest = Math.Max(Math.Max(bottom, top), Math.Max(left, right));

        // Ties resolve to Bottom because that is where Windows puts it unless told otherwise —
        // including the case where the work area equals the monitor and every inset is zero.
        if (largest <= 0 || largest == bottom) return TaskbarEdge.Bottom;
        if (largest == top) return TaskbarEdge.Top;
        if (largest == left) return TaskbarEdge.Left;
        return TaskbarEdge.Right;
    }

    /// <summary>
    /// Top-left corner for the panel: beside the clock, clear of the taskbar, and never off the
    /// work area.
    /// </summary>
    /// <param name="trayIcon">
    /// Where Windows says Flow's tray icon is, or null when it declines to say — which happens
    /// while the icon is inside the overflow flyout.
    /// </param>
    /// <param name="monitor">Full monitor bounds, used only to work out where the taskbar is.</param>
    /// <param name="work">Work area of the monitor the panel belongs on.</param>
    /// <param name="gap">Clearance from the taskbar and screen edge, already DPI-scaled.</param>
    public static (int X, int Y) Anchor(
        PanelRect? trayIcon,
        PanelRect monitor,
        PanelRect work,
        int width,
        int height,
        int gap)
    {
        var edge = EdgeOf(monitor, work);

        // No icon to point at: the corner nearest the clock for this taskbar edge.
        if (trayIcon is not { } icon || icon.Width <= 0 || icon.Height <= 0)
        {
            return edge switch
            {
                TaskbarEdge.Top => (work.Right - width - gap, work.Top + gap),
                TaskbarEdge.Left => (work.Left + gap, work.Bottom - height - gap),
                _ => (work.Right - width - gap, work.Bottom - height - gap),
            };
        }

        return edge switch
        {
            // Horizontal taskbar: right-align the panel to the icon, then sit above or below it.
            TaskbarEdge.Bottom => (
                ClampX(icon.Right - width, work, width, gap),
                work.Bottom - height - gap),

            TaskbarEdge.Top => (
                ClampX(icon.Right - width, work, width, gap),
                work.Top + gap),

            // Vertical taskbar: hug the taskbar's edge, then bottom-align the panel to the icon.
            TaskbarEdge.Left => (
                work.Left + gap,
                ClampY(icon.Bottom - height, work, height, gap)),

            _ => (
                work.Right - width - gap,
                ClampY(icon.Bottom - height, work, height, gap)),
        };
    }

    private static int ClampX(int x, PanelRect work, int width, int gap)
    {
        var max = work.Right - width - gap;
        var min = work.Left + gap;
        return max < min ? min : Math.Clamp(x, min, max);
    }

    private static int ClampY(int y, PanelRect work, int height, int gap)
    {
        var max = work.Bottom - height - gap;
        var min = work.Top + gap;
        return max < min ? min : Math.Clamp(y, min, max);
    }
}
