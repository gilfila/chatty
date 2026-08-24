#!/bin/bash
# Run the test suite.
#
# Why this wrapper exists: this machine has Command Line Tools but no Xcode.app, so SwiftPM does
# not know where swift-testing lives. Testing.framework and lib_TestingInterop.dylib ship with the
# CLT but sit outside the default search and runtime paths, so a bare `swift test` fails first at
# compile ("no such module 'Testing'") and then at dlopen. These four flags fix both.
#
# Use this instead of `swift test`. If you add XCTest-based tests they will NOT work — XCTest.framework
# is Xcode-only and is genuinely absent here. Stick to swift-testing (`import Testing`).
set -euo pipefail

CLT_FRAMEWORKS="/Library/Developer/CommandLineTools/Library/Developer/Frameworks"
CLT_LIB="/Library/Developer/CommandLineTools/Library/Developer/usr/lib"

if [[ ! -d "$CLT_FRAMEWORKS/Testing.framework" ]]; then
  echo "error: Testing.framework not found at $CLT_FRAMEWORKS" >&2
  echo "hint: install/repair Command Line Tools with 'xcode-select --install'" >&2
  exit 1
fi

cd "$(dirname "$0")/.."
exec swift test \
  -Xswiftc -F -Xswiftc "$CLT_FRAMEWORKS" \
  -Xlinker -F -Xlinker "$CLT_FRAMEWORKS" \
  -Xlinker -rpath -Xlinker "$CLT_FRAMEWORKS" \
  -Xlinker -rpath -Xlinker "$CLT_LIB" \
  "$@"
