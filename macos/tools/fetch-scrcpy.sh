#!/bin/bash
# Downloads the official scrcpy macOS release, verifies its SHA-256 against a pinned hash, and
# extracts it to Sources/NotiLinkMac/tools/scrcpy/ — the macOS equivalent of
# windows/tools/fetch-scrcpy.ps1. Run once after a fresh clone, or whenever SCRCPY_VERSION below
# is bumped.
#
# Confirmed by hand (not guessed) against the real v5.0.1 release before writing this pin:
# the macOS tarball bundles `scrcpy`, `scrcpy-server`, AND a universal (arm64+x86_64) `adb` —
# fully self-contained, same as the Windows zip, just statically linked (SDL3.4.18/libavcodec
# 63.1.102) instead of shipping separate DLLs — so no separate adb/platform-tools fetch is
# needed here, unlike what was originally assumed before checking.
set -euo pipefail

SCRCPY_VERSION="5.0.1"

# Pinned from the release's own SHA256SUMS.txt — verified by hand against a real download.
SHA256_AARCH64="33611e51977a8289e2e124b220f727895181ef2eb9060e0987a4dc2db9cf0d6b"
SHA256_X86_64="31a5467a9e907f162b093267cbbab1276b15c6e709c2ea8159566a8d165beb50"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MACOS_DIR="$(dirname "$SCRIPT_DIR")"
TARGET_DIR="$MACOS_DIR/Sources/NotiLinkMac/tools/scrcpy"

if [ -x "$TARGET_DIR/scrcpy" ] && [ -x "$TARGET_DIR/adb" ]; then
    echo "scrcpy already present at $TARGET_DIR — skipping (delete that folder to re-fetch)."
    exit 0
fi

case "$(uname -m)" in
    arm64)
        ARCH="aarch64"
        EXPECTED_SHA256="$SHA256_AARCH64"
        ;;
    x86_64)
        ARCH="x86_64"
        EXPECTED_SHA256="$SHA256_X86_64"
        ;;
    *)
        echo "error: unsupported architecture $(uname -m)" >&2
        exit 1
        ;;
esac

ASSET="scrcpy-macos-${ARCH}-v${SCRCPY_VERSION}.tar.gz"
URL="https://github.com/Genymobile/scrcpy/releases/download/v${SCRCPY_VERSION}/${ASSET}"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

echo "==> downloading $URL"
curl -fL -o "$WORK_DIR/$ASSET" "$URL"

echo "==> verifying SHA-256"
ACTUAL_SHA256="$(shasum -a 256 "$WORK_DIR/$ASSET" | awk '{print $1}')"
if [ "$ACTUAL_SHA256" != "$EXPECTED_SHA256" ]; then
    echo "error: SHA-256 mismatch for $ASSET" >&2
    echo "  expected: $EXPECTED_SHA256" >&2
    echo "  actual:   $ACTUAL_SHA256" >&2
    exit 1
fi

echo "==> extracting"
mkdir -p "$WORK_DIR/extracted"
tar -xzf "$WORK_DIR/$ASSET" -C "$WORK_DIR/extracted"

EXTRACTED_SUBDIR="$WORK_DIR/extracted/scrcpy-macos-${ARCH}-v${SCRCPY_VERSION}"
mkdir -p "$TARGET_DIR"
cp "$EXTRACTED_SUBDIR/scrcpy" "$EXTRACTED_SUBDIR/scrcpy-server" "$EXTRACTED_SUBDIR/adb" "$TARGET_DIR/"
chmod +x "$TARGET_DIR/scrcpy" "$TARGET_DIR/adb"

# Defensive no-op in practice (curl doesn't set com.apple.quarantine the way Safari/Finder
# downloads do), but harmless and cheap insurance since we've already verified the checksum —
# the same reasoning Homebrew uses for its own downloaded bottles.
xattr -d com.apple.quarantine "$TARGET_DIR/scrcpy" "$TARGET_DIR/adb" 2>/dev/null || true

echo "==> done: $TARGET_DIR"
"$TARGET_DIR/scrcpy" --version | head -1
"$TARGET_DIR/adb" --version | head -1
