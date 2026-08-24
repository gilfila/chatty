using Flow.Core.Abstractions;
using Flow.Shell.Core;
using Flow.Windows.Interop;
using static Flow.Windows.Interop.NativeMethods;

namespace Flow.Windows;

/// <summary>
/// Global hold-to-talk listener built on a low-level keyboard hook.
///
/// Design constraints:
/// - The hook callback must return fast or Windows silently removes the hook, so it
///   only enqueues edges; all downstream work happens on a consumer thread.
/// - Every event is passed through via CallNextHookEx — Flow never blocks or swallows
///   ordinary keystrokes. The trigger is a bare modifier, so passing it through types
///   nothing.
/// - Injected events (LLKHF_INJECTED) are ignored so Flow's own SendInput paste, and
///   any other synthetic input, can never start or stop a session.
/// </summary>
public sealed class KeyboardHookListener : IDisposable
{
    private volatile uint _triggerVk;
    private readonly Queue<Edge> _queue = new();
    private readonly object _queueGate = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _cts = new();

    private Thread? _hookThread;
    private Thread? _consumerThread;
    private nint _hook;
    private uint _hookThreadId;
    private LowLevelKeyboardProc? _proc; // rooted so the GC can't collect the delegate behind the native hook
    private volatile bool _triggerDown;

    private enum Edge { Pressed, Released, EscapePressed }

    public event Action? TriggerPressed;
    public event Action? TriggerReleased;
    /// <summary>
    /// The held press was abandoned. Carries why, so the panel can say "Windows locked" rather
    /// than a generic cancel. Supersedes a bare Escape event: a lost key-up must cancel too.
    /// </summary>
    public event Action<CancelReason>? TriggerCancelled;
    /// <summary>Raised when the hook could not be installed or has failed; the session
    /// machine must treat this as a cancel condition.</summary>
    public event Action<string>? ListenerFailed;

    // Default is right Ctrl (0xA3): right Alt is AltGr on many international layouts and
    // would start a session on every accented character. Locked product decision.
    public KeyboardHookListener(ushort triggerVk = ShortcutCatalog.Default) => _triggerVk = triggerVk;

    /// <summary>
    /// Change the hold-to-talk key. Refuses anything <see cref="ShortcutCatalog"/> disallows, so a
    /// stored or mistyped value cannot leave the user holding a key that types into their document.
    /// </summary>
    public bool SetTrigger(ushort virtualKey)
    {
        if (!ShortcutCatalog.Validate(virtualKey).IsAllowed) return false;

        _triggerVk = virtualKey;
        _triggerDown = false; // any press in flight belonged to the old key
        return true;
    }

    private readonly ManualResetEventSlim _installAttempted = new();
    private volatile bool _hookInstalled;

    /// <summary>True once the hook is installed and delivering edges.</summary>
    public bool IsInstalled => _hookInstalled;

    public void Start()
    {
        _consumerThread = new Thread(ConsumeLoop) { IsBackground = true, Name = "flow-key-consumer" };
        _consumerThread.Start();

        StartHookThread();
    }

    /// <summary>
    /// Tear the hook down and install it again, reporting whether it came back.
    /// </summary>
    /// <remarks>
    /// Backs the panel's "Try again" action. Windows evicts a low-level hook whose callback was too
    /// slow, and there is no notification when it does — the edges simply stop. Reinstalling is the
    /// only recovery, and it has to report honestly, because a shortcut that looks installed and
    /// never fires is the worst failure this app has.
    /// </remarks>
    public bool Restart()
    {
        if (_hookThreadId != 0)
        {
            PostThreadMessageW(_hookThreadId, WM_QUIT, 0, 0);
            _hookThread?.Join(TimeSpan.FromSeconds(2));
        }

        _hookThreadId = 0;
        _hookInstalled = false;
        _installAttempted.Reset();
        _triggerDown = false;

        StartHookThread();

        return _installAttempted.Wait(TimeSpan.FromSeconds(2)) && _hookInstalled;
    }

    private void StartHookThread()
    {
        _hookThread = new Thread(HookLoop) { IsBackground = true, Name = "flow-key-hook" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
    }

    private void HookLoop()
    {
        _hookThreadId = GetCurrentThreadId();
        _proc = HookCallback;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        if (_hook == 0)
        {
            _hookInstalled = false;
            _installAttempted.Set();
            ListenerFailed?.Invoke("SetWindowsHookEx failed — global shortcut unavailable");
            return;
        }

        _hookInstalled = true;
        _installAttempted.Set();

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        UnhookWindowsHookEx(_hook);
        _hook = 0;
        _hookInstalled = false;
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLKHF_INJECTED) == 0)
            {
                var msg = (uint)wParam;
                var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                var up = msg is WM_KEYUP or WM_SYSKEYUP;

                if (info.vkCode == _triggerVk)
                {
                    if (down && !_triggerDown)
                    {
                        _triggerDown = true;
                        Enqueue(Edge.Pressed);
                    }
                    else if (up && _triggerDown)
                    {
                        _triggerDown = false;
                        Enqueue(Edge.Released);
                    }
                }
                else if (info.vkCode == VK_ESCAPE && down && _triggerDown)
                {
                    Enqueue(Edge.EscapePressed);
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Enqueue(Edge edge)
    {
        lock (_queueGate) _queue.Enqueue(edge);
        _available.Release();
    }

    /// <summary>
    /// How often the consumer wakes with no edge to process, so the hold watchdog can advance.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    private void ConsumeLoop()
    {
        // Raw hook edges go through HoldToTalkGate rather than straight to the events. The gate is
        // what turns "the key-up never arrived" into a cancel instead of a session that sits in
        // Recording with the microphone open until the user notices.
        var gate = new HoldToTalkGate();
        var since = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var gotEdge = _available.Wait((int)TickInterval.TotalMilliseconds, _cts.Token);
                var elapsed = since.Elapsed;
                since.Restart();

                TriggerEdge? normalized;

                if (gotEdge)
                {
                    Edge edge;
                    lock (_queueGate) edge = _queue.Dequeue();

                    normalized = edge switch
                    {
                        Edge.Pressed => gate.Edge(RawKeyEdge.Down),
                        Edge.Released => gate.Edge(RawKeyEdge.Up),
                        Edge.EscapePressed => gate.Abort(CancelReason.UserCancelled),
                        _ => null,
                    };
                }
                else
                {
                    // No edge arrived. If one has been outstanding too long, the up edge was lost —
                    // a workstation lock, the secure desktop, or an evicted hook.
                    normalized = gate.Tick(elapsed);
                }

                if (normalized is not { } publish) continue;

                try
                {
                    switch (publish)
                    {
                        case TriggerEdge.Pressed:
                            TriggerPressed?.Invoke();
                            break;
                        case TriggerEdge.Released:
                            TriggerReleased?.Invoke();
                            break;
                        case TriggerEdge.Cancelled:
                            TriggerCancelled?.Invoke(gate.LastCancelReason ?? CancelReason.UserCancelled);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    ListenerFailed?.Invoke($"key handler failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_hookThreadId != 0)
            PostThreadMessageW(_hookThreadId, WM_QUIT, 0, 0);
    }
}
