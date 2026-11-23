## What's Changed in v1.4.0

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
