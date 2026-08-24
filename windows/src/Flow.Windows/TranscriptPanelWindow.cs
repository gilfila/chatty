using Flow.Core.Abstractions;
using Flow.Core.Session;
using Flow.Shell.Core;
using static Flow.Windows.Interop.NativeMethods;
using static Flow.Windows.Interop.PanelNativeMethods;

namespace Flow.Windows;

/// <summary>
/// The compact live-transcript panel: a topmost, non-activating tool window anchored beside the
/// clock. It renders whatever <see cref="PanelPresenter"/> decided, and decides nothing itself.
/// </summary>
/// <remarks>
/// Four things about this window are load-bearing rather than cosmetic.
///
/// <para>
/// <b>It never activates.</b> <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>, and every reposition
/// passes <c>SWP_NOACTIVATE</c>. If the panel ever became the foreground window it would replace
/// the very target that was captured on the press edge, and the paste guard would correctly refuse
/// to type — Flow would break itself by showing its own UI.
/// </para>
///
/// <para>
/// <b>It anchors bottom-right, to the tray icon.</b> The delivery plan says "near the clock", and
/// on Windows 11 the clock is bottom-right, not top-right. <see cref="Shell_NotifyIconGetRect"/>
/// gives the real position of Flow's own tray icon, so the panel follows the taskbar if the user
/// moves it and lands on the right monitor in a multi-monitor setup. The work area is only a
/// fallback for when the icon cannot be located.
/// </para>
///
/// <para>
/// <b>Every dimension is DPI-scaled.</b> The manifest declares PerMonitorV2, which means Windows
/// does no scaling for us. Fixed pixel sizes would render the panel at two-thirds size on a 150%
/// display and half size at 200%.
/// </para>
///
/// <para>
/// <b>Painting is double-buffered.</b> Live partial text repaints at speech rate; painting
/// straight to the window DC flickers visibly at that cadence.
/// </para>
///
/// <para>
/// All Win32 calls happen on the panel's own message-loop thread. <see cref="Render"/> from other
/// threads is marshaled with a posted message.
/// </para>
/// </remarks>
public sealed class TranscriptPanelWindow : IPanelPresenter, IDisposable
{
    // Design sizes in logical pixels at 96 DPI. Everything is scaled through Scale().
    private const int PanelWidthDp = 360;
    private const int PadXDp = 14;
    private const int PadTopDp = 12;
    private const int PadBottomDp = 13;
    private const int DotDp = 8;
    private const int DotGapDp = 9;
    private const int HeadlineSizeDp = 13;
    private const int DetailSizeDp = 12;
    private const int BodySizeDp = 13;
    private const int ButtonSizeDp = 12;
    private const int DetailTopDp = 3;
    private const int BodyTopDp = 9;
    private const int BodyPadXDp = 10;
    private const int BodyPadYDp = 8;
    private const int ButtonTopDp = 10;
    private const int ButtonHeightDp = 30;
    private const int ButtonPadXDp = 12;
    private const int RadiusDp = 4;
    private const int EdgeGapDp = 12;
    private const int WaveBarDp = 3;
    private const int WaveGapDp = 3;
    private const int WaveMaxDp = 13;

    private const uint MSG_APPLY = WM_APP + 1;
    private const nuint TimerDismiss = 1;
    private const nuint TimerWave = 2;

    // Fluent dark. COLORREF is 0x00BBGGRR, hence the Rgb() helper.
    private static readonly uint ColorBackground = Rgb(0x2C, 0x2C, 0x2C);
    private static readonly uint ColorFill = Rgb(0x36, 0x36, 0x36);
    private static readonly uint ColorStroke = Rgb(0x3C, 0x3C, 0x3C);
    private static readonly uint ColorText1 = Rgb(0xFF, 0xFF, 0xFF);
    private static readonly uint ColorText2 = Rgb(0xC8, 0xC8, 0xC8);
    private static readonly uint ColorText3 = Rgb(0x8A, 0x8A, 0x8A);
    private static readonly uint ColorAccent = Rgb(0x00, 0x78, 0xD4);

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _ready = new();

    private Thread? _thread;
    private nint _hwnd;
    private WndProc? _wndProc; // rooted for the native window's lifetime
    private PanelView _view = PanelView.Hidden;

    private int _dpi = DefaultDpi;
    private nint _fontHeadline, _fontDetail, _fontBody, _fontButton;
    private RECT _buttonRect;
    private int _waveFrame;

    /// <summary>The panel's single action button was clicked.</summary>
    public event Action<PanelAction>? ActionInvoked;

    /// <summary>
    /// The view currently on screen. Read when an action fires so the press can be validated
    /// against what the user was actually looking at rather than against later state.
    /// </summary>
    public PanelView CurrentView
    {
        get { lock (_gate) return _view; }
    }

    public void Start()
    {
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "flow-panel" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Render a presenter decision. Safe to call from any thread.</summary>
    public void Render(PanelView view)
    {
        lock (_gate) _view = view;
        if (_hwnd != 0) PostMessageW(_hwnd, MSG_APPLY, 0, 0);
    }

    // ---- IPanelPresenter ----------------------------------------------------
    // Compatibility with the session machine's current thin contract. It cannot express a
    // recovery action or a setup state, so it is adapted rather than extended here; the session
    // machine should move to Render(PanelView) so Copy last can appear on the panel itself.

    public void Show(PanelState state, string text) => Render(state switch
    {
        PanelState.Recording => new PanelView(
            true, PanelTone.Listening, "Listening", "Release to insert · Esc to cancel",
            text, true, true, PanelAction.None, null, null),

        PanelState.Working => new PanelView(
            true, PanelTone.Working, "Turning that into text…", string.Empty,
            text, true, false, PanelAction.None, null, null),

        PanelState.Error => new PanelView(
            true, PanelTone.Error, "Something went wrong", text,
            null, false, false, PanelAction.None, null, null),

        _ => PanelView.Hidden,
    });

    /// <summary>
    /// Hide the panel, unless it is currently holding the only copy of the user's words.
    /// </summary>
    /// <remarks>
    /// <see cref="DictationSessionMachine"/> schedules a blanket hide after its transient-error
    /// window, which would take a "Saved, not typed" panel off screen on a timer and strand the
    /// transcript in the tray menu the user has no reason to open. Delivery-plan invariant 4 says a
    /// failed paste leaves the transcript reachable, and <see cref="PanelPresenter"/> is the
    /// authority on dismissal — it marks exactly these views with a null auto-dismiss. So the
    /// panel declines that hide and waits for the user.
    /// </remarks>
    public void Hide()
    {
        lock (_gate)
        {
            if (_view.IsVisible && _view.Action == PanelAction.CopyLast && _view.AutoDismissAfter is null)
            {
                return;
            }
        }

        Render(PanelView.Hidden);
    }

    // ---- Window -------------------------------------------------------------

    private void RunLoop()
    {
        var hInstance = GetModuleHandleW(null);
        _wndProc = HandleMessage;
        var cls = new WNDCLASSW
        {
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = "FlowTranscriptPanel",
        };
        if (RegisterClassW(ref cls) == 0)
        {
            _ready.Set();
            return;
        }

        _hwnd = CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "FlowTranscriptPanel", "Flow", WS_POPUP,
            0, 0, Scale(PanelWidthDp), Scale(80),
            0, 0, hInstance, 0);

        _ready.Set();
        if (_hwnd == 0) return;

        _dpi = QueryDpi(_hwnd);
        RebuildFonts();

        // Windows 11 rounds flyout corners; on Windows 10 this call fails harmlessly.
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var border = ColorStroke;
        var borderValue = unchecked((int)border);
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderValue, sizeof(int));

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        ReleaseFonts();
    }

    private nint HandleMessage(nint hWnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case MSG_APPLY:
                Apply(hWnd);
                return 0;

            case WM_ERASEBKGND:
                return 1; // every pixel is painted in WM_PAINT; erasing first only adds flicker

            case WM_PAINT:
                Paint(hWnd);
                return 0;

            case WM_TIMER when wParam == TimerDismiss:
                KillTimer(hWnd, TimerDismiss);
                Hide();
                return 0;

            case WM_TIMER when wParam == TimerWave:
                _waveFrame++;
                InvalidateRect(hWnd, 0, false);
                return 0;

            case WM_LBUTTONDOWN:
                OnClick(lParam);
                return 0;

            case WM_DPICHANGED:
                _dpi = (int)(wParam & 0xFFFF);
                RebuildFonts();
                Apply(hWnd);
                return 0;

            default:
                return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private void OnClick(nint lParam)
    {
        PanelView view;
        lock (_gate) view = _view;
        if (view.Action == PanelAction.None) return;

        // LOWORD/HIWORD of lParam are the click point in client coordinates.
        var x = (short)(lParam & 0xFFFF);
        var y = (short)((lParam >> 16) & 0xFFFF);

        if (x >= _buttonRect.left && x < _buttonRect.right &&
            y >= _buttonRect.top && y < _buttonRect.bottom)
        {
            ActionInvoked?.Invoke(view.Action);
        }
    }

    private void Apply(nint hWnd)
    {
        PanelView view;
        lock (_gate) view = _view;

        KillTimer(hWnd, TimerDismiss);
        KillTimer(hWnd, TimerWave);

        if (!view.IsVisible)
        {
            ShowWindow(hWnd, SW_HIDE);
            return;
        }

        var width = Scale(PanelWidthDp);
        var height = MeasureHeight(view, width);
        var (x, y) = AnchorPoint(hWnd, width, height);

        SetWindowPos(hWnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(hWnd, 0, false);

        if (view.WaveformActive)
        {
            SetTimer(hWnd, TimerWave, 90, 0);
        }

        if (view.AutoDismissAfter is { } linger)
        {
            SetTimer(hWnd, TimerDismiss, (uint)linger.TotalMilliseconds, 0);
        }
    }

    /// <summary>
    /// Beside the clock — anchored to Flow's own tray icon when Windows can locate it, so the
    /// panel follows the taskbar to whichever edge the user put it and lands on the right monitor.
    /// </summary>
    /// <remarks>
    /// This method only gathers the Win32 facts. The placement arithmetic lives in
    /// <see cref="PanelLayout.Anchor"/> so the cases that actually go wrong — a taskbar on the left
    /// edge, a monitor at negative coordinates, an icon hidden in the overflow flyout — are covered
    /// by tests rather than by owning the hardware.
    /// </remarks>
    private (int X, int Y) AnchorPoint(nint hWnd, int width, int height)
    {
        var gap = Scale(EdgeGapDp);
        PanelRect? iconRect = null;
        var monitor = hWnd;

        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = ShellHost.TrayOwnerWindow,
            uID = ShellHost.TrayIconId,
            guidItem = Guid.Empty,
        };

        // Fails while the icon is inside the overflow flyout, which is why null is a real case.
        if (ShellHost.TrayOwnerWindow != 0 &&
            Shell_NotifyIconGetRect(ref id, out var icon) == 0 &&
            icon.right > icon.left)
        {
            iconRect = new PanelRect(icon.left, icon.top, icon.right, icon.bottom);
            monitor = MonitorFromPoint(new POINT { x = icon.left, y = icon.top }, MONITOR_DEFAULTTONEAREST);
        }
        else
        {
            monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        }

        var info = MonitorInfoFor(monitor);

        return PanelLayout.Anchor(
            iconRect,
            ToPanelRect(info.rcMonitor),
            ToPanelRect(info.rcWork),
            width,
            height,
            gap);
    }

    private static PanelRect ToPanelRect(RECT r) => new(r.left, r.top, r.right, r.bottom);

    private static MONITORINFO MonitorInfoFor(nint monitor)
    {
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
        {
            // Primary work area, which is all SystemParametersInfo can tell us.
            var work = new RECT();
            SystemParametersInfoW(SPI_GETWORKAREA, 0, ref work, 0);
            info.rcWork = work;
            info.rcMonitor = work;
        }
        return info;
    }

    // ---- Layout and painting ------------------------------------------------

    private int Scale(int dp) => PanelLayout.Scale(dp, _dpi);

    private static int QueryDpi(nint hWnd)
    {
        var dpi = GetDpiForWindow(hWnd);
        return dpi == 0 ? DefaultDpi : (int)dpi;
    }

    private void RebuildFonts()
    {
        ReleaseFonts();
        _fontHeadline = MakeFont(HeadlineSizeDp, FW_SEMIBOLD);
        _fontDetail = MakeFont(DetailSizeDp, FW_NORMAL);
        _fontBody = MakeFont(BodySizeDp, FW_NORMAL);
        _fontButton = MakeFont(ButtonSizeDp, FW_SEMIBOLD);
    }

    private nint MakeFont(int sizeDp, int weight)
    {
        var lf = new LOGFONTW
        {
            lfHeight = -Scale(sizeDp),
            lfWeight = weight,
            lfCharSet = DEFAULT_CHARSET,
            lfQuality = CLEARTYPE_QUALITY,
            // Segoe UI Variable is the Windows 11 UI face; Segoe UI is the Windows 10 fallback.
            // GDI falls back on its own if neither is installed.
            lfFaceName = "Segoe UI Variable Text",
        };

        var font = CreateFontIndirectW(ref lf);
        if (font != 0) return font;

        lf.lfFaceName = "Segoe UI";
        return CreateFontIndirectW(ref lf);
    }

    private void ReleaseFonts()
    {
        foreach (var font in new[] { _fontHeadline, _fontDetail, _fontBody, _fontButton })
        {
            if (font != 0) DeleteObject(font);
        }
        _fontHeadline = _fontDetail = _fontBody = _fontButton = 0;
    }

    /// <summary>Height the content needs. Measured, not guessed, so long detail text is not clipped.</summary>
    private int MeasureHeight(PanelView view, int width)
    {
        var dc = CreateCompatibleDC(0);
        try
        {
            var y = Scale(PadTopDp);
            var contentWidth = width - (Scale(PadXDp) * 2);

            y += Math.Max(Scale(18), MeasureText(dc, _fontHeadline, view.Headline, contentWidth, singleLine: true));

            if (!string.IsNullOrEmpty(view.Detail))
            {
                y += Scale(DetailTopDp);
                y += MeasureText(dc, _fontDetail, view.Detail, contentWidth, singleLine: false);
            }

            if (!string.IsNullOrEmpty(view.BodyText))
            {
                y += Scale(BodyTopDp);
                var inner = contentWidth - (Scale(BodyPadXDp) * 2);
                var textHeight = MeasureText(dc, _fontBody, view.BodyText!, inner, singleLine: false);
                y += Math.Min(textHeight, Scale(BodySizeDp * 3 + 8)) + (Scale(BodyPadYDp) * 2);
            }

            if (view.Action != PanelAction.None)
            {
                y += Scale(ButtonTopDp) + Scale(ButtonHeightDp);
            }

            return y + Scale(PadBottomDp);
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    private static int MeasureText(nint dc, nint font, string text, int width, bool singleLine)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var previous = SelectObject(dc, font);
        var rect = new RECT { left = 0, top = 0, right = width, bottom = 0 };
        var format = DT_CALCRECT | DT_NOPREFIX | (singleLine ? DT_SINGLELINE : DT_WORDBREAK);
        DrawTextW(dc, text, -1, ref rect, format);
        SelectObject(dc, previous);
        return rect.bottom - rect.top;
    }

    private void Paint(nint hWnd)
    {
        var hdc = BeginPaint(hWnd, out var ps);
        try
        {
            GetClientRect(hWnd, out var client);
            var width = client.right - client.left;
            var height = client.bottom - client.top;
            if (width <= 0 || height <= 0) return;

            // Double buffer: compose off-screen, then blit once. Partial text repaints at speech
            // rate, which flickers badly if drawn straight to the window DC.
            var mem = CreateCompatibleDC(hdc);
            var bitmap = CreateCompatibleBitmap(hdc, width, height);
            var oldBitmap = SelectObject(mem, bitmap);
            try
            {
                PaintContent(mem, width, height);
                BitBlt(hdc, 0, 0, width, height, mem, 0, 0, SRCCOPY);
            }
            finally
            {
                SelectObject(mem, oldBitmap);
                DeleteObject(bitmap);
                DeleteDC(mem);
            }
        }
        finally
        {
            EndPaint(hWnd, ref ps);
        }
    }

    private void PaintContent(nint dc, int width, int height)
    {
        PanelView view;
        lock (_gate) view = _view;

        var full = new RECT { left = 0, top = 0, right = width, bottom = height };
        FillSolid(dc, full, ColorBackground);
        SetBkMode(dc, TRANSPARENT);

        var padX = Scale(PadXDp);
        var contentWidth = width - (padX * 2);
        var y = Scale(PadTopDp);

        // Row: state dot, headline, waveform.
        var dot = Scale(DotDp);
        var headlineHeight = Math.Max(Scale(18), MeasureText(dc, _fontHeadline, view.Headline, contentWidth, true));
        var dotTop = y + ((headlineHeight - dot) / 2);
        FillEllipse(dc, padX, dotTop, padX + dot, dotTop + dot, ToneColor(view.Tone));

        var textLeft = padX + dot + Scale(DotGapDp);
        var waveWidth = view.WaveformActive ? Scale(WaveBarDp * 3 + WaveGapDp * 2) + Scale(8) : 0;

        DrawLabel(dc, _fontHeadline, view.Headline, ColorText1,
            textLeft, y, width - padX - waveWidth, y + headlineHeight, DT_SINGLELINE | DT_END_ELLIPSIS);

        if (view.WaveformActive)
        {
            PaintWaveform(dc, width - padX - Scale(WaveBarDp * 3 + WaveGapDp * 2), y, headlineHeight);
        }

        y += headlineHeight;

        if (!string.IsNullOrEmpty(view.Detail))
        {
            y += Scale(DetailTopDp);
            var detailHeight = MeasureText(dc, _fontDetail, view.Detail, contentWidth, false);
            DrawLabel(dc, _fontDetail, view.Detail, ColorText2,
                padX, y, width - padX, y + detailHeight, DT_WORDBREAK);
            y += detailHeight;
        }

        if (!string.IsNullOrEmpty(view.BodyText))
        {
            y += Scale(BodyTopDp);
            var inner = contentWidth - (Scale(BodyPadXDp) * 2);
            var textHeight = Math.Min(
                MeasureText(dc, _fontBody, view.BodyText!, inner, false),
                Scale(BodySizeDp * 3 + 8));
            var boxHeight = textHeight + (Scale(BodyPadYDp) * 2);

            FillRounded(dc, padX, y, width - padX, y + boxHeight, ColorFill, ColorStroke);

            // Provisional text is dimmed so a sentence that is still changing never reads as final.
            DrawLabel(dc, _fontBody, view.BodyText!,
                view.BodyIsProvisional ? ColorText3 : ColorText1,
                padX + Scale(BodyPadXDp), y + Scale(BodyPadYDp),
                width - padX - Scale(BodyPadXDp), y + boxHeight - Scale(BodyPadYDp),
                DT_WORDBREAK | DT_END_ELLIPSIS);

            y += boxHeight;
        }

        if (view.Action != PanelAction.None && view.ActionLabel is { } label)
        {
            y += Scale(ButtonTopDp);
            var buttonHeight = Scale(ButtonHeightDp);
            var labelWidth = MeasureWidth(dc, _fontButton, label);
            var buttonWidth = labelWidth + (Scale(ButtonPadXDp) * 2);

            _buttonRect = new RECT
            {
                left = padX,
                top = y,
                right = padX + buttonWidth,
                bottom = y + buttonHeight,
            };

            FillRounded(dc, _buttonRect.left, _buttonRect.top, _buttonRect.right, _buttonRect.bottom,
                ColorAccent, ColorAccent);
            DrawLabel(dc, _fontButton, label, ColorText1,
                _buttonRect.left, _buttonRect.top, _buttonRect.right, _buttonRect.bottom,
                DT_SINGLELINE | DT_CENTER | DT_VCENTER);
        }
        else
        {
            _buttonRect = default;
        }
    }

    /// <summary>
    /// Three bars, restrained on purpose: it has to read as "hearing you" from the corner of the
    /// eye without pulling the eye to it.
    /// </summary>
    private void PaintWaveform(nint dc, int left, int top, int rowHeight)
    {
        var barWidth = Scale(WaveBarDp);
        var gap = Scale(WaveGapDp);
        var max = Scale(WaveMaxDp);
        var colour = ToneColor(PanelTone.Listening);

        // Fixed phase offsets rather than randomness, so the motion is even and reproducible.
        ReadOnlySpan<double> phase = stackalloc double[] { 0.0, 0.45, 0.8 };

        for (var i = 0; i < 3; i++)
        {
            var t = (_waveFrame * 0.18) + phase[i];
            var amplitude = 0.45 + (0.55 * (0.5 + (0.5 * Math.Sin(t * Math.PI * 2))));
            var barHeight = Math.Max(Scale(2), (int)(max * amplitude));
            var x = left + (i * (barWidth + gap));
            var yTop = top + ((rowHeight - barHeight) / 2);

            FillSolid(dc, new RECT { left = x, top = yTop, right = x + barWidth, bottom = yTop + barHeight }, colour);
        }
    }

    private static int MeasureWidth(nint dc, nint font, string text)
    {
        var previous = SelectObject(dc, font);
        var rect = new RECT { left = 0, top = 0, right = 4000, bottom = 0 };
        DrawTextW(dc, text, -1, ref rect, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
        SelectObject(dc, previous);
        return rect.right - rect.left;
    }

    private static void DrawLabel(
        nint dc, nint font, string text, uint color, int left, int top, int right, int bottom, uint format)
    {
        var previous = SelectObject(dc, font);
        SetTextColor(dc, color);
        var rect = new RECT { left = left, top = top, right = right, bottom = bottom };
        DrawTextW(dc, text, -1, ref rect, format | DT_NOPREFIX);
        SelectObject(dc, previous);
    }

    private static void FillSolid(nint dc, RECT rect, uint color)
    {
        var brush = CreateSolidBrush(color);
        FillRect(dc, ref rect, brush);
        DeleteObject(brush);
    }

    private void FillRounded(nint dc, int left, int top, int right, int bottom, uint fill, uint stroke)
    {
        var radius = Scale(RadiusDp) * 2;
        var brush = CreateSolidBrush(fill);
        var pen = CreatePen(PS_SOLID, 1, stroke);
        var oldBrush = SelectObject(dc, brush);
        var oldPen = SelectObject(dc, pen);

        RoundRect(dc, left, top, right, bottom, radius, radius);

        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
        DeleteObject(brush);
        DeleteObject(pen);
    }

    private static void FillEllipse(nint dc, int left, int top, int right, int bottom, uint color)
    {
        var brush = CreateSolidBrush(color);
        var oldBrush = SelectObject(dc, brush);
        var oldPen = SelectObject(dc, GetStockObject(NULL_PEN));

        Ellipse(dc, left, top, right + 1, bottom + 1);

        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
        DeleteObject(brush);
    }

    /// <summary>Tone colour. Never the only signal — the headline beside it always says the same thing.</summary>
    private static uint ToneColor(PanelTone tone) => tone switch
    {
        PanelTone.Listening => Rgb(0xFF, 0x99, 0xA4),
        PanelTone.Working => Rgb(0x4C, 0xA6, 0xFF),
        PanelTone.Success => Rgb(0x6C, 0xCB, 0x5F),
        PanelTone.Caution => Rgb(0xFC, 0xE1, 0x00),
        PanelTone.Error => Rgb(0xFF, 0x99, 0xA4),
        _ => ColorText3,
    };

    private static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    public void Dispose()
    {
        if (_hwnd != 0) PostMessageW(_hwnd, WM_QUIT, 0, 0);
    }
}
