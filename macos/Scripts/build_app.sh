#!/bin/bash
# Builds NotiLink.app — the macOS equivalent of windows/publish.ps1. Unlike the Windows build,
# there's no Xcode project here (see Package.swift's header note on why); this script does by
# hand what Xcode's own app-bundle packaging step would otherwise do: compile, then assemble
# Contents/{MacOS,Resources}, stamp Info.plist, copy in the shared wwwroot/ UI, and ad-hoc sign.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MACOS_DIR="$(dirname "$SCRIPT_DIR")"
REPO_ROOT="$(dirname "$MACOS_DIR")"

APP_NAME="NotiLink"
BUILD_CONFIG="release"
DIST_DIR="$MACOS_DIR/dist"
APP_BUNDLE="$DIST_DIR/$APP_NAME.app"
WWWROOT_SRC="$REPO_ROOT/windows/NotiLink.App/wwwroot"
SCRCPY_SRC="$MACOS_DIR/Sources/NotiLinkMac/tools/scrcpy"

if [ ! -x "$SCRCPY_SRC/scrcpy" ] || [ ! -x "$SCRCPY_SRC/adb" ]; then
    echo "==> scrcpy not vendored yet, fetching"
    "$SCRIPT_DIR/../tools/fetch-scrcpy.sh"
fi

echo "==> swift build -c $BUILD_CONFIG"
(cd "$MACOS_DIR" && swift build -c "$BUILD_CONFIG" --product NotiLinkMac)

BINARY_PATH="$MACOS_DIR/.build/$BUILD_CONFIG/NotiLinkMac"
if [ ! -f "$BINARY_PATH" ]; then
    echo "error: built binary not found at $BINARY_PATH" >&2
    exit 1
fi

echo "==> assembling $APP_BUNDLE"
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"

cp "$BINARY_PATH" "$APP_BUNDLE/Contents/MacOS/NotiLinkMac"
cp "$MACOS_DIR/Resources/Info.plist" "$APP_BUNDLE/Contents/Info.plist"
cp "$MACOS_DIR/Resources/AppIcon.icns" "$APP_BUNDLE/Contents/Resources/AppIcon.icns"

if [ ! -d "$WWWROOT_SRC" ]; then
    echo "error: wwwroot not found at $WWWROOT_SRC" >&2
    exit 1
fi
cp -R "$WWWROOT_SRC" "$APP_BUNDLE/Contents/Resources/wwwroot"

mkdir -p "$APP_BUNDLE/Contents/Resources/tools"
cp -R "$SCRCPY_SRC" "$APP_BUNDLE/Contents/Resources/tools/scrcpy"

echo "==> ad-hoc codesign"
codesign --force --deep --sign - "$APP_BUNDLE"

echo "==> done: $APP_BUNDLE"
echo "    First launch: macOS will ask you to allow Local Network access — accept it,"
echo "    otherwise phones on the LAN can't reach the TCP server or UDP discovery."
echo "    Open it with: open \"$APP_BUNDLE\""
