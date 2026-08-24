# flow — Wispr Flow clone, macOS MVP

Native Swift menu-bar agent plus a `flow` control CLI. On-device dictation via macOS 26
`SpeechAnalyzer`. Plan: `../../PLANS/WISPR_FLOW_MACOS_MVP_DELIVERY_PLAN.md`.

## Use the installed app — no Terminal required

1. Open Spotlight (`⌘ Space`), type **Flow**, and press Return.
2. Confirm the microphone icon appears in the macOS menu bar.
3. Focus any normal editable field, hold **right Option**, speak, and release to insert.
4. Press Escape while holding to cancel. If insertion is blocked, choose
   **Copy Last Transcript** from the Flow menu-bar menu.

Flow never stores microphone audio. It refuses password fields and will not paste if focus moved
to a different field while you were speaking.

## Build and test

```bash
swift build          # plain build works
./Scripts/test.sh    # USE THIS — not `swift test`
```

`swift test` fails on this machine. There is no Xcode.app, and while swift-testing ships with the
Command Line Tools it sits outside SwiftPM's default search and runtime paths, so a bare run fails
first at compile (`no such module 'Testing'`) and then at `dlopen`. `Scripts/test.sh` passes the
four flags that fix both. **XCTest is genuinely unavailable here** — `XCTest.framework` is
Xcode-only. Write tests with `import Testing`.

## Layout and ownership

| Target | Owner | Contents |
|---|---|---|
| `FlowCore` | **Gil** | Shared boundary types and protocols. Both sides depend on this. |
| `FlowTestSupport` | **Gil** | `FakeAudioFrameSource`, `FakeDictationTrigger` — so M2 needs no real mic. |
| `FlowDictation` | **Tony S** | Speech adapter, formatter, transcript store, injector (M2). |
| `FlowApp` | **Gil** | Menu-bar agent: permissions, hotkey, HUD, and coordinator integration. |
| `FlowCLI` | **Gil** | `flow install\|start\|stop\|status\|diagnose\|open`. |

`FlowApp` depends on `FlowDictation` for the integrated resident loop. `FlowDictation` does **not**
depend on `FlowApp`.
If M2 needs a type that the menu bar also renders, it goes in `FlowCore` — ask Gil rather than
declaring it in `FlowDictation`, otherwise the ownership boundary reverses.

`FlowCoreContract.version` is bumped on any source-breaking change to the published boundary, and
the change is announced in the channel. M1 and M2 build against it in parallel, so a silent change
costs the other side a rebuild loop.

## State machine (P0)

```
                ┌──────────────────────── cancelled ◄─── (Escape, key-up before speech,
                │                              │           app quit, mic interruption)
                ▼                              │
  idle ──► requestingPermissions ──► listening ──► finalizing ──► inserting ──► idle
    ▲               │                    │             │              │
    │               └──────────┬─────────┴─────────────┴──────────────┘
    │                          ▼
    └────────────────── recoverableFailure
```

Invariants, enforced by `DictationState` and the contract tests:

1. `requestingPermissions` is entered only for a grant the **selected API actually needs** — the
   app never speculatively prompts for all three.
2. The transcript is persisted on the `finalizing → inserting` edge, **before** any paste is
   attempted. Every injection failure is therefore recoverable, never data loss.
3. `cancelled` and `recoverableFailure` both return to `idle`. Neither is terminal.
4. Only one session may be non-`idle` at a time. Events tagged with a stale `DictationSessionID`
   are discarded — this is what stops a late result from pasting into a field the user has left.

## Published M0 contracts

- `AudioFrameSource` — mic capture (Gil produces, M2 consumes). The stream is guaranteed to
  finish on stop/interruption/termination so a consumer is never stranded, and to **throw rather
  than yield silence** on failure. `AudioFrame` is `@unchecked Sendable`; the ownership rule that
  makes it safe is that a producer must not retain or mutate a buffer after yielding it.
- `DictationTrigger` — hold-to-talk edges. Auto-repeat cannot emit two `pressed` in a row, and
  `cancelled` always wins over a following `released`. `requiredPermissions` is declared per
  implementation so the app prompts for the minimum real set.
- `PermissionState` — per-grant status with `notRequired` as a first-class case. Note
  `canDictate`: a denied **Accessibility** grant is not fatal (transcript is retained for manual
  paste); a denied microphone or hotkey grant is.
- `DictationSessionDelegate` — `@MainActor` callbacks for state, partials, final, injection
  outcome, failure.
- `Transcript` — `rawText` **and** `formattedText` both retained, so formatting is reversible.
  No audio field: P0 never writes microphone audio to disk.
- `InjectionOutcome` — exhaustive. `needsRecoverySurface` encodes which outcomes must keep the
  transcript reachable; only `inserted` and `secureTarget` end the story.
- `FocusTarget` — snapshot at recording start, `==` re-validated before paste. Inequality means
  return `.targetChanged` and do not paste.

## Status

The integrated resident pipeline and supporting packages pass 43 tests in 12 suites. Local app
bundles are signed with the machine-local **Flow Local Development Signing** identity; this is a
test distribution path for this Mac, not an Apple-notarized public release.
