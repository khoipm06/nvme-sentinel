# NVMe Sentinel Release Notes

This document tracks changes, improvements, and fixes across all released versions of NVMe Sentinel.

---

## NVMe Sentinel v1.4.0

This release introduces native RPM packaging for Red Hat enterprise and community Linux distributions, refreshes visual documentation with real-world NVMe hardware telemetry, and adds continuous integration testing across the development pipeline.

### Packaging & Distribution
- **Fedora / RHEL / openSUSE RPM Packaging**: Added a native `.rpm` package (`nvme-sentinel-1.4.0-1.x86_64.rpm`) featuring automated user-level systemd service setup, desktop shortcuts, and high-resolution application icons.
- **Enhanced Continuous Integration**: Added a dedicated GitHub Actions CI pipeline running automated unit test suites and release builds on main branch pushes and pull requests.

### UI & Telemetry Visualization
- **Timeline & Throughput Refinements**: Added multi-tick timestamp labels across 24-hour write history timelines and live I/O activity curves, giving clearer context during sustained write spikes.
- **Clean Hardware Documentation**: Refreshed official screenshots using genuine NVMe controller telemetry across both Dark and Light themes.

### Core & Lifespan Modeling
- **Standardized Silicon Endurance Calculations**: Aligned lifespan projections directly with manufacturer-rated TBW metrics without arbitrary risk offsets, letting users track endurance horizons reliably against hardware specifications.
- **Production Code Hygiene**: Cleaned up all development mock fallbacks from the core monitoring engine.

---

## NVMe Sentinel v1.3.2

This maintenance and packaging release introduces native desktop installers for Windows, Linux, and macOS, alongside core reliability and concurrency improvements across the monitoring engine.

### Native Desktop Packages
- **Windows**: Added a native Inno Setup installer (`.exe`) that installs to user programs, sets up Desktop and Start Menu shortcuts, and integrates cleanly with Windows Settings for uninstallation.
- **Linux**: Added a Debian/Ubuntu `.deb` package with an integrated user-level systemd service and desktop launcher.
- **macOS**: Added a drag-and-drop `.dmg` disk image containing `NVMe Sentinel.app`, with setup guidance for Gatekeeper on open-source binaries.

### Reliability & Core Fixes
- **Write History Retention**: Fixed a timing issue during periodic database saves where active memory buffers could be reset before database writes finished, ensuring historical write metrics remain accurate.
- **Hardware Spec Alignment**: Updated Windows NVMe health queries to decode 128-bit data units per the NVMe specification, avoiding numeric truncation on high-capacity drives.
- **Process & Thread Safety**: Improved termination of background hardware query processes when tasks are cancelled, added concurrency gates to live IPC streams, and ensured UI state updates occur exclusively on the main dispatch thread.
- **macOS Path Validation**: Added validation to storage device paths before initiating controller diagnostic self-tests.

---

## NVMe Sentinel v1.3.1

This release focuses on everyday usability, storage privacy, and addressing common questions about NVMe SSD behavior.

### What's New
- **Built-in FAQ Tab**: Added an in-app Help & FAQ view answering common questions regarding drive endurance, write amplification, normal temperature ranges, and OEM reboot power-cut quirks.
- **Storage Privacy & Custom Paths**: Renamed the local SQLite database from `telemetry.db` to `history.db` to reflect its offline nature, and added a settings option to choose a custom database storage directory. Existing databases migrate automatically.
- **Reboot Event Logging**: System reboots and normal power-cycle increments are now categorized as quiet informational events rather than triggering false-positive unsafe shutdown warnings.
- **UI & Display Polish**: Cleaned up number formatting across metric cards by removing redundant trailing zeroes and placing raw byte values into tooltips.
- **Installer Safety**: Updated installation scripts to stop running background services before replacing binaries during upgrades.

---

## NVMe Sentinel v1.3.0

This release introduces customizable workload alert profiles, sustained spike detection to eliminate false alarms, and fixes an application shutdown issue on desktop environments.

### What's New
- **Workload Alert Profiles**: Added configurable alert presets to match how you use your machine:
  - *Office / General*: Sensitive thresholds (~8 GB / 5m) for standard web browsing and productivity, catching unexpected runaway writes early.
  - *Gaming / High I/O*: Higher thresholds (~35 GB / 5m) to avoid interruptions during large game installs or launcher updates.
  - *Creator / Workstation*: Designed for video editing and heavy compiling workflows (~100 GB / 5m).
  - *Dynamic DWPD*: Calculates limits automatically based on your SSD's rated warranty endurance.
  - *Custom*: Allows setting exact 5-minute write limits.
- **Sustained Spike Filtering**: The daemon now requires high write volumes across multiple consecutive 5-minute cycles before triggering desktop alerts, preventing false alarms from single large file copies.
- **Process Whitelisting**: Added the ability to mute popup alerts for trusted software (such as Steam, backup tools, or package managers) while continuing to log their metrics in the background.
- **Desktop Exit Stability**: Resolved an issue where logging out or shutting down the desktop session could cause an application crash during window disposal.

---

## NVMe Sentinel v1.2.0

This release refines interface responsiveness, notification feedback, and drive endurance estimation.

### What's New
- **Interface Interaction Polish**: Fixed an issue where hovering over the sidebar navigation could produce ghosting or momentary text blurring.
- **Toast Notifications**: Status updates now appear as timed banners with a smooth 5-second progress countdown bar before dismissing.
- **Daemon Connectivity Indicator**: Replaced text status with a clear green/red status badge showing background service availability.
- **Contextual Tooltips**: Added hover tooltips across health scores, thermal sensors, and throughput gauges to explain what each metric means.
- **Endurance Modeling Refinements**: Improved TBW estimation algorithms by normalizing drive capacities and applying silicon endurance sanity boundaries to firmware wear extrapolations.

---

## NVMe Sentinel v1.1.0

This release adds desktop status bar integration, terminal data export capabilities, and theme toggling from the system tray.

### What's New
- **Waybar & Polybar Integration**: Added a `--waybar` flag to `nvme-sentinel-daemon` that outputs real-time temperature, health, and write speed as JSON formatted for Linux desktop status bars.
- **Terminal Telemetry Export**: Added an `--export` command to the daemon CLI, allowing users to dump historical write logs directly to CSV or JSON from the command line.
- **Tray Theme Switcher**: Added a Dark / Light theme toggle option directly to the system tray context menu.
- **Archive Extraction Packaging Fix**: Updated release packaging so archive files extract cleanly into a dedicated directory rather than spilling loose files into the current folder.

---

## NVMe Sentinel v1.0.0

Initial release of NVMe Sentinel, a native cross-platform NVMe SSD monitoring and endurance tracking suite for Linux, macOS, and Windows.

### Key Features
- **Low-Impact Background Daemon**: Collects throughput, IOPS, temperatures, and per-process disk writes in RAM, flushing consolidated records to a local SQLite database every 10 minutes to avoid adding wear to your SSD.
- **Timeline Inspection**: View historical write activity, temperature trends, and cumulative TBW over 1 hour, 24 hours, 7 days, 30 days, 1 year, or lifetime ranges.
- **Process Attribution**: Drag to select any past time interval to see which processes wrote the most data during that specific period.
- **Lifespan Projection**: Projects remaining drive life based on historical daily burn rates and manufacturer endurance ratings.
- **Hardware Diagnostics**: Read controller health registers, check PCIe link width and speed, and initiate NVMe self-tests.
- **System Tray Integration**: Minimize to the system tray to keep lightweight background monitoring active without taking up screen space.
