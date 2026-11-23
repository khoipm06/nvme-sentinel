#!/usr/bin/env bash
# NVMe Sentinel Cross-Platform Release Packaging Script
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT_DIR"

RAW_VER="${1:-${GITHUB_REF_NAME:-1.3.2}}"
VERSION="${RAW_VER#v}"
DIST_DIR="$ROOT_DIR/dist"
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"

DOTNET_CMD=(dotnet)
if command -v dotnet >/dev/null 2>&1; then
    DOTNET_CMD=(dotnet)
elif [ -f "$HOME/.local/share/mise/dotnet-root/dotnet" ]; then
    DOTNET_CMD=("$HOME/.local/share/mise/dotnet-root/dotnet")
elif command -v mise >/dev/null 2>&1; then
    DOTNET_CMD=(mise exec -- dotnet)
fi

RIDS=("linux-x64" "win-x64" "osx-arm64" "osx-x64")

for RID in "${RIDS[@]}"; do
    echo "=================================================="
    echo "Building packages for $RID..."
    echo "=================================================="

    # 1. STANDALONE (Self-contained, Single-File)
    ARCHIVE_BASE="nvme-sentinel-v${VERSION}-${RID}-standalone"
    BUILD_ROOT="/tmp/pub-sentinel-${RID}-standalone"
    BUILD_DIR="$BUILD_ROOT/$ARCHIVE_BASE"
    rm -rf "$BUILD_ROOT"
    mkdir -p "$BUILD_DIR"

    echo "  -> Publishing Daemon (Standalone)..."
    "${DOTNET_CMD[@]}" publish src/NvmeSentinel.Daemon/NvmeSentinel.Daemon.csproj \
        -c Release -r "$RID" --self-contained true \
        -p:PublishSingleFile=true \
        -p:EnableCompressionInSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -o "$BUILD_DIR"

    echo "  -> Publishing GUI App (Standalone)..."
    "${DOTNET_CMD[@]}" publish src/NvmeSentinel.App/NvmeSentinel.App.csproj \
        -c Release -r "$RID" --self-contained true \
        -p:PublishSingleFile=true \
        -p:EnableCompressionInSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -o "$BUILD_DIR"

    # Remove pdb debug symbols from release package
    rm -f "$BUILD_DIR"/*.pdb

    # Copy shared assets
    mkdir -p "$BUILD_DIR/icons"
    cp -a assets/icons/*.png "$BUILD_DIR/icons/" 2>/dev/null || true
    cp -a assets/nvme-sentinel.svg "$BUILD_DIR/" 2>/dev/null || true
    cp -a assets/icons/nvme-sentinel.ico "$BUILD_DIR/" 2>/dev/null || true
    cp -a README.md "$BUILD_DIR/"

    if [[ "$RID" == linux* ]]; then
        cp packaging/linux/install.sh "$BUILD_DIR/install.sh"
        cp packaging/linux/uninstall.sh "$BUILD_DIR/uninstall.sh"
        cp packaging/linux/nvme-sentinel.desktop "$BUILD_DIR/nvme-sentinel.desktop"
        cp packaging/linux/nvme-sentinel.service "$BUILD_DIR/nvme-sentinel.service"
        chmod +x "$BUILD_DIR/install.sh" "$BUILD_DIR/uninstall.sh"
        ARCHIVE_NAME="${ARCHIVE_BASE}.tar.gz"
        echo "  -> Packaging $ARCHIVE_NAME..."
        tar -czf "$DIST_DIR/$ARCHIVE_NAME" -C "$BUILD_ROOT" "$ARCHIVE_BASE"
    elif [[ "$RID" == win* ]]; then
        cp packaging/windows/install.ps1 "$BUILD_DIR/install.ps1"
        cp packaging/windows/install.cmd "$BUILD_DIR/install.cmd"
        cp packaging/windows/uninstall.ps1 "$BUILD_DIR/uninstall.ps1"
        ARCHIVE_NAME="${ARCHIVE_BASE}.zip"
        echo "  -> Packaging $ARCHIVE_NAME..."
        (cd "$BUILD_ROOT" && zip -qr9 "$DIST_DIR/$ARCHIVE_NAME" "$ARCHIVE_BASE")
    elif [[ "$RID" == osx* ]]; then
        cp packaging/macos/install.sh "$BUILD_DIR/install.sh"
        cp packaging/macos/uninstall.sh "$BUILD_DIR/uninstall.sh"
        cp packaging/macos/com.khoipm.nvme-sentinel.plist "$BUILD_DIR/com.khoipm.nvme-sentinel.plist"
        chmod +x "$BUILD_DIR/install.sh" "$BUILD_DIR/uninstall.sh"
        ARCHIVE_NAME="${ARCHIVE_BASE}.tar.gz"
        echo "  -> Packaging $ARCHIVE_NAME..."
        tar -czf "$DIST_DIR/$ARCHIVE_NAME" -C "$BUILD_ROOT" "$ARCHIVE_BASE"
    fi
    rm -rf "$BUILD_ROOT"

    # 2. FRAMEWORK-DEPENDENT (FDD)
    ARCHIVE_BASE_FDD="nvme-sentinel-v${VERSION}-${RID}-fdd"
    BUILD_ROOT_FDD="/tmp/pub-sentinel-${RID}-fdd"
    BUILD_DIR_FDD="$BUILD_ROOT_FDD/$ARCHIVE_BASE_FDD"
    rm -rf "$BUILD_ROOT_FDD"
    mkdir -p "$BUILD_DIR_FDD"

    echo "  -> Publishing Daemon (FDD)..."
    "${DOTNET_CMD[@]}" publish src/NvmeSentinel.Daemon/NvmeSentinel.Daemon.csproj \
        -c Release -r "$RID" --self-contained false \
        -o "$BUILD_DIR_FDD"

    echo "  -> Publishing GUI App (FDD)..."
    "${DOTNET_CMD[@]}" publish src/NvmeSentinel.App/NvmeSentinel.App.csproj \
        -c Release -r "$RID" --self-contained false \
        -o "$BUILD_DIR_FDD"

    rm -f "$BUILD_DIR_FDD"/*.pdb

    mkdir -p "$BUILD_DIR_FDD/icons"
    cp -a assets/icons/*.png "$BUILD_DIR_FDD/icons/" 2>/dev/null || true
    cp -a assets/nvme-sentinel.svg "$BUILD_DIR_FDD/" 2>/dev/null || true
    cp -a assets/icons/nvme-sentinel.ico "$BUILD_DIR_FDD/" 2>/dev/null || true
    cp -a README.md "$BUILD_DIR_FDD/"

    if [[ "$RID" == linux* ]]; then
        cp packaging/linux/install.sh "$BUILD_DIR_FDD/install.sh"
        cp packaging/linux/uninstall.sh "$BUILD_DIR_FDD/uninstall.sh"
        cp packaging/linux/nvme-sentinel.desktop "$BUILD_DIR_FDD/nvme-sentinel.desktop"
        cp packaging/linux/nvme-sentinel.service "$BUILD_DIR_FDD/nvme-sentinel.service"
        chmod +x "$BUILD_DIR_FDD/install.sh" "$BUILD_DIR_FDD/uninstall.sh"
        ARCHIVE_NAME="${ARCHIVE_BASE_FDD}.tar.gz"
        echo "  -> Packaging $ARCHIVE_NAME..."
        tar -czf "$DIST_DIR/$ARCHIVE_NAME" -C "$BUILD_ROOT_FDD" "$ARCHIVE_BASE_FDD"
    elif [[ "$RID" == win* ]]; then
        cp packaging/windows/install.ps1 "$BUILD_DIR_FDD/install.ps1"
        cp packaging/windows/install.cmd "$BUILD_DIR_FDD/install.cmd"
        cp packaging/windows/uninstall.ps1 "$BUILD_DIR_FDD/uninstall.ps1"
        ARCHIVE_NAME="${ARCHIVE_BASE_FDD}.zip"
        echo "  -> Packaging $ARCHIVE_NAME..."
        (cd "$BUILD_ROOT_FDD" && zip -qr9 "$DIST_DIR/$ARCHIVE_NAME" "$ARCHIVE_BASE_FDD")
    elif [[ "$RID" == osx* ]]; then
        cp packaging/macos/install.sh "$BUILD_DIR_FDD/install.sh"
        cp packaging/macos/uninstall.sh "$BUILD_DIR_FDD/uninstall.sh"
        cp packaging/macos/com.khoipm.nvme-sentinel.plist "$BUILD_DIR_FDD/com.khoipm.nvme-sentinel.plist"
        chmod +x "$BUILD_DIR_FDD/install.sh" "$BUILD_DIR_FDD/uninstall.sh"
        ARCHIVE_NAME="${ARCHIVE_BASE_FDD}.tar.gz"
        echo "  -> Packaging $ARCHIVE_NAME..."
        tar -czf "$DIST_DIR/$ARCHIVE_NAME" -C "$BUILD_ROOT_FDD" "$ARCHIVE_BASE_FDD"
    fi
    rm -rf "$BUILD_ROOT_FDD"
done

# 3. Native Distributions (deb, rpm & installers)
if [ -f "$ROOT_DIR/packaging/linux/build-deb.sh" ]; then
    echo "=================================================="
    echo "Building native Debian package (.deb)..."
    echo "=================================================="
    bash "$ROOT_DIR/packaging/linux/build-deb.sh" "$VERSION" || true
fi

if [ -f "$ROOT_DIR/packaging/linux/build-rpm.sh" ]; then
    echo "=================================================="
    echo "Building native RPM package (.rpm)..."
    echo "=================================================="
    bash "$ROOT_DIR/packaging/linux/build-rpm.sh" "$VERSION" || true
fi

# 4. Checksums
echo "=================================================="
echo "Generating SHA256SUMS.txt..."
echo "=================================================="
(cd "$DIST_DIR" && sha256sum * > SHA256SUMS.txt 2>/dev/null || sha256sum nvme-sentinel-* > SHA256SUMS.txt)

echo "All release packages created in $DIST_DIR:"
ls -lh "$DIST_DIR"
