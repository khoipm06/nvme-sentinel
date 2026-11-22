#!/usr/bin/env bash
# NVMe Sentinel - Linux User-Space Installer (Non-Root)
# Safe for Fedora Silverblue, Kinoite, Aurora, SteamOS, Ubuntu, Debian, Arch, etc.

set -euo pipefail

# ANSI color codes
BOLD='\033[1m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
RESET='\033[0m'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo -e "${BOLD}${CYAN}============================================${RESET}"
echo -e "${BOLD}${CYAN}   NVMe Sentinel - Desktop Installation     ${RESET}"
echo -e "${BOLD}${CYAN}============================================${RESET}"
echo

# 1. Target Directories (XDG User Spec)
INSTALL_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/nvme-sentinel"
BIN_DIR="${HOME}/.local/bin"
DESKTOP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICON_BASE="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"
SYSTEMD_USER_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"

echo -e "Target installation directory: ${BOLD}${INSTALL_DIR}${RESET}"

# 2. Create directories
mkdir -p "$INSTALL_DIR" "$BIN_DIR" "$DESKTOP_DIR" "$SYSTEMD_USER_DIR"

# Stop existing daemon service if running to allow clean binary replacement (prevent ETXTBSY)
if command -v systemctl >/dev/null 2>&1; then
    systemctl --user stop nvme-sentinel.service 2>/dev/null || true
fi

# 3. Copy application binaries and runtime files
echo "Installing application binaries..."
# If executed from inside an unpacked release bundle or repo root:
if [ -f "$SCRIPT_DIR/nvme-sentinel" ]; then
    SRC_DIR="$SCRIPT_DIR"
elif [ -d "$SCRIPT_DIR/../../publish/linux-x64" ]; then
    SRC_DIR="$SCRIPT_DIR/../../publish/linux-x64"
elif [ -d "$SCRIPT_DIR/bin" ]; then
    SRC_DIR="$SCRIPT_DIR/bin"
else
    SRC_DIR="$SCRIPT_DIR"
fi

# Copy all files from source directory to target installation directory (--remove-destination unlinks active inodes)
cp -a --remove-destination "$SRC_DIR"/* "$INSTALL_DIR/" 2>/dev/null || cp -a "$SRC_DIR"/* "$INSTALL_DIR/" 2>/dev/null || true
# Ensure executables have execute permissions
chmod +x "$INSTALL_DIR/nvme-sentinel" "$INSTALL_DIR/nvme-sentinel-daemon" 2>/dev/null || true

# 4. Create symlinks in ~/.local/bin
ln -sf "$INSTALL_DIR/nvme-sentinel" "$BIN_DIR/nvme-sentinel"
ln -sf "$INSTALL_DIR/nvme-sentinel-daemon" "$BIN_DIR/nvme-sentinel-daemon"

# 5. Install Desktop Launcher
echo "Installing desktop launcher..."
DESKTOP_SRC="$SCRIPT_DIR/nvme-sentinel.desktop"
if [ ! -f "$DESKTOP_SRC" ] && [ -f "$SCRIPT_DIR/packaging/linux/nvme-sentinel.desktop" ]; then
    DESKTOP_SRC="$SCRIPT_DIR/packaging/linux/nvme-sentinel.desktop"
fi

if [ -f "$DESKTOP_SRC" ]; then
    # Inject full binary path into Exec to ensure it launches even if ~/.local/bin isn't in default GUI environment PATH
    sed -e "s|^Exec=nvme-sentinel|Exec=$INSTALL_DIR/nvme-sentinel|" "$DESKTOP_SRC" > "$DESKTOP_DIR/nvme-sentinel.desktop"
    chmod +x "$DESKTOP_DIR/nvme-sentinel.desktop"
fi

# 6. Install Icons
echo "Installing icons..."
# Search for icons in bundle or assets
ICONS_SRC_DIR=""
if [ -d "$SCRIPT_DIR/icons" ]; then
    ICONS_SRC_DIR="$SCRIPT_DIR/icons"
elif [ -d "$SCRIPT_DIR/assets/icons" ]; then
    ICONS_SRC_DIR="$SCRIPT_DIR/assets/icons"
elif [ -d "$SCRIPT_DIR/../../assets/icons" ]; then
    ICONS_SRC_DIR="$SCRIPT_DIR/../../assets/icons"
fi

if [ -n "$ICONS_SRC_DIR" ] && [ -d "$ICONS_SRC_DIR" ]; then
    for size in 16 32 48 64 128 256 512; do
        if [ -f "$ICONS_SRC_DIR/nvme-sentinel-${size}.png" ]; then
            mkdir -p "$ICON_BASE/${size}x${size}/apps"
            cp "$ICONS_SRC_DIR/nvme-sentinel-${size}.png" "$ICON_BASE/${size}x${size}/apps/nvme-sentinel.png"
        fi
    done
fi

# Scalable SVG
SVG_SRC="$SCRIPT_DIR/nvme-sentinel.svg"
if [ ! -f "$SVG_SRC" ] && [ -f "$SCRIPT_DIR/assets/nvme-sentinel.svg" ]; then
    SVG_SRC="$SCRIPT_DIR/assets/nvme-sentinel.svg"
elif [ ! -f "$SVG_SRC" ] && [ -f "$SCRIPT_DIR/../../assets/nvme-sentinel.svg" ]; then
    SVG_SRC="$SCRIPT_DIR/../../assets/nvme-sentinel.svg"
fi

if [ -f "$SVG_SRC" ]; then
    mkdir -p "$ICON_BASE/scalable/apps"
    cp "$SVG_SRC" "$ICON_BASE/scalable/apps/nvme-sentinel.svg"
fi

# 7. Install Systemd User Unit
echo "Configuring background monitoring service..."
SERVICE_SRC="$SCRIPT_DIR/nvme-sentinel.service"
if [ ! -f "$SERVICE_SRC" ] && [ -f "$SCRIPT_DIR/packaging/linux/nvme-sentinel.service" ]; then
    SERVICE_SRC="$SCRIPT_DIR/packaging/linux/nvme-sentinel.service"
fi

if [ -f "$SERVICE_SRC" ]; then
    cp "$SERVICE_SRC" "$SYSTEMD_USER_DIR/nvme-sentinel.service"
    if command -v systemctl >/dev/null 2>&1; then
        systemctl --user daemon-reload || true
        systemctl --user enable nvme-sentinel.service 2>/dev/null || true
        systemctl --user restart nvme-sentinel.service 2>/dev/null || systemctl --user start nvme-sentinel.service 2>/dev/null || true
    fi
fi

# 8. Update desktop and icon databases if available
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t "$ICON_BASE" 2>/dev/null || true
fi

# 9. Verification & Summary
echo
echo -e "${BOLD}${GREEN}✔ Installation completed successfully!${RESET}"
echo
echo -e "You can launch NVMe Sentinel via:"
echo -e "  1. Your desktop Application Menu (search ${BOLD}'NVMe Sentinel'${RESET})"
echo -e "  2. Terminal command: ${BOLD}nvme-sentinel${RESET}"
echo

if [[ ":$PATH:" != *":$BIN_DIR:"* ]]; then
    echo -e "${YELLOW}Notice: $BIN_DIR is not yet in your current PATH.${RESET}"
    echo -e "To run directly from terminal without logging out, add this to your ~/.bashrc or ~/.zshrc:"
    echo -e "  ${BOLD}export PATH=\"\$HOME/.local/bin:\$PATH\"${RESET}"
    echo
fi

echo -e "To uninstall anytime, run: ${BOLD}$INSTALL_DIR/uninstall.sh${RESET}"
