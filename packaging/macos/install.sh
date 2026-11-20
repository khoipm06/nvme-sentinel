#!/usr/bin/env bash
# NVMe Sentinel - macOS User Installer

set -euo pipefail

BOLD='\033[1m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
RESET='\033[0m'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="$HOME/Applications/NVMe Sentinel"
BIN_DIR="/usr/local/bin"
LAUNCH_AGENTS_DIR="$HOME/Library/LaunchAgents"

echo -e "${BOLD}${CYAN}Installing NVMe Sentinel for macOS...${RESET}"

mkdir -p "$INSTALL_DIR" "$LAUNCH_AGENTS_DIR"
if [ ! -d "$BIN_DIR" ]; then
    mkdir -p "$HOME/.local/bin"
    BIN_DIR="$HOME/.local/bin"
fi

cp -a "$SCRIPT_DIR"/* "$INSTALL_DIR/" 2>/dev/null || true
chmod +x "$INSTALL_DIR/nvme-sentinel" "$INSTALL_DIR/nvme-sentinel-daemon" 2>/dev/null || true

# Symlink binaries
ln -sf "$INSTALL_DIR/nvme-sentinel" "$BIN_DIR/nvme-sentinel"
ln -sf "$INSTALL_DIR/nvme-sentinel-daemon" "$BIN_DIR/nvme-sentinel-daemon"

# Configure launchd agent
PLIST_SRC="$SCRIPT_DIR/com.khoipm.nvme-sentinel.plist"
if [ ! -f "$PLIST_SRC" ] && [ -f "$SCRIPT_DIR/packaging/macos/com.khoipm.nvme-sentinel.plist" ]; then
    PLIST_SRC="$SCRIPT_DIR/packaging/macos/com.khoipm.nvme-sentinel.plist"
fi

if [ -f "$PLIST_SRC" ]; then
    sed -e "s|/usr/local/bin/nvme-sentinel-daemon|$INSTALL_DIR/nvme-sentinel-daemon|" "$PLIST_SRC" > "$LAUNCH_AGENTS_DIR/com.khoipm.nvme-sentinel.plist"
    launchctl unload "$LAUNCH_AGENTS_DIR/com.khoipm.nvme-sentinel.plist" 2>/dev/null || true
    launchctl load "$LAUNCH_AGENTS_DIR/com.khoipm.nvme-sentinel.plist" 2>/dev/null || true
fi

echo -e "${BOLD}${GREEN}✔ NVMe Sentinel installed successfully on macOS!${RESET}"
echo -e "You can run: ${BOLD}nvme-sentinel${RESET} from terminal or launch from ${BOLD}$INSTALL_DIR/nvme-sentinel${RESET}"
