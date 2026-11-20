#!/usr/bin/env bash
# NVMe Sentinel - Linux User-Space Uninstaller

set -euo pipefail

BOLD='\033[1m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
RESET='\033[0m'

INSTALL_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/nvme-sentinel"
BIN_DIR="${HOME}/.local/bin"
DESKTOP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICON_BASE="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"
SYSTEMD_USER_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"

echo -e "${BOLD}${CYAN}Uninstalling NVMe Sentinel...${RESET}"

# 1. Stop and disable systemd user service
if command -v systemctl >/dev/null 2>&1; then
    echo "Stopping user service..."
    systemctl --user stop nvme-sentinel.service 2>/dev/null || true
    systemctl --user disable nvme-sentinel.service 2>/dev/null || true
fi
rm -f "$SYSTEMD_USER_DIR/nvme-sentinel.service"
if command -v systemctl >/dev/null 2>&1; then
    systemctl --user daemon-reload || true
fi

# 2. Stop running daemon if any
pkill -f "nvme-sentinel-daemon" 2>/dev/null || true

# 3. Remove symlinks in ~/.local/bin
rm -f "$BIN_DIR/nvme-sentinel"
rm -f "$BIN_DIR/nvme-sentinel-daemon"

# 4. Remove desktop launcher
rm -f "$DESKTOP_DIR/nvme-sentinel.desktop"

# 5. Remove icons
for size in 16 32 48 64 128 256 512; do
    rm -f "$ICON_BASE/${size}x${size}/apps/nvme-sentinel.png"
done
rm -f "$ICON_BASE/scalable/apps/nvme-sentinel.svg"

# Update desktop and icon databases
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t "$ICON_BASE" 2>/dev/null || true
fi

# 6. Remove installation directory
if [ -d "$INSTALL_DIR" ]; then
    rm -rf "$INSTALL_DIR"
fi

echo -e "${BOLD}${GREEN}✔ NVMe Sentinel uninstalled cleanly.${RESET}"
