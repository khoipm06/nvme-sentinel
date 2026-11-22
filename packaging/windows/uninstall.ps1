# NVMe Sentinel - Windows Uninstaller
$ErrorActionPreference = "SilentlyContinue"

Write-Host "Uninstalling NVMe Sentinel..." -ForegroundColor Cyan

$TargetDir = Join-Path $env:LOCALAPPDATA "NvmeSentinel"
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\NVMe Sentinel"
$DesktopDir = [Environment]::GetFolderPath("Desktop")

# Stop processes if running
Stop-Process -Name "nvme-sentinel" -Force
Stop-Process -Name "nvme-sentinel-daemon" -Force

# Remove HKCU Run registry value if present
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (Get-ItemProperty -Path $RunKey -Name "NvmeSentinelDaemon" -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $RunKey -Name "NvmeSentinelDaemon" -Force -ErrorAction SilentlyContinue
}

# Remove shortcuts
Remove-Item -Path (Join-Path $DesktopDir "NVMe Sentinel.lnk") -Force
Remove-Item -Path $StartMenuDir -Recurse -Force

# Remove directory
if (Test-Path $TargetDir) {
    Remove-Item -Path $TargetDir -Recurse -Force
}

Write-Host "✔ NVMe Sentinel uninstalled cleanly." -ForegroundColor Green
