using static Flow.Windows.Interop.NativeMethods;

namespace Flow.Windows;

/// <summary>
/// Hidden message window that owns the tray icon, its context menu, and system
/// notifications (desktop lock). This is the app's main-thread message loop.
/// </summary>
public sealed class ShellHost : IDisposable
{
    private const uint MSG_TRAY = WM_APP + 10;
    private const nuint CMD_COPY_LAST = 1;
    private const nuint CMD_QUIT = 2;

    /// <summary>Identity of Flow's tray icon, so the panel can anchor itself to it.</summary>
    internal const uint TrayIconId = 1;

    /// <summary>
    /// The window that owns the tray icon. <see cref="TranscriptPanelWindow"/> needs it to ask
    /// Windows where the icon actually is, which is how the panel lands beside the clock on the
    /// right monitor and follows the taskbar if it is moved.
    /// </summary>
    internal static nint TrayOwnerWindow { get; private set; }

    private nint _hwnd;
    private WndProc? _wndProc;
    private bool _trayAdded;

    /// <summary>User picked "Copy last transcript" from the tray menu.</summary>
    public event Action? CopyLastRequested;

    /// <summary>The desktop was locked — any active dictation session must cancel.</summary>
    public event Action? DesktopLocked;

    public event Action? QuitRequested;

    /// <summary>Creates the window and tray icon, then pumps messages until quit.</summary>
    public void Run()
    {
        var hInstance = GetModuleHandleW(null);
        _wndProc = HandleMessage;
        var cls = new WNDCLASSW
        {
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = "FlowShellHost",
        };
        if (RegisterClassW(ref cls) == 0) throw new InvalidOperationException("RegisterClass failed");

        _hwnd = CreateWindowExW(0, "FlowShellHost", "Flow", 0, 0, 0, 0, 0, 0, 0, hInstance, 0);
        if (_hwnd == 0) throw new InvalidOperationException("CreateWindowEx failed");
        TrayOwnerWindow = _hwnd;

        WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION);
        AddTrayIcon(hInstance);

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private void AddTrayIcon(nint hInstance)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = MSG_TRAY,
            hIcon = LoadIconW(0, IDI_APPLICATION),
            szTip = "Flow — hold right Ctrl to dictate",
            szInfo = "",
            szInfoTitle = "",
        };
        _trayAdded = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    private nint HandleMessage(nint hWnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case MSG_TRAY when (uint)lParam is WM_RBUTTONUP or WM_LBUTTONUP:
                ShowMenu(hWnd);
                return 0;

            case WM_WTSSESSION_CHANGE when (int)wParam == WTS_SESSION_LOCK:
                DesktopLocked?.Invoke();
                return 0;

            default:
                return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private void ShowMenu(nint hWnd)
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenuW(menu, MF_STRING, CMD_COPY_LAST, "Copy last transcript");
            AppendMenuW(menu, MF_STRING, CMD_QUIT, "Quit Flow");

            // Required for the menu to dismiss when the user clicks elsewhere.
            SetForegroundWindow(hWnd);
            GetCursorPos(out var pt);
            var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.x, pt.y, 0, hWnd, 0);
            switch ((nuint)cmd)
            {
                case CMD_COPY_LAST: CopyLastRequested?.Invoke(); break;
                case CMD_QUIT:
                    QuitRequested?.Invoke();
                    PostMessageW(hWnd, WM_QUIT, 0, 0);
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_trayAdded)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
                szTip = "",
                szInfo = "",
                szInfoTitle = "",
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
        }
        if (_hwnd != 0)
        {
            WTSUnRegisterSessionNotification(_hwnd);
            DestroyWindow(_hwnd);
        }
    }
}
