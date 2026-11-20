# NVMe Sentinel - Windows User-Space Installer (No Admin Rights Needed)
$ErrorActionPreference = "Stop"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "   NVMe Sentinel - Windows Installation     " -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$TargetDir = Join-Path $env:LOCALAPPDATA "NvmeSentinel"
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\NVMe Sentinel"
$DesktopDir = [Environment]::GetFolderPath("Desktop")

Write-Host "Installing to: $TargetDir" -ForegroundColor Gray

# 1. Create target directory
if (-not (Test-Path $TargetDir)) {
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
}

# 2. Copy binaries and assets
Write-Host "Copying application files..." -ForegroundColor Gray
Copy-Item -Path "$ScriptDir\*" -Destination $TargetDir -Recurse -Force

# 3. Create Start Menu Shortcut
Write-Host "Creating Start Menu shortcut..." -ForegroundColor Gray
if (-not (Test-Path $StartMenuDir)) {
    New-Item -ItemType Directory -Path $StartMenuDir -Force | Out-Null
}

$WshShell = New-Object -ComObject WScript.Shell
$ExePath = Join-Path $TargetDir "nvme-sentinel.exe"
$IcoPath = Join-Path $TargetDir "nvme-sentinel.ico"

$Shortcut = $WshShell.CreateShortcut((Join-Path $StartMenuDir "NVMe Sentinel.lnk"))
$Shortcut.TargetPath = $ExePath
$Shortcut.WorkingDirectory = $TargetDir
if (Test-Path $IcoPath) {
    $Shortcut.IconLocation = "$IcoPath,0"
}
$Shortcut.Description = "NVMe Hardware Health & Endurance Monitor"
$Shortcut.Save()

# 4. Create Desktop Shortcut
$DesktopShortcut = $WshShell.CreateShortcut((Join-Path $DesktopDir "NVMe Sentinel.lnk"))
$DesktopShortcut.TargetPath = $ExePath
$DesktopShortcut.WorkingDirectory = $TargetDir
if (Test-Path $IcoPath) {
    $DesktopShortcut.IconLocation = "$IcoPath,0"
}
$DesktopShortcut.Description = "NVMe Hardware Health & Endurance Monitor"
$DesktopShortcut.Save()

Write-Host ""
Write-Host "✔ NVMe Sentinel installed successfully!" -ForegroundColor Green
Write-Host "You can start the app from your Start Menu or Desktop shortcut."
Write-Host ""
