# Flow for Windows

Native Windows client for Flow. Not a port of the macOS app — that one is macOS 26-only and
depends on AppKit, AVFAudio, Apple `SpeechAnalyzer`, macOS Accessibility and CGEvent. Rationale in
`RESEARCH/FLOW_WINDOWS_FEASIBILITY.md`; the working contract is
`PLANS/FLOW_WINDOWS_MVP_DELIVERY_PLAN.md`.

## Layout and ownership

| Project | Owner | Contents |
|---|---|---|
| `src/Flow.Core` | **Tony S** | Shared boundary types, speech/transcript contracts, target guard, insertion orchestrator, clipboard restore policy. |
| `src/Flow.Shell.Core` | **Gil** | Portable shell logic: hold-to-talk edge normalization, press-edge target admission, panel presentation. |
| `src/Flow.Windows` | **Gil** | Windows-only shell: tray host, panel window, `WH_KEYBOARD_LL` hook, `SendInput`, clipboard, UI Automation. |
| `tests/Flow.Shell.Core.Tests` | **Gil** | 2714 tests, including the recovery end-to-end test. Runs on macOS and Windows. |
| `design/` | **Gil** | Panel design spec and rendered prototype. |
| `packaging/` | **Gil** | MSIX manifest and packaging. |

`Flow.Shell.Core` depends on `Flow.Core`. `Flow.Core` does **not** depend on the shell. Anything
both sides render belongs in `Flow.Core` — ask Tony S rather than declaring it in the shell,
otherwise the ownership boundary reverses.

## Build and test

```bash
dotnet test Flow.Windows.sln
```

Both `Flow.Core` and `Flow.Shell.Core` target plain `net8.0` and build anywhere. The suite runs on
macOS, which is where it is being developed.

## What can and cannot be built off-Windows

Verified on macOS 15 / .NET SDK 8.0.424, not assumed:

| Layer | Off-Windows | Evidence |
|---|---|---|
| `net8.0` portable logic | ✅ builds and tests | `dotnet test Flow.Windows.sln` — 2749 passed |
| `net8.0-windows` P/Invoke interop | ✅ compiles with `<EnableWindowsTargeting>true</EnableWindowsTargeting>` | probe built clean with `SetWindowsHookExW`, `GetClipboardSequenceNumber`, `KBDLLHOOKSTRUCT` |
| WinUI 3 / Windows App SDK | ❌ | `MSB4062` — `Microsoft.Build.Packaging.Pri.Tasks.ExpandPriContent` is missing; the MRT/PRI resource task ships only in the Windows SDK's AppxPackage tooling |
| MSIX packaging, running the app | ❌ | needs Windows |

The useful consequence: the risky Win32 code — keyboard hook lifetime, injected-event filtering,
integrity checks, clipboard sequencing — lives in a `net8.0-windows` library that compile-checks
off-Windows, so only the thin XAML/tray/MSIX shell actually needs a Windows machine.

## Platform notes that shaped the design

- **`RegisterHotKey` cannot do hold-to-talk.** It delivers one `WM_HOTKEY` activation with no
  release edge. The shell runs a `WH_KEYBOARD_LL` hook on a dedicated message-pump thread instead.
  See `HoldToTalkGate` for the three ways a raw hook stream lies to you.
- **The panel anchors bottom-right, not top-right.** The plan says "near the clock", and on
  Windows 11 the clock is bottom-right. It anchors to the Flow tray icon and follows the taskbar.
- **The panel must never take focus.** `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`. If it activates it
  becomes the foreground window, which destroys the very target capture the paste guard depends on.
- **Flow never elevates to paste.** Windows discards injected input sent from medium to high
  integrity. Flow declines, says so at the moment you press, and keeps the text for Copy last.
