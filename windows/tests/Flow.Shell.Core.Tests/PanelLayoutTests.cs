using Flow.Shell.Core;

namespace Flow.Shell.Core.Tests;

/// <summary>
/// Where the panel lands. The release gate is "beside the clock, at the right size" — these cover
/// the cases a single Windows screenshot never would: a moved taskbar, a second monitor at a
/// different scale, and a tray icon Windows will not locate.
/// </summary>
public sealed class PanelLayoutTests
{
    // A 1920x1080 monitor with a 48px taskbar at the bottom, which is the Windows 11 default.
    private static readonly PanelRect Monitor = new(0, 0, 1920, 1080);
    private static readonly PanelRect WorkBottomBar = new(0, 0, 1920, 1032);

    private const int Width = 360;
    private const int Height = 120;
    private const int Gap = 12;

    // ---- DPI ----------------------------------------------------------------

    [Theory]
    [InlineData(96, 360)]
    [InlineData(120, 450)]   // 125%
    [InlineData(144, 540)]   // 150%
    [InlineData(192, 720)]   // 200%
    public void DesignPixelsScaleWithTheMonitor(int dpi, int expected)
    {
        Assert.Equal(expected, PanelLayout.Scale(360, dpi));
    }

    [Fact]
    public void AnUnreportedDpiFallsBackToOneHundredPercent()
    {
        Assert.Equal(360, PanelLayout.Scale(360, 0));
        Assert.Equal(360, PanelLayout.Scale(360, -1));
    }

    // ---- Taskbar edge -------------------------------------------------------

    [Fact]
    public void ABottomTaskbarIsDetected()
    {
        Assert.Equal(TaskbarEdge.Bottom, PanelLayout.EdgeOf(Monitor, WorkBottomBar));
    }

    [Fact]
    public void ATopTaskbarIsDetected()
    {
        Assert.Equal(TaskbarEdge.Top, PanelLayout.EdgeOf(Monitor, new PanelRect(0, 48, 1920, 1080)));
    }

    [Fact]
    public void ALeftTaskbarIsDetected()
    {
        Assert.Equal(TaskbarEdge.Left, PanelLayout.EdgeOf(Monitor, new PanelRect(72, 0, 1920, 1080)));
    }

    [Fact]
    public void ARightTaskbarIsDetected()
    {
        Assert.Equal(TaskbarEdge.Right, PanelLayout.EdgeOf(Monitor, new PanelRect(0, 0, 1848, 1080)));
    }

    [Fact]
    public void AnAutoHiddenTaskbarLeavesTheWorkAreaWholeAndStillReadsAsBottom()
    {
        Assert.Equal(TaskbarEdge.Bottom, PanelLayout.EdgeOf(Monitor, Monitor));
    }

    // ---- Anchoring ----------------------------------------------------------

    [Fact]
    public void ThePanelSitsAboveTheTaskbarNotAtTheTopOfTheScreen()
    {
        // The regression this whole file exists for: the plan said "top-right", but on Windows 11
        // the clock is bottom-right.
        var icon = new PanelRect(1780, 1040, 1804, 1064);

        var (_, y) = PanelLayout.Anchor(icon, Monitor, WorkBottomBar, Width, Height, Gap);

        Assert.Equal(WorkBottomBar.Bottom - Height - Gap, y);
        Assert.True(y > Monitor.Bottom / 2, "panel must be in the lower half of the screen");
    }

    [Fact]
    public void ThePanelRightAlignsToTheTrayIcon()
    {
        var icon = new PanelRect(1780, 1040, 1804, 1064);

        var (x, _) = PanelLayout.Anchor(icon, Monitor, WorkBottomBar, Width, Height, Gap);

        Assert.Equal(1804 - Width, x);
    }

    [Fact]
    public void ThePanelNeverHangsOffTheRightEdge()
    {
        // An icon hard against the screen edge would push a right-aligned panel past the work area.
        var icon = new PanelRect(1900, 1040, 1920, 1064);

        var (x, _) = PanelLayout.Anchor(icon, Monitor, WorkBottomBar, Width, Height, Gap);

        Assert.True(x + Width <= WorkBottomBar.Right - Gap);
    }

    [Fact]
    public void ThePanelNeverHangsOffTheLeftEdge()
    {
        var icon = new PanelRect(4, 1040, 28, 1064);

        var (x, _) = PanelLayout.Anchor(icon, Monitor, WorkBottomBar, Width, Height, Gap);

        Assert.True(x >= WorkBottomBar.Left);
    }

    [Fact]
    public void ATopTaskbarPutsThePanelUnderIt()
    {
        var work = new PanelRect(0, 48, 1920, 1080);
        var icon = new PanelRect(1780, 8, 1804, 40);

        var (x, y) = PanelLayout.Anchor(icon, Monitor, work, Width, Height, Gap);

        Assert.Equal(work.Top + Gap, y);
        Assert.Equal(1804 - Width, x);
    }

    [Fact]
    public void ALeftTaskbarPutsThePanelBesideItAndBottomAlignsToTheIcon()
    {
        var work = new PanelRect(72, 0, 1920, 1080);
        var icon = new PanelRect(20, 900, 52, 932);

        var (x, y) = PanelLayout.Anchor(icon, Monitor, work, Width, Height, Gap);

        Assert.Equal(work.Left + Gap, x);
        Assert.Equal(932 - Height, y);
    }

    [Fact]
    public void ARightTaskbarPutsThePanelBesideIt()
    {
        var work = new PanelRect(0, 0, 1848, 1080);
        var icon = new PanelRect(1870, 900, 1902, 932);

        var (x, y) = PanelLayout.Anchor(icon, Monitor, work, Width, Height, Gap);

        Assert.Equal(work.Right - Width - Gap, x);
        Assert.Equal(932 - Height, y);
    }

    [Fact]
    public void AVerticalTaskbarNeverPushesThePanelOffTheBottom()
    {
        var work = new PanelRect(72, 0, 1920, 1080);
        var icon = new PanelRect(20, 1060, 52, 1080);

        var (_, y) = PanelLayout.Anchor(icon, Monitor, work, Width, Height, Gap);

        Assert.True(y + Height <= work.Bottom - Gap);
    }

    // ---- No icon ------------------------------------------------------------

    [Fact]
    public void AnIconInTheOverflowFlyoutFallsBackToTheCornerNearestTheClock()
    {
        // Shell_NotifyIconGetRect fails while the icon is hidden in the overflow.
        var (x, y) = PanelLayout.Anchor(null, Monitor, WorkBottomBar, Width, Height, Gap);

        Assert.Equal(WorkBottomBar.Right - Width - Gap, x);
        Assert.Equal(WorkBottomBar.Bottom - Height - Gap, y);
    }

    [Fact]
    public void ADegenerateIconRectIsTreatedAsNoIcon()
    {
        var empty = new PanelRect(0, 0, 0, 0);

        Assert.Equal(
            PanelLayout.Anchor(null, Monitor, WorkBottomBar, Width, Height, Gap),
            PanelLayout.Anchor(empty, Monitor, WorkBottomBar, Width, Height, Gap));
    }

    // ---- Second monitor -----------------------------------------------------

    [Fact]
    public void ASecondMonitorLeftOfThePrimaryAnchorsInItsOwnCoordinates()
    {
        // Monitors to the left of the primary have negative coordinates, which is the classic way
        // to end up with a panel drawn off-screen.
        var monitor = new PanelRect(-1920, 0, 0, 1080);
        var work = new PanelRect(-1920, 0, 0, 1032);
        var icon = new PanelRect(-140, 1040, -116, 1064);

        var (x, y) = PanelLayout.Anchor(icon, monitor, work, Width, Height, Gap);

        Assert.True(x >= work.Left && x + Width <= work.Right);
        Assert.Equal(work.Bottom - Height - Gap, y);
    }

    [Fact]
    public void AScaledSecondMonitorGetsAScaledPanel()
    {
        // 3840x2160 at 200%: the panel is twice as many physical pixels, and still fits.
        var monitor = new PanelRect(0, 0, 3840, 2160);
        var work = new PanelRect(0, 0, 3840, 2064);
        var width = PanelLayout.Scale(360, 192);
        var height = PanelLayout.Scale(120, 192);
        var gap = PanelLayout.Scale(12, 192);
        var icon = new PanelRect(3560, 2080, 3608, 2128);

        var (x, y) = PanelLayout.Anchor(icon, monitor, work, width, height, gap);

        Assert.Equal(720, width);
        Assert.True(x >= work.Left && x + width <= work.Right);
        Assert.Equal(work.Bottom - height - gap, y);
    }

    // ---- Sweep --------------------------------------------------------------

    public static TheoryData<PanelRect, PanelRect> EveryTaskbarPlacement() => new()
    {
        { Monitor, WorkBottomBar },
        { Monitor, new PanelRect(0, 48, 1920, 1080) },
        { Monitor, new PanelRect(72, 0, 1920, 1080) },
        { Monitor, new PanelRect(0, 0, 1848, 1080) },
        { Monitor, Monitor },
    };

    [Theory]
    [MemberData(nameof(EveryTaskbarPlacement))]
    public void ThePanelIsAlwaysFullyInsideTheWorkArea(PanelRect monitor, PanelRect work)
    {
        // Sweep the icon across the whole monitor: wherever Windows reports it, the panel stays on
        // screen. An off-screen panel is indistinguishable from a broken app.
        for (var ix = monitor.Left; ix < monitor.Right; ix += 97)
        {
            for (var iy = monitor.Top; iy < monitor.Bottom; iy += 89)
            {
                var icon = new PanelRect(ix, iy, ix + 24, iy + 24);
                var (x, y) = PanelLayout.Anchor(icon, monitor, work, Width, Height, Gap);

                Assert.InRange(x, work.Left, work.Right - Width);
                Assert.InRange(y, work.Top, work.Bottom - Height);
            }
        }
    }
}
