# Flow

Flow is an open-source, privacy-first dictation app inspired by Wispr Flow. Hold a
shortcut, speak, and Flow turns the result into text for the field you already
have selected. It keeps microphone audio on-device and does not save recordings.

This repository contains separate native clients rather than a cross-platform
wrapper:

| Platform | Location | Status |
| --- | --- | --- |
| macOS | [`mac/`](mac/) | Working local-development build; 43 tests in 12 suites pass. |
| Windows | [`windows/`](windows/) | Prototype under active validation; portable tests pass, but a real Windows release is not ready yet. |

## Install from source

There is no downloadable public release package in this repository yet. The
macOS client can be assembled locally; the Windows client must remain in its
validation phase until its Windows safety checks have been observed on a real
machine.

### macOS (local-development install)

Requirements: **macOS 26**, the Xcode Command Line Tools, and a local code
signing identity named `Flow Local Development Signing`. The signing identity
is deliberately not included in the repository. If you do not have one, you
can still build and run the development binary with `swift run FlowApp`, but
macOS privacy permissions may not remain stable across rebuilds.

```bash
git clone https://github.com/gilfila/chatty.git
cd chatty/mac
xcode-select --install            # only if the Command Line Tools are missing
./Scripts/test.sh
./Scripts/bundle.sh
open dist/Flow.app
```

`bundle.sh` creates a local `dist/Flow.app` and verifies its signature. To
keep it in Applications, drag `dist/Flow.app` into **Applications** in Finder,
then open it from there. On first use, allow the requested microphone and
accessibility permissions. Focus a normal text field, hold **right Option**,
speak, and release. Press Escape to cancel.

### Windows

**Do not install the Windows prototype yet.** No tested MSIX installer is
published, and real Windows 11 validation is still outstanding. Developers can
run the portable test suite from source:

```bash
git clone https://github.com/gilfila/chatty.git
cd chatty/windows
dotnet test Flow.Windows.sln
```

When a Windows test build is ready, its MSIX installation and permission steps
will be documented here. Until then, use the macOS build above for hands-on
testing.

## macOS

The macOS client is a native Swift menu-bar app for macOS 26. Hold **right
Option** while speaking; the live transcript panel appears near the menu-bar
clock. Release to insert text into the selected editable field. Escape cancels.

```bash
cd mac
swift build
./Scripts/test.sh
```

See the [macOS README](mac/README.md) for permissions, installation, safety
behaviour, and development notes.

## Windows

The Windows client is a native .NET/Win32 system-tray prototype. It is designed
to use **right Ctrl** as a hold-to-talk shortcut, show live text beside the
Windows tray/clock, and preserve the same safety model as the macOS client.

```bash
cd windows
dotnet test Flow.Windows.sln
```

Its portable test suite is useful engineering evidence, but it is **not a
Windows release certificate**. Packaging, on-device speech, clipboard-format
round trips, password-field detection, and panel behaviour still need observed
Windows 11 validation before this client should be installed by testers.

See the [Windows README](windows/README.md) and
[open validation gates](windows/ISSUES.md) before treating it as ready.

## Safety principles

- No microphone audio is retained.
- Text is saved before Flow attempts to paste, so it can be recovered with
  **Copy Last** when insertion is refused or fails.
- Flow refuses password/protected or elevated targets and refuses a paste when
  focus has changed.
- The Windows prototype remains fail-closed while its platform-specific safety
  checks are awaiting real-machine verification.

## Repository contents

- [`mac/`](mac/) — macOS app source, tests, resources, and build scripts.
- [`windows/`](windows/) — Windows app source, tests, packaging, and UI design.

Generated build folders, local signing material, and app bundles are deliberately
excluded from version control. Build the clients from source using the commands
above.
