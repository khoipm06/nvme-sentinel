#!/usr/bin/env bash
# NVMe Sentinel - Linux Native .deb Package Builder
# Generates a standard Debian / Ubuntu .deb package

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

VERSION="${1:-1.4.0}"
ARCH="amd64"
RID="linux-x64"

SRC_DIR="$ROOT_DIR/publish/$RID"
if [ ! -d "$SRC_DIR" ]; then
    SRC_DIR="/tmp/pub-sentinel-${RID}-standalone/nvme-sentinel-v${VERSION}-${RID}-standalone"
fi

if [ ! -d "$SRC_DIR" ]; then
    echo "Publishing standalone linux-x64 binaries..."
    SRC_DIR="/tmp/deb-build-binaries"
    rm -rf "$SRC_DIR"
    mkdir -p "$SRC_DIR"
    dotnet publish "$ROOT_DIR/src/NvmeSentinel.Daemon" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true -o "$SRC_DIR"
    dotnet publish "$ROOT_DIR/src/NvmeSentinel.App" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true -o "$SRC_DIR"
fi

STAGE_DIR="/tmp/nvme-sentinel-deb-build"
PKG_DIR="$STAGE_DIR/nvme-sentinel_${VERSION}_${ARCH}"
DIST_DIR="$ROOT_DIR/dist"

rm -rf "$STAGE_DIR"
mkdir -p "$PKG_DIR/DEBIAN"
mkdir -p "$PKG_DIR/usr/bin"
mkdir -p "$PKG_DIR/usr/share/applications"
mkdir -p "$PKG_DIR/usr/share/icons/hicolor/scalable/apps"
mkdir -p "$PKG_DIR/usr/lib/systemd/user"
mkdir -p "$DIST_DIR"

# 1. Install binaries
cp -a "$SRC_DIR/nvme-sentinel" "$PKG_DIR/usr/bin/"
cp -a "$SRC_DIR/nvme-sentinel-daemon" "$PKG_DIR/usr/bin/"
chmod 755 "$PKG_DIR/usr/bin/nvme-sentinel" "$PKG_DIR/usr/bin/nvme-sentinel-daemon"

# 2. Desktop launcher
cp "$SCRIPT_DIR/nvme-sentinel.desktop" "$PKG_DIR/usr/share/applications/"

# 3. Icons
for size in 16 32 48 64 128 256 512; do
    if [ -f "$ROOT_DIR/assets/icons/nvme-sentinel-${size}.png" ]; then
        mkdir -p "$PKG_DIR/usr/share/icons/hicolor/${size}x${size}/apps"
        cp "$ROOT_DIR/assets/icons/nvme-sentinel-${size}.png" "$PKG_DIR/usr/share/icons/hicolor/${size}x${size}/apps/nvme-sentinel.png"
    fi
done
if [ -f "$ROOT_DIR/assets/nvme-sentinel.svg" ]; then
    cp "$ROOT_DIR/assets/nvme-sentinel.svg" "$PKG_DIR/usr/share/icons/hicolor/scalable/apps/"
fi

# 4. Systemd user service
sed 's|%h/\.local/share/nvme-sentinel/nvme-sentinel-daemon|/usr/bin/nvme-sentinel-daemon|g' \
    "$SCRIPT_DIR/nvme-sentinel.service" > "$PKG_DIR/usr/lib/systemd/user/nvme-sentinel.service"

# 5. DEBIAN/control
cat << EOF > "$PKG_DIR/DEBIAN/control"
Package: nvme-sentinel
Version: ${VERSION}
Section: utils
Priority: optional
Architecture: amd64
Maintainer: Khoi Pham <khoipm06@github.com>
Description: High-precision NVMe SSD health & wear monitoring suite
 Real-time hardware telemetry, per-process write attribution, and lifespan
 forecasting for modern NVMe solid-state storage.
EOF

# 6. DEBIAN/postinst
cat << 'EOF' > "$PKG_DIR/DEBIAN/postinst"
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database /usr/share/applications || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q /usr/share/icons/hicolor || true
fi
exit 0
EOF
chmod 755 "$PKG_DIR/DEBIAN/postinst"

# 7. DEBIAN/prerm
cat << 'EOF' > "$PKG_DIR/DEBIAN/prerm"
#!/bin/sh
set -e
# Kill daemon processes if running before removal
pkill -f nvme-sentinel-daemon || true
exit 0
EOF
chmod 755 "$PKG_DIR/DEBIAN/prerm"

# 8. Build package
if command -v dpkg-deb >/dev/null 2>&1; then
    dpkg-deb --build --root-owner-group "$PKG_DIR" "$DIST_DIR/nvme-sentinel_${VERSION}_${ARCH}.deb"
    echo "✔ Successfully generated $DIST_DIR/nvme-sentinel_${VERSION}_${ARCH}.deb"
else
    echo "Notice: dpkg-deb not found on current host. Package tree prepared at $PKG_DIR"
fi

rm -rf "$STAGE_DIR"
