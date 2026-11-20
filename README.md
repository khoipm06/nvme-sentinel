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

## Architecture

```text
nvme-sentinel/
├── src/
│   ├── NvmeSentinel.Core/      # Hardware providers, process I/O trackers, SQLite rollups, NamedPipe/UDS IPC
│   ├── NvmeSentinel.Daemon/    # Headless user-space background worker & IPC server
│   └── NvmeSentinel.App/       # Native Avalonia + SukiUI desktop dashboard & System Tray client
└── tests/
    └── NvmeSentinel.Tests/     # Storage rollup, lifespan forecasting, and end-to-end IPC tests
```

### Platform Hardware Backends
| Platform | NVMe S.M.A.R.T. & Link Info | Live Block I/O & Temps | Per-Process Write Attribution |
| :--- | :--- | :--- | :--- |
| **Linux** | `UDisks2` (`NVMe.Controller` D-Bus, unprivileged) + `sysfs` + `smartctl` fallback | `/sys/block/nvme*/stat` + `hwmon` | `/proc/<pid>/io` + kernel/root delta reconciliation |
| **Windows** | `IOCTL_STORAGE_QUERY_PROPERTY` (NVMe Log Page `02h`) + Storage CIM + `smartctl` fallback | `IOCTL_DISK_PERFORMANCE` | Win32 `GetProcessIoCounters` |
| **macOS** | `SPNVMeDataType` / `IOKit` + `smartctl` fallback | `iostat` | `libproc` `proc_pid_rusage` (`ri_diskio_byteswritten`) |

---

## Quick Start

### Prerequisites
- **.NET 10 SDK** (or run a self-contained published binary with zero runtime dependencies).

### Build & Test
```bash
dotnet build NvmeSentinel.sln -c Release
dotnet test NvmeSentinel.sln
```

### Launch Desktop GUI (Auto-spawns Daemon if not running)
```bash
./scripts/run-gui.sh
```

### Daemon CLI Commands
```bash
./scripts/run-daemon.sh              # Run background daemon in foreground
./scripts/run-daemon.sh --once       # Print one-shot hardware & SMART JSON snapshot
./scripts/run-daemon.sh --status     # Query running daemon status over IPC
./scripts/run-daemon.sh --stop       # Gracefully flush buffers and stop daemon
```

### Publish Self-Contained Native Binaries (Linux, Windows, macOS)
```bash
./scripts/publish-crossplatform.sh
```
Outputs standalone binaries under `publish/linux-x64`, `publish/win-x64`, and `publish/osx-arm64`.
