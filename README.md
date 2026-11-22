# NVMe Sentinel

A native, cross-platform NVMe SSD health, endurance (TBW), and process write-attribution telemetry suite built with **.NET** and **Avalonia UI**.

Unlike traditional point-in-time S.M.A.R.T. viewers or browser-based server dashboards, **NVMe Sentinel** pairs an ultra-lightweight user-space background daemon with a GPU-accelerated native desktop dashboard so you can inspect **exact historical TBW growth**, **thermal behavior**, and **which processes caused disk writes during any past time window**.

---

## Key Capabilities

- **Low-Wear Background Daemon (`nvme-sentinel-daemon`)**
  - Samples live block throughput, IOPS, thermal sensors, and per-process disk I/O every second in RAM (`0` disk writes while buffering).
  - Flushes batched 5-minute buckets to an embedded WAL-mode SQLite time-series store every 10 minutes (or immediately upon detecting an abnormal write spike, thermal event, or S.M.A.R.T. integrity change).
  - Automatic multi-tier retention: **5-minute resolution** for 7 days, **hourly rollups** for 1 year, and **daily rollups** indefinitely.
- **Interactive TBW Time-Machine**
  - Inspect cumulative TBW, interval write deltas, temperature history, and endurance wear across `1H`, `24H`, `7D`, `30D`, `1Y`, or `All Time`.
  - **Drag-to-Brush Selection**: Drag horizontally across any spike on the timeline to isolate that exact sub-window and view ranked **Top Write Culprit Processes** responsible for those writes.
- **Endurance & Lifespan Forecast**
  - Computes 24-hour, 7-day, 30-day, and lifetime daily burn rates (`GB/day`) against rated drive endurance (model presets, wear-curve extrapolation, or custom TBW override) to project remaining lifespan.
- **Live Hardware & Per-Process Telemetry**
  - Real-time Read/Write throughput (`MB/s`), Read/Write `IOPS`, PCIe Link Generation & Lane Width verification, power state, multi-sensor temperatures, and live per-process disk write monitor.
- **Active Diagnostics, Smart Alerts & System Tray**
  - Trigger NVMe Controller **Short** and **Extended Self-Tests** (elevating via Polkit / UAC / macOS authorization only when invoked).
  - Native desktop notifications for runaway write loops, high composite temperatures, media integrity errors, or unsafe shutdowns.
  - Native System Tray icon with live health/thermal state, quick snapshot flush, and independent **Quit GUI (Keep Daemon Running)** mode.
  - One-click login autostart registration (`systemd --user` on Linux, `LaunchAgent` on macOS, `HKCU\...\Run` on Windows) and `CSV` / `JSON` report export.

---

## Installation & Releases

Pre-built native release packages are available on the [GitHub Releases](https://github.com/khoipm06/nvme-sentinel/releases) page:

- **Standalone Packages** (`*-standalone.tar.gz` / `*-standalone.zip`): Fully self-contained native bundles with embedded .NET runtime. Zero external dependencies required.
- **Framework-Dependent Packages** (`*-fdd.tar.gz` / `*-fdd.zip`): Lightweight portable archives (~12–15 MB) for machines with .NET 10 SDK/runtime installed.

### Cross-Platform Installation

#### Windows (10 / 11 x64)
1. Download `nvme-sentinel-v1.3.0-win-x64-standalone.zip` from Releases.
2. Extract the ZIP archive.
3. **Portable run**: Double-click `nvme-sentinel.exe` to run immediately without installation.
4. **Desktop & Start Menu integration (No Admin Required)**:
   - Double-click `install.cmd` (or right-click `install.ps1` -> *Run with PowerShell*).
   - Installs binaries into `%LOCALAPPDATA%\NvmeSentinel\`, generates Start Menu / Desktop shortcuts, and sets up tray integration.
   - To uninstall cleanly anytime, run `uninstall.ps1`.

#### macOS (Apple Silicon M1/M2/M3/M4 & Intel x64)
1. Download `nvme-sentinel-v1.3.0-osx-arm64-standalone.tar.gz` (Apple Silicon) or `nvme-sentinel-v1.3.0-osx-x64-standalone.tar.gz` (Intel).
2. Extract and run user-space installer:
   ```bash
   tar -xzf nvme-sentinel-v1.3.0-osx-arm64-standalone.tar.gz
   cd nvme-sentinel-v1.3.0-osx-arm64-standalone
   ./install.sh
   ```
3. Installs binaries into `~/Applications/NVMe Sentinel`, registers background monitoring LaunchAgent (`~/Library/LaunchAgents/com.khoipm.nvme-sentinel.plist`), and symlinks to `~/.local/bin/nvme-sentinel`.

#### Linux (Fedora, Silverblue, Ubuntu, Debian, Arch, SteamOS)
1. Download `nvme-sentinel-v1.3.0-linux-x64-standalone.tar.gz`.
2. Extract and run non-root user installer:
   ```bash
   tar -xzf nvme-sentinel-v1.3.0-linux-x64-standalone.tar.gz
   cd nvme-sentinel-v1.3.0-linux-x64-standalone
   ./install.sh
   ```
3. Installs binaries to `~/.local/share/nvme-sentinel/`, creates desktop launcher, registers hicolor icons, and enables background `systemd --user` monitoring daemon (`nvme-sentinel.service`). To uninstall, run `~/.local/share/nvme-sentinel/uninstall.sh`.

---

### Building from Source (Optional)

If you have the **.NET 10 SDK** installed, you can build from source:
```bash
dotnet build NvmeSentinel.sln -c Release
./scripts/run-gui.sh
```

---

## Privacy & Security

- **100% Offline & Private**: All data is stored locally in `history.db`. Zero network requests, analytics, or tracking.
- **Minimal System Impact**: Telemetry is buffered in RAM and flushed every 10 minutes to minimize SSD writes. CPU usage is `< 0.1%`.
- **User-Space & Non-Root**: Runs cleanly without root/administrator privileges for everyday monitoring. Privileged self-tests elevate only when explicitly requested via standard OS prompts (Polkit / UAC).

