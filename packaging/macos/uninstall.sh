#!/usr/bin/env bash
# NVMe Sentinel - macOS Uninstaller

set -euo pipefail

BOLD='\033[1m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
RESET='\033[0m'

INSTALL_DIR="$HOME/Applications/NVMe Sentinel"
LAUNCH_AGENTS_DIR="$HOME/Library/LaunchAgents"

echo -e "${BOLD}${CYAN}Uninstalling NVMe Sentinel from macOS...${RESET}"

if [ -f "$LAUNCH_AGENTS_DIR/com.khoipm.nvme-sentinel.plist" ]; then
    launchctl unload "$LAUNCH_AGENTS_DIR/com.khoipm.nvme-sentinel.plist" 2>/dev/null || true
    rm -f "$LAUNCH_AGENTS_DIR/com.khoipm.nvme-sentinel.plist"
fi

pkill -f "nvme-sentinel-daemon" 2>/dev/null || true
rm -f "/usr/local/bin/nvme-sentinel" "/usr/local/bin/nvme-sentinel-daemon"
rm -f "$HOME/.local/bin/nvme-sentinel" "$HOME/.local/bin/nvme-sentinel-daemon"

if [ -d "$INSTALL_DIR" ]; then
    rm -rf "$INSTALL_DIR"
fi

echo -e "${BOLD}${GREEN}✔ NVMe Sentinel uninstalled cleanly.${RESET}"
