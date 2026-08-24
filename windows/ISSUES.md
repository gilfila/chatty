# Flow Windows — issues needing a human decision

Kept by Gil. Open product/policy calls only; bugs go in tests or get fixed.

## Open

### 1. Nothing can be committed or pushed
`REPOS/chatty` has no git `user.name`/`user.email` (blocker open since 2026-08-22) and no
credentials for `github.com/gilfila/chatty`. All Windows work sits uncommitted in the
working tree.
**Needs Tony G:** provide the git identity (and push access) to commit and push.

### 2. UIA text-editable control-type allowlist
Fail-closed classification (locked decision) currently admits UIA control types Edit,
ComboBox, and Document only. Terminals and some editors expose other types and will be
refused until the allowlist is widened. Widening is a per-type judgment (each addition
grows the surface where a password-like field could be misclassified) and needs data from
Pollen's real-Windows pass.
**Decide (after Windows validation):** which additional control types are admitted.

### 4. Windows clipboard fidelity implementation is compile-verified only
`WindowsRawClipboard` now implements the `IRawClipboard` seam that `ClipboardFidelityPolicy`
consumes, replacing the deleted `ClipboardSnapshotService` (whose capture silently skipped
formats it could not carry — the partial-snapshot behaviour the policy layer forbids). It is
deliberately **not wired into the production paste path** while the fidelity gate is open; the
session machine still receives the text-only `ClipboardService`.

It cannot be tested off Windows: it is entirely P/Invoke, and the CF_ENHMETAFILE round trip
(`GetEnhMetaFileBits`/`SetEnhMetaFileBits`) needs real GDI. `ClipboardFidelityPolicy.Classify`
declares CF_ENHMETAFILE preservable *because* the raw implementation serializes it — until this
existed, that was a promise nothing fulfilled.
**Needs Pollen:** prove on Windows that every supported format round-trips (text, RTF/HTML,
CF_DIB/DIBV5 images, CF_HDROP files, and an Office copy carrying EMF alongside DIB/RTF), and
exercise the forced post-empty write failure with the `Restore clipboard` card visible.

### 3. Clipboard fidelity gate is OPEN (QA hold, Jarvis 17:27 UTC)
The candidate path is `ClipboardFidelityPolicy` (Flow.Shell.Core) over the `IRawClipboard`
seam: capture-refusal *before Flow's first clipboard write*, preflight allocation before
`EmptyClipboard`, atomic in-open sequence check, bounded in-open set retries, and
caller-retained snapshots on failure. 14 portable tests cover the policy. The gate stays
open until, on real Windows:
1. all supported formats round-trip byte-identically (text, RTF/HTML, CF_DIB image,
   CF_HDROP file list, EMF via GetEnhMetaFileBits);
2. a forced post-empty `SetClipboardData` failure provably takes the refusal/retry path with
   the "Restore clipboard" action surfaced (snapshot held in memory only — never persisted
   or logged — until restore succeeds or the app exits; a foreign clipboard write supersedes
   recovery per the never-overwrite invariant). Portable half: `ClipboardRestoreRecovery`;
3. `ClipboardSnapshotService` is rewired as the `IRawClipboard` implementation (its current
   capture still skips rather than refuses — must not ship).
The Core migration (`InsertionOrchestrator` adoption, **Tony S**) must not make this path
the default before that evidence exists. Until then the paste path stays on the old
text-restore behavior, which is itself not shippable — so this gate blocks release either
way and is the top Windows-validation priority.

## Decided (locked by Jarvis, 2026-08-24 17:13 UTC, thread `5e1fcb46…`)

1. Panel anchors by the Windows tray/clock (bottom-right on a standard taskbar, follows
   the taskbar edge). Plan amended.
2. Default trigger is right Ctrl, configurable; right Alt rejected (AltGr collision).
   First-run shortcut setup surfaced. *(Implemented: hook default, tray tip, first-run panel
   card, `ShortcutCatalog` allowlist, and `ShortcutSettings` persistence that re-validates a
   stored key on load so an old right-Alt binding cannot survive an upgrade. Change shortcut
   cycles a vetted list rather than capturing a live keystroke — capture is unverifiable off
   Windows and a mis-bind leaves no way back; revisit after Pollen can exercise it.)*
3. UIA field identity + password classification are P0 release gates; unclassifiable
   focused field ⇒ refuse to record, no record-only fallback for unknown fields.
   *(Shell side implemented fail-closed via `UiaFieldInspector`; Core `TargetDescriptor`
   field-identity change is Tony S's.)*
4. `Inserted` renamed to honest `PasteSent` wording — Core contract change, Tony S.
   UIA targeted insertion is later hardening, not P0.
5. Clipboard restore must preserve the full data object, else the paste path is blocked.
   *(Shell mechanics implemented: `ClipboardSnapshotService`, atomic sequence-checked
   restore; orchestrator adoption is the Core side.)*
