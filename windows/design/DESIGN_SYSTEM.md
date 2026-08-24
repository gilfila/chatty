# Flow for Windows — design system

Fluent, Windows 11. This is not the macOS system with different colours: the anchoring, the type
ramp, the corner radii and the status palette are all different, and the one shared thing is the
product promise.

Prototype: `flow-panel-prototype.html`. Implementation: `src/Flow.Windows/TranscriptPanelWindow.cs`
rendering `PanelView` from `src/Flow.Shell.Core/PanelPresenter.cs`.

## Product and job

Flow is a private, on-device Windows dictation utility. Hold a shortcut, speak, see live words
beside the clock, release, and the text lands in the field you were already typing in. The
interface has to make the state obvious at a glance and make every transcript recoverable without
the user learning anything.

## Experience principles

1. One obvious next action at a time. The panel offers at most one button, ever.
2. Trust is visible: on-device, no saved audio, clipboard put back.
3. Recording state is unmistakable but calm.
4. Every error names a recovery, and the words come before the colour.
5. A problem Flow already knows about is said before the user speaks, not after.
6. It feels like part of Windows, not a web app in a window.

## The panel

The whole visible product on Windows is one non-activating flyout.

- **360 dp wide**, height measured from content. Never a fixed height — long detail text must not clip.
- **Anchored to the Flow tray icon**, 12 dp clear of the taskbar and the screen edge. Bottom-right
  by default, because that is where the Windows 11 clock is. Follows the taskbar to the top, left
  or right edge, and lands on the monitor the icon is on.
- **Never takes focus.** `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, `SWP_NOACTIVATE` on every move.
  This is a correctness requirement, not a polish one — if the panel activates it becomes the
  foreground window and replaces the target Flow captured on the press edge.
- **Windows 11 rounded corners** via `DWMWA_WINDOW_CORNER_PREFERENCE`. 4 dp radius on inner
  controls.
- **Double-buffered.** Live partials repaint at speech rate.

### Anatomy, top to bottom

| Element | Spec |
|---|---|
| State dot | 8 dp, vertically centred on the headline. Never the only signal. |
| Headline | 13 dp Segoe UI Variable Text Semibold, single line, ellipsised. |
| Waveform | 3 bars, 3 dp wide, 13 dp max, right-aligned on the headline row. Only while listening. |
| Detail | 12 dp regular, wraps, secondary colour. |
| Body | 13 dp regular in a 4 dp filled box. Provisional text is dimmed. Max 3 lines. |
| Action | 30 dp tall accent button, 12 dp horizontal padding. At most one. |

## Type

Segoe UI Variable Text, falling back to Segoe UI. Never a web font, never a macOS font.
Sizes: 13 semibold (headline), 13 regular (body), 12 regular (detail), 12 semibold (button).

All sizes are design pixels at 96 DPI and are scaled by the per-monitor DPI. The manifest declares
`PerMonitorV2`, so Windows does no scaling for us.

## Colour

Windows 11 default accent `#0078D4` for the single action. Status colours carry meaning and are
always paired with words.

| Tone | Dark | Meaning |
|---|---|---|
| Idle | `#8A8A8A` | Nothing at stake |
| Listening | `#FF99A4` | Hearing you |
| Working | `#4CA6FF` | Flow is busy |
| Success | `#6CCB5F` | Text landed |
| Caution | `#FCE100` | Saved, not typed |
| Error | `#FF99A4` | Setup or failure |

Surfaces: background `#2C2C2C`, inner fill `#363636`, stroke `#3C3C3C`. Text: `#FFFFFF`,
`#C8C8C8` secondary, `#8A8A8A` tertiary and provisional.

No gradients, no neon, no glass beyond the system's own, no decorative imagery, no marketing type.

## Motion

- Panel entrance: 250 ms, decelerating, 8 dp rise. It rises toward attention rather than dropping on it.
- Waveform: 90 ms frame interval, fixed phase offsets — even and reproducible, never random.
- Success lingers 1.6 s, cancellation 1.2 s, then fades.
- **Anything holding recoverable text never auto-dismisses.** This is enforced in the presenter and
  asserted across every reachable state.

## Accessibility

- Never rely on colour alone. Every tone has a headline that says the same thing.
- Provisional text is dimmed *and* italic in the prototype, dimmed in GDI — two signals, not one.
- Respect the system dark/light app theme.
- The panel is glanceable, not interactive beyond one button; the tray menu carries the full
  recovery surface for keyboard and screen-reader users.

## Voice

Plain, short, never blames the user, always says where the text went.

- "Focus moved to a different field, so Flow did not type there. Use Copy last."
- "That app runs as administrator, so Windows blocks typing into it. Use Copy last."
- "Flow does not listen while a password field is focused."

Not: "Injection failed", "Error 0x80070005", "Are you sure?".

## Rules

Use only the fonts, colours, spacing and components defined here. Do not introduce a font, colour,
gradient or control that is not part of this Fluent system, and do not port a macOS idiom across —
traffic lights, SF Symbols, menu-bar anchoring and system indigo all belong to the other app.
