#!/usr/bin/env bash
# NVMe Sentinel - Linux Native .rpm Package Builder
# Generates a standard RPM package for Fedora / RHEL / openSUSE

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

VERSION="${1:-1.3.2}"
RELEASE="1"
ARCH="x86_64"
RID="linux-x64"

SRC_DIR="$ROOT_DIR/publish/$RID"
if [ ! -d "$SRC_DIR" ]; then
    SRC_DIR="/tmp/pub-sentinel-${RID}-standalone/nvme-sentinel-v${VERSION}-${RID}-standalone"
fi

if [ ! -d "$SRC_DIR" ]; then
    echo "Publishing standalone linux-x64 binaries..."
    SRC_DIR="/tmp/rpm-build-binaries"
    rm -rf "$SRC_DIR"
    mkdir -p "$SRC_DIR"
    dotnet publish "$ROOT_DIR/src/NvmeSentinel.Daemon" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true -o "$SRC_DIR"
    dotnet publish "$ROOT_DIR/src/NvmeSentinel.App" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true -o "$SRC_DIR"
fi

STAGE_DIR="/tmp/nvme-sentinel-rpm-build"
RPM_TOP="$STAGE_DIR/rpmbuild"
BUILD_ROOT="$RPM_TOP/BUILDROOT/nvme-sentinel-${VERSION}-${RELEASE}.${ARCH}"
DIST_DIR="$ROOT_DIR/dist"

rm -rf "$STAGE_DIR"
mkdir -p "$RPM_TOP"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}
mkdir -p "$BUILD_ROOT/usr/bin"
mkdir -p "$BUILD_ROOT/usr/share/applications"
mkdir -p "$BUILD_ROOT/usr/share/icons/hicolor/scalable/apps"
mkdir -p "$BUILD_ROOT/usr/lib/systemd/user"
mkdir -p "$DIST_DIR"

# 1. Install binaries
cp -a "$SRC_DIR/nvme-sentinel" "$BUILD_ROOT/usr/bin/"
cp -a "$SRC_DIR/nvme-sentinel-daemon" "$BUILD_ROOT/usr/bin/"
chmod 755 "$BUILD_ROOT/usr/bin/nvme-sentinel" "$BUILD_ROOT/usr/bin/nvme-sentinel-daemon"

# 2. Desktop launcher
cp "$SCRIPT_DIR/nvme-sentinel.desktop" "$BUILD_ROOT/usr/share/applications/"

# 3. Icons
for size in 16 32 48 64 128 256 512; do
    if [ -f "$ROOT_DIR/assets/icons/nvme-sentinel-${size}.png" ]; then
        mkdir -p "$BUILD_ROOT/usr/share/icons/hicolor/${size}x${size}/apps"
        cp "$ROOT_DIR/assets/icons/nvme-sentinel-${size}.png" "$BUILD_ROOT/usr/share/icons/hicolor/${size}x${size}/apps/nvme-sentinel.png"
    fi
done
if [ -f "$ROOT_DIR/assets/nvme-sentinel.svg" ]; then
    cp "$ROOT_DIR/assets/nvme-sentinel.svg" "$BUILD_ROOT/usr/share/icons/hicolor/scalable/apps/"
fi

# 4. Systemd user service
sed 's|%h/\.local/share/nvme-sentinel/nvme-sentinel-daemon|/usr/bin/nvme-sentinel-daemon|g' \
    "$SCRIPT_DIR/nvme-sentinel.service" > "$BUILD_ROOT/usr/lib/systemd/user/nvme-sentinel.service"

# 5. RPM Spec file
cat << EOF > "$RPM_TOP/SPECS/nvme-sentinel.spec"
Name:           nvme-sentinel
Version:        ${VERSION}
Release:        ${RELEASE}%{?dist}
Summary:        NVMe SSD health, endurance (TBW), and process write-attribution monitor
License:        MIT
URL:            https://github.com/khoipm06/nvme-sentinel
AutoReqProv:    no

%description
A native cross-platform NVMe SSD monitoring suite that tracks historical
wear, thermal behavior, and attributes disk writes to specific running processes.

%files
/usr/bin/nvme-sentinel
/usr/bin/nvme-sentinel-daemon
/usr/share/applications/nvme-sentinel.desktop
/usr/share/icons/hicolor/*/apps/nvme-sentinel.*
/usr/lib/systemd/user/nvme-sentinel.service

%post
if [ \$1 -eq 1 ]; then
    /usr/bin/update-desktop-database &> /dev/null || :
    /usr/bin/gtk-update-icon-cache -q /usr/share/icons/hicolor &> /dev/null || :
fi

%postun
/usr/bin/update-desktop-database &> /dev/null || :
/usr/bin/gtk-update-icon-cache -q /usr/share/icons/hicolor &> /dev/null || :

%changelog
* Sun Nov 22 2026 Khoi Pham <khoipm06@github.com> - ${VERSION}-${RELEASE}
- Native RPM package release for NVMe Sentinel
EOF

# 6. Build package
if command -v rpmbuild >/dev/null 2>&1; then
    rpmbuild --define "_topdir $RPM_TOP" --buildroot "$BUILD_ROOT" -bb "$RPM_TOP/SPECS/nvme-sentinel.spec"
    find "$RPM_TOP/RPMS" -name "*.rpm" -exec cp {} "$DIST_DIR/" \;
    echo "✔ Successfully generated RPM package in $DIST_DIR/"
else
    echo "Notice: rpmbuild not found on current host. RPM spec and build tree prepared at $RPM_TOP"
fi

rm -rf "$STAGE_DIR"
