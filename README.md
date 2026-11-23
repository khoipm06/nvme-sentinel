# NVMe Sentinel

NVMe Sentinel is an open-source desktop app and background service for monitoring NVMe SSD health, tracking endurance (TBW), and attributing disk writes to specific running processes over time.

Available for **Windows**, **macOS**, and **Linux**.

---

## Why NVMe Sentinel?

Most disk health tools only show a static, point-in-time view of S.M.A.R.T. registers. They tell you your current percentage of drive wear, but cannot tell you **which background program wrote 40 GB to your disk overnight**.

NVMe Sentinel bridges that gap:
- **Zero-wear buffering**: Collects throughput, IOPS, temperatures, and per-process disk activity in RAM, only writing consolidated records to a local database once every 10 minutes.
- **Historical write attribution**: Select any past time interval to see exactly which applications wrote data and how much each contributed.
- **Endurance forecasting**: Calculates daily burn rates against manufacturer endurance ratings (TBW) to project remaining drive life.
- **Smart write-spike alerts**: Customizable workload profiles (Office, Gaming, Workstation, Dynamic DWPD) filter out routine activity and alert you only when background processes misbehave repeatedly.
- **Privacy by design**: Runs 100% locally. No telemetry sent to remote servers, no background network connections, and no cloud accounts required.

---

## Installation

Download the latest release for your operating system from the [Releases](https://github.com/khoipm06/nvme-sentinel/releases) page.

### Windows (10 / 11 x64)

- **Installer (Recommended)**: Download and run `nvme-sentinel-v1.3.2-win-x64-setup.exe`.
  - Installs to `%LocalAppData%\Programs\NVMe Sentinel` (no Administrator rights needed).
  - Creates Desktop and Start Menu shortcuts.
  - Automatically registers in Windows Settings (*Installed apps*) for clean uninstallation.
- **Portable**: Download `nvme-sentinel-v1.3.2-win-x64-standalone.zip`, extract, and run `nvme-sentinel.exe`.

### Linux (Debian, Ubuntu, Fedora, Arch, etc.)

- **Debian / Ubuntu**: Download and install the `.deb` package:
  ```bash
  sudo apt install ./nvme-sentinel_1.3.2_amd64.deb
  ```
  This installs binaries to `/usr/bin/`, adds the application launcher to your desktop environment, and configures a user-level background service (`systemctl --user enable --now nvme-sentinel`).
- **Other distributions (Arch, Fedora, openSUSE, etc.)**:
  Download `nvme-sentinel-v1.3.2-linux-x64-standalone.tar.gz`, extract, and run the user-space installer:
  ```bash
  tar -xzf nvme-sentinel-v1.3.2-linux-x64-standalone.tar.gz
  cd nvme-sentinel-v1.3.2-linux-x64-standalone
  ./install.sh
  ```

### macOS (Apple Silicon & Intel)

- **Disk Image (Recommended)**:
  1. Download `NVMe-Sentinel-v1.3.2-osx-arm64.dmg` (Apple Silicon M1–M4) or `NVMe-Sentinel-v1.3.2-osx-x64.dmg` (Intel).
  2. Open the DMG and drag **NVMe Sentinel.app** to your `/Applications` folder.
  3. Since this is an open-source binary without an Apple Developer ID signature, allow it to run by right-clicking the app and selecting **Open**, or run:
     ```bash
     xattr -d com.apple.quarantine "/Applications/NVMe Sentinel.app"
     ```
- **Tarball Installer**:
  Download the standalone `.tar.gz`, extract, and run `./install.sh` to configure the user `LaunchAgent`.

---

## Desktop Status Bar Integration (Waybar / Polybar)

If you use a custom desktop environment or tiling window manager on Linux, the daemon provides a built-in formatting flag for Waybar or Polybar:

```bash
nvme-sentinel-daemon --waybar
```

Sample JSON output:
```json
{"text":"44°C 1.2MB/s","tooltip":"Samsung 980 PRO 2TB\nHealth: 98%\nTBW: 24.50 TB\nTemp: 44°C\nWrites: 1.2 MB/s","class":"healthy","percentage":98}
```

---

## Building from Source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/)

```bash
# Clone the repository
git clone https://github.com/khoipm06/nvme-sentinel.git
cd nvme-sentinel

# Build all projects
dotnet build NvmeSentinel.sln -c Release

# Run tests
dotnet test

# Launch GUI
./scripts/run-gui.sh
```

---

## License

Distributed under the [MIT License](LICENSE).
