## NVMe Sentinel v1.3.0 Release Notes

### Highlights

NVMe Sentinel v1.3.0 introduces an intelligent **Smart Alert Profiling Engine**, **Dynamic DWPD-based Endurance Thresholds**, **Sustained Spike Sensitivity**, and a fix for desktop lifecycle shutdown re-entrancy.

---

### Key Improvements & Features

1. **Workload Alert Profiles (Settings & Alerts)**:
   - **Office / General (5–10 GB / 5m, default 8 GB)**: Tailored for standard productivity, web browsing, and development. Exceeding 10 GB flags memory leaks, crash dumps, or excessive swap thrashing.
   - **Gaming / High I/O (30–50 GB / 5m, default 35 GB)**: Designed for gaming platforms (Steam, Epic Games). Prevents false alarms during large game downloads and shader decompilation.
   - **Creator / Workstation (80–150 GB / 5m, default 100 GB)**: Configured for 4K/8K video editing, CAD exports, virtual machines, and high-throughput compilation.
   - **Dynamic TBW (DWPD-based)**: Computes dynamic thresholds automatically based on drive endurance ratings:
     $$\text{Alert Threshold (GB/5m)} = \frac{\text{TBW (TB)} \times 1000}{\text{Warranty Days (1825)}} \times \frac{5}{1440} \times \text{Burst Multiplier (12x)}$$
     *(For a standard 1TB TLC SSD with 600 TBW, this yields ~13.7 GB / 5m).*
   - **Custom Threshold**: Complete manual control over the 5-minute write limit.

2. **False Alarm Mitigation & Multi-Tier Escalation**:
   - **Sustained Spike Sensitivity**: Instead of notifying on isolated 1-off bursts (such as copying a single large movie file), warnings are only dispatched when 2 or more consecutive 5-minute intervals exceed the threshold.
   - **Process Whitelisting**: Process write bursts driven primarily ($\ge 65\%$) by known safe applications (`steam`, `epicgameslauncher`, `rsync`, `tar`, `timeshift`, `restic`, browsers) are logged to the time-series store as `Info` audit events without disturbing the user with popups.
   - **Critical Emergency Escalation**: Write floods exceeding emergency limits ($\ge 50$ GB / 5 min or $\ge 3\times$ threshold) immediately escalate to `Critical` priority.

3. **Resolved Previous Boot Crash (`SIGABRT`)**:
   - Fixed an Avalonia / SukiUI lifecycle re-entrancy issue where system logout or reboot triggered duplicate shutdown requests, causing `SukiEffect.EnsureDisposed()` to throw `InvalidOperationException`.
   - Added atomic lifecycle state transitions (`Interlocked.Exchange`) to guarantee safe disposal of tray icons and UI subscriptions on session exit.

4. **Streamlined Documentation & Cross-Platform Parity**:
   - Removed internal storage architecture diagrams from `README.md` to prioritize user-facing guides.
   - Updated setup documentation and installers for Windows, macOS, and Linux with equal weight.

---

### Installation Quickstart

#### Windows (10 / 11 x64)
- Download `nvme-sentinel-v1.3.0-win-x64-standalone.zip`, extract, and run `nvme-sentinel.exe` (portable), or double-click `install.cmd` for Start Menu and Desktop shortcuts.

#### macOS (Apple Silicon & Intel)
- Download `nvme-sentinel-v1.3.0-osx-arm64-standalone.tar.gz` (or `osx-x64`), extract, and run `./install.sh`.

#### Linux (All distributions)
- Download `nvme-sentinel-v1.3.0-linux-x64-standalone.tar.gz`, extract, and execute:
  ```bash
  ./install.sh
  ```
  Installs into `~/.local/share/nvme-sentinel/` and activates the user-space `systemd --user` background monitoring daemon.
