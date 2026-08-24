#!/bin/bash
# Build FlowApp and assemble + sign dist/Flow.app.
#
# Why a script instead of Xcode: no Xcode.app on this machine, so the bundle is assembled by
# hand from the SwiftPM release binary. TCC keys grants to (bundle identifier, designated
# requirement of the signature), so everything here must stay deterministic across rebuilds:
# same Info.plist identifier, same signing identity. The binary's cdhash changing per build is
# fine and expected — the designated requirement is what TCC matches.
#
# Identity: "Flow Local Development Signing" — self-signed cert in the login keychain, approved
# by Tony G 2026-08-22. `security find-identity` reports it as invalid because it is not
# Apple-anchored; direct `codesign --sign` works and is the functional check.
set -euo pipefail

cd "$(dirname "$0")/.."

IDENTITY="Flow Local Development Signing"
DIST="dist"
APP="$DIST/Flow.app"

swift build -c release --product FlowApp

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp Resources/FlowApp-Info.plist "$APP/Contents/Info.plist"
cp "$(swift build -c release --product FlowApp --show-bin-path)/FlowApp" "$APP/Contents/MacOS/FlowApp"

codesign --force --sign "$IDENTITY" "$APP"
codesign --verify --strict --verbose=2 "$APP"

echo "--- signature ---"
codesign --display --verbose=2 "$APP" 2>&1 | grep -E '^(Identifier|CDHash|Authority|Signature)'
echo "--- designated requirement (what TCC keys grants to) ---"
codesign --display -r- "$APP" 2>&1 | grep '^designated'
