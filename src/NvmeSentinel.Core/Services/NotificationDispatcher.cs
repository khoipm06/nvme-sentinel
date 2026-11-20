using System.Diagnostics;
using System.Runtime.InteropServices;
using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Services;

public static class NotificationDispatcher
{
    public static void SendDesktopNotification(AlertEvent alert)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var urgency = alert.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)
                    ? "critical"
                    : "normal";
                var psi = new ProcessStartInfo
                {
                    FileName = "notify-send",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add("NVMe Sentinel");
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add(urgency);
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add("drive-harddisk-solidstate");
                psi.ArgumentList.Add(alert.Title);
                psi.ArgumentList.Add(alert.Message);
                using var _ = Process.Start(psi);
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var safeTitle = alert.Title.Replace("\"", "\\\"");
                var safeMsg = alert.Message.Replace("\"", "\\\"");
                var psi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add($"display notification \"{safeMsg}\" with title \"NVMe Sentinel: {safeTitle}\"");
                using var _ = Process.Start(psi);
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Lightweight PowerShell BurntToast / Balloon fallback
                var safeTitle = alert.Title.Replace("'", "''");
                var safeMsg = alert.Message.Replace("'", "''");
                var script = $"Add-Type -AssemblyName System.Windows.Forms; $n = New-Object System.Windows.Forms.NotifyIcon; $n.Icon = [System.Drawing.SystemIcons]::Warning; $n.Visible = $true; $n.ShowBalloonTip(5000, 'NVMe Sentinel - {safeTitle}', '{safeMsg}', [System.Windows.Forms.ToolTipIcon]::Warning); Start-Sleep -Seconds 6; $n.Dispose()";
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-WindowStyle");
                psi.ArgumentList.Add("Hidden");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(script);
                using var _ = Process.Start(psi);
            }
        }
        catch
        {
            // Notifications are best-effort
        }
    }
}
