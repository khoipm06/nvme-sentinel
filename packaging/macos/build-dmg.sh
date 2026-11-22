#!/usr/bin/env bash
# NVMe Sentinel - macOS .app Bundle and .dmg Builder
# Assembles a standard macOS Application Bundle with drag-to-Applications DMG

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

VERSION="${1:-1.3.2}"
ARCH="${2:-arm64}" # arm64 or x64
RID="osx-${ARCH}"

SRC_DIR="$ROOT_DIR/publish/$RID"
if [ ! -d "$SRC_DIR" ]; then
    echo "Error: Source publish directory not found at $SRC_DIR"
    echo "Run dotnet publish first: dotnet publish src/NvmeSentinel.App -c Release -r $RID --self-contained true -o $SRC_DIR"
    exit 1
fi

STAGE_DIR="/tmp/sentinel-macos-build-$ARCH"
APP_NAME="NVMe Sentinel.app"
APP_DIR="$STAGE_DIR/$APP_NAME"
DMG_NAME="NVMe-Sentinel-v${VERSION}-${RID}.dmg"
DIST_DIR="$ROOT_DIR/dist"

rm -rf "$STAGE_DIR"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources" "$DIST_DIR"

echo "Assembling $APP_NAME..."
# 1. Copy binaries
cp -a "$SRC_DIR"/* "$APP_DIR/Contents/MacOS/"
chmod +x "$APP_DIR/Contents/MacOS/nvme-sentinel"
if [ -f "$APP_DIR/Contents/MacOS/nvme-sentinel-daemon" ]; then
    chmod +x "$APP_DIR/Contents/MacOS/nvme-sentinel-daemon"
fi

# 2. Copy Info.plist and PkgInfo
cp "$SCRIPT_DIR/Info.plist" "$APP_DIR/Contents/Info.plist"
echo -n "APPL????" > "$APP_DIR/Contents/PkgInfo"

# 3. Copy Icon
if [ -f "$ROOT_DIR/assets/icons/nvme-sentinel.icns" ]; then
    cp "$ROOT_DIR/assets/icons/nvme-sentinel.icns" "$APP_DIR/Contents/Resources/AppIcon.icns"
elif [ -f "$ROOT_DIR/assets/icons/nvme-sentinel-512.png" ]; then
    cp "$ROOT_DIR/assets/icons/nvme-sentinel-512.png" "$APP_DIR/Contents/Resources/AppIcon.png"
fi

# 4. Copy LaunchAgent template for background service
cp "$SCRIPT_DIR/com.khoipm.nvme-sentinel.plist" "$APP_DIR/Contents/Resources/"

echo "Creating .dmg image..."
DMG_STAGE="$STAGE_DIR/dmg_content"
mkdir -p "$DMG_STAGE"
cp -R "$APP_DIR" "$DMG_STAGE/"
# Symlink to /Applications so user can drag-and-drop
ln -s /Applications "$DMG_STAGE/Applications"

if command -v hdiutil >/dev/null 2>&1; then
    # Native macOS hdiutil
    hdiutil create -volname "NVMe Sentinel" \
        -srcfolder "$DMG_STAGE" \
        -ov -format UDZO \
        "$DIST_DIR/$DMG_NAME"
    echo "✔ Successfully generated $DIST_DIR/$DMG_NAME"
else
    # On non-macOS Linux build hosts: package as .tar.gz bundle preserving .app structure
    echo "Notice: hdiutil is only available on macOS hosts. Packaging $APP_NAME as .tar.gz..."
    tar -czf "$DIST_DIR/NVMe-Sentinel-v${VERSION}-${RID}.tar.gz" -C "$STAGE_DIR" "$APP_NAME"
    echo "✔ Successfully generated $DIST_DIR/NVMe-Sentinel-v${VERSION}-${RID}.tar.gz"
fi

rm -rf "$STAGE_DIR"
