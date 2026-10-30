#!/bin/bash
# Builds Krackn and wraps the binary in a real .app bundle.
#
# SwiftPM only produces a bare executable. A bare executable launched from
# Finder has no bundle identifier, gets no dock icon, and can't become the
# active application — so the .app wrapper isn't cosmetic, it's required for
# the thing to behave like a Mac app at all.

set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="Krackn"
BUNDLE_ID="com.krackn.app"
VERSION="1.1"
BUILD="2"
CONFIG="${1:-release}"
# /Applications, not ~/Applications: macOS only offers an app as a default
# browser candidate when it's installed somewhere Launch Services scans, and a
# per-user Applications folder makes the app invisible to other accounts.
DEST="${2:-/Applications}"

echo "==> Building ($CONFIG)…"
swift build -c "$CONFIG"

BINARY="$(swift build -c "$CONFIG" --show-bin-path)/Krackn"
if [[ ! -f "$BINARY" ]]; then
    echo "Build did not produce a binary at $BINARY" >&2
    exit 1
fi

APP="$DEST/$APP_NAME.app"
echo "==> Assembling $APP"

# /Applications is root-owned on a default install. Fall back rather than
# failing outright, but say so, because the fallback location is exactly the
# one that stops the app being offered as a default browser.
if [[ ! -w "$DEST" ]]; then
    if ! mkdir -p "$DEST" 2>/dev/null || [[ ! -w "$DEST" ]]; then
        echo "    $DEST isn't writable."
        echo "    Re-run with sudo to install there, or pass a different destination:"
        echo "        sudo ./bundle.sh release /Applications"
        DEST="$HOME/Applications"
        APP="$DEST/$APP_NAME.app"
        echo "    Falling back to $APP (won't be offered as a default browser)."
        mkdir -p "$DEST"
    fi
fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp "$BINARY" "$APP/Contents/MacOS/$APP_NAME"
chmod +x "$APP/Contents/MacOS/$APP_NAME"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleExecutable</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$BUILD</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSSupportsAutomaticTermination</key>
    <true/>
    <key>ATSApplicationFontsPath</key>
    <string>Fonts</string>

    <!-- Camera and microphone. Without these two strings macOS kills the app
         the instant it asks for either device, so getUserMedia() failed with
         no prompt and no error the user could act on. -->
    <key>NSCameraUsageDescription</key>
    <string>Krackn needs camera access so sites you open in its browser can use it for video calls and photo capture. Each site still has to ask you separately.</string>
    <key>NSMicrophoneUsageDescription</key>
    <string>Krackn needs microphone access so sites you open in its browser can use it for calls, voice input and recording. Each site still has to ask you separately.</string>

    <!-- Registers Krackn as a browser, so it appears in System Settings ›
         Desktop & Dock › Default web browser and can be picked. Both the URL
         scheme handlers and the HTML document role are required: macOS only
         lists an app as a browser candidate when it claims both. -->
    <key>CFBundleURLTypes</key>
    <array>
        <dict>
            <key>CFBundleURLName</key>
            <string>Web site URL</string>
            <key>CFBundleURLSchemes</key>
            <array>
                <string>http</string>
                <string>https</string>
            </array>
            <key>CFBundleTypeRole</key>
            <string>Viewer</string>
            <key>LSHandlerRank</key>
            <string>Owner</string>
        </dict>
    </array>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key>
            <string>HTML document</string>
            <key>CFBundleTypeRole</key>
            <string>Viewer</string>
            <key>LSHandlerRank</key>
            <string>Alternate</string>
            <key>LSItemContentTypes</key>
            <array>
                <string>public.html</string>
                <string>public.xhtml</string>
            </array>
        </dict>
        <dict>
            <key>CFBundleTypeName</key>
            <string>Web site location</string>
            <key>CFBundleTypeRole</key>
            <string>Viewer</string>
            <key>LSHandlerRank</key>
            <string>Alternate</string>
            <key>LSItemContentTypes</key>
            <array>
                <string>com.apple.web-internet-location</string>
            </array>
        </dict>
    </array>
</dict>
</plist>
PLIST

if [[ -f "Resources/AppIcon.icns" ]]; then
    cp "Resources/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
fi

# Bundle the Inter typeface. ATSApplicationFontsPath (above) makes macOS
# auto-register anything in Contents/Resources/Fonts at launch; the app also
# registers it at runtime as a belt-and-braces fallback.
if [[ -f "Resources/Inter.ttf" ]]; then
    mkdir -p "$APP/Contents/Resources/Fonts"
    cp "Resources/Inter.ttf" "$APP/Contents/Resources/Fonts/Inter.ttf"
    cp "Resources/Inter.ttf" "$APP/Contents/Resources/Inter.ttf"
fi

# Ad-hoc signature. Without this, Gatekeeper on Apple Silicon refuses to launch
# an unsigned binary outright rather than just warning about it.
echo "==> Signing (ad-hoc)…"
codesign --force --deep --sign - "$APP"

# Two copies of the same bundle identifier is a trap, not a convenience.
# This script's default used to be ~/Applications; after it moved to
# /Applications a stale copy stayed behind, the Dock kept launching *that*, and
# an entire debugging session went into "fixes that changed nothing" — the fixes
# were fine, the running app was a build from weeks earlier. Never leave two.
for OTHER in "$HOME/Applications/$APP_NAME.app" "/Applications/$APP_NAME.app"; do
    [[ "$OTHER" == "$APP" ]] && continue
    if [[ -d "$OTHER" ]]; then
        echo
        echo "!! A second $APP_NAME.app exists at:"
        echo "     $OTHER"
        OTHER_VERSION="$(defaults read "$OTHER/Contents/Info.plist" \
            CFBundleShortVersionString 2>/dev/null || echo "unknown")"
        echo "   version $OTHER_VERSION — same bundle id, so the Dock and Launch"
        echo "   Services may open it instead of the build you just made."
        echo "   Remove it with:"
        echo "     rm -rf \"$OTHER\""
        echo
    fi
done

# Make sure Launch Services resolves the freshly installed copy rather than a
# remembered path.
"/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister" \
    -f "$APP" >/dev/null 2>&1 || true

echo "==> Installed version: $VERSION (build $BUILD)"

echo
echo "Done: $APP"
echo "Open it with:  open \"$APP\""
