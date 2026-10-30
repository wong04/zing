#!/bin/bash
# Regenerates Resources/AppIcon.icns from the logo's vector paths.
set -euo pipefail
cd "$(dirname "$0")/.."
BUILD="$(mktemp -d)"; trap 'rm -rf "$BUILD"' EXIT
# Top-level code is only allowed in a file called main.swift.
cp Tools/make-icon.swift "$BUILD/main.swift"
swiftc -o "$BUILD/make-icon" "$BUILD/main.swift" Sources/Krackn/App/KracknLogo.swift
rm -rf Resources/AppIcon.iconset
"$BUILD/make-icon" Resources/AppIcon.iconset
iconutil -c icns Resources/AppIcon.iconset -o Resources/AppIcon.icns
echo "wrote Resources/AppIcon.icns"
