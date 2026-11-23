# NVMe Sentinel

NVMe Sentinel is a cross-platform desktop application and background service that monitors NVMe SSD health, tracks write endurance (TBW), and identifies which running processes write data to your drive over time.

Runs on **Linux**, **Windows**, and **macOS**.

---

## Features

- **Process write attribution**: Select any time interval on the timeline to see which processes wrote to your disk and how much each contributed.
- **Low-wear metrics collection**: Samples disk counters, throughput, IOPS, and temperatures in RAM every second, flushing consolidated history to a local SQLite database once every 10 minutes to avoid adding SSD wear.
- **Endurance & lifespan projections**: Tracks daily write burn rates against factory endurance ratings (TBW) to estimate remaining drive lifespan.
- **Configurable alert profiles**: Choose an alert profile matching your workload (Office, Gaming, Workstation, or DWPD-calculated) with sustained spike detection to filter out one-off large file downloads.
- **Live hardware diagnostics**: Real-time read/write throughput, IOPS, PCIe generation/width verification, controller self-tests (Short and Extended), and multi-sensor thermals.
- **Desktop status bar & CLI export**: Integration for Waybar/Polybar via `--waybar`, plus terminal telemetry exports to CSV or JSON.
- **In-app documentation**: Built-in Help & FAQ tab answering common questions about write amplification, OEM reboot power cuts, and wear leveling.

---

## Installation

Pre-built binaries are available on the [Releases](https://github.com/khoipm06/nvme-sentinel/releases) page for each platform.

### Windows (10 / 11 x64)

- **Native Installer (Recommended)**:
  Download and run `nvme-sentinel-v1.3.2-win-x64-setup.exe`.
  - Installs to `%LocalAppData%\Programs\NVMe Sentinel` without requiring administrator privileges.
  - Adds Start Menu and Desktop shortcuts.
  - Registers a clean uninstaller in Windows Settings (*Installed apps*).
  - Optional autostart at user login.
- **Portable**:
  Download `nvme-sentinel-v1.3.2-win-x64-standalone.zip`, extract anywhere, and launch `nvme-sentinel.exe`.

### Linux

- **Debian / Ubuntu / Mint (`.deb`)**:
  Download `nvme-sentinel_1.3.2_amd64.deb` and install:
  ```bash
  sudo apt install ./nvme-sentinel_1.3.2_amd64.deb
  ```
  This puts binaries in `/usr/bin/`, sets up desktop menu entries and icons, and enables the user systemd service:
  ```bash
  systemctl --user enable --now nvme-sentinel
  ```
- **Fedora, Arch, openSUSE, and other distributions**:
  Download `nvme-sentinel-v1.3.2-linux-x64-standalone.tar.gz`, extract, and run the user-space installer:
  ```bash
  tar -xzf nvme-sentinel-v1.3.2-linux-x64-standalone.tar.gz
  cd nvme-sentinel-v1.3.2-linux-x64-standalone
  ./install.sh
  ```
  To uninstall later, run `~/.local/share/nvme-sentinel/uninstall.sh`.

### macOS

- **Native DMG (Recommended)**:
  1. Download `NVMe-Sentinel-v1.3.2-osx-arm64.dmg` (Apple Silicon M1–M4) or `NVMe-Sentinel-v1.3.2-osx-x64.dmg` (Intel).
  2. Open the disk image and drag **NVMe Sentinel.app** into `/Applications`.
  3. Because this is an unsigned open-source app, clear the quarantine attribute before first launch:
     ```bash
     xattr -d com.apple.quarantine "/Applications/NVMe Sentinel.app"
     ```
- **Tarball Installer**:
  Download the standalone `.tar.gz`, extract, and run `./install.sh` to install to `~/Applications/` and register a `LaunchAgent`.

---

## Daemon & CLI Usage

The background service (`nvme-sentinel-daemon`) runs quietly in the background and communicates with the desktop app over a local IPC socket. It can also be called directly from the command line:

```bash
# Print current hardware snapshot as JSON
nvme-sentinel-daemon --status

# Output status formatted for Waybar or Polybar
nvme-sentinel-daemon --waybar

# Export historical telemetry to a file (CSV or JSON)
nvme-sentinel-daemon --export /path/to/report.csv
nvme-sentinel-daemon --export /path/to/report.json

# Gracefully stop the background daemon
nvme-sentinel-daemon --stop
```

### Waybar Configuration Example

```json
"custom/nvme": {
    "exec": "nvme-sentinel-daemon --waybar",
    "return-type": "json",
    "interval": 2,
    "format": "SSD {text}"
}
```

---

## Building from Source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/)

```bash
git clone https://github.com/khoipm06/nvme-sentinel.git
cd nvme-sentinel

# Build Release binaries
dotnet build NvmeSentinel.sln -c Release

# Run test suite
dotnet test

# Launch GUI app
./scripts/run-gui.sh
```

---

## License

Distributed under the [MIT License](LICENSE).
