using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NvmeSentinel.Core.Services;

public static class AutostartManager
{
    private const string ServiceUnitName = "nvme-sentinel-daemon.service";
    private const string MacAgentLabel = "io.nvmesentinel.daemon";
    private const string WindowsRunValueName = "NvmeSentinelDaemon";

    public static bool IsAutostartEnabled()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var (code, _) = RunSync("reg", [@"query", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", WindowsRunValueName]);
                return code == 0;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return File.Exists(GetMacLaunchAgentPath());
            }

            var unitPath = GetLinuxSystemdUnitPath();
            if (!File.Exists(unitPath))
            {
                return false;
            }

            var (exitCode, stdout) = RunSync("systemctl", ["--user", "is-enabled", ServiceUnitName]);
            return exitCode == 0 && stdout.Contains("enabled", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static (bool Ok, string Message) SetAutostart(bool enable, string daemonExecutablePath)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (enable)
                {
                    var (code, err) = RunSync("reg",
                        [@"add", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", WindowsRunValueName, "/t", "REG_SZ", "/d", $"\"{daemonExecutablePath}\"", "/f"]);
                    return code == 0
                        ? (true, "Enabled Windows user login startup.")
                        : (false, $"Failed to write Windows registry Run key: {err}");
                }
                else
                {
                    RunSync("reg", [@"delete", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", WindowsRunValueName, "/f"]);
                    return (true, "Disabled Windows user login startup.");
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var plistPath = GetMacLaunchAgentPath();
                if (enable)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);
                    var plist = $"""
                        <?xml version="1.0" encoding="UTF-8"?>
                        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                        <plist version="1.0">
                        <dict>
                            <key>Label</key>
                            <string>{MacAgentLabel}</string>
                            <key>ProgramArguments</key>
                            <array>
                                <string>{daemonExecutablePath}</string>
                            </array>
                            <key>RunAtLoad</key>
                            <true/>
                            <key>KeepAlive</key>
                            <true/>
                        </dict>
                        </plist>
                        """;
                    File.WriteAllText(plistPath, plist);
                    RunSync("launchctl", ["load", "-w", plistPath]);
                    return (true, $"Installed macOS LaunchAgent at {plistPath}");
                }
                else
                {
                    if (File.Exists(plistPath))
                    {
                        RunSync("launchctl", ["unload", "-w", plistPath]);
                        File.Delete(plistPath);
                    }

                    return (true, "Removed macOS LaunchAgent.");
                }
            }

            // Linux systemd --user
            var unitPath = GetLinuxSystemdUnitPath();
            if (enable)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(unitPath)!);
                string execLine = daemonExecutablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? $"\"{Environment.ProcessPath ?? "dotnet"}\" \"{daemonExecutablePath}\""
                    : $"\"{daemonExecutablePath}\"";

                var unit = $"""
                    [Unit]
                    Description=NVMe Sentinel Health & TBW Telemetry Daemon
                    After=default.target

                    [Service]
                    Type=simple
                    ExecStart={execLine}
                    Restart=on-failure
                    RestartSec=5

                    [Install]
                    WantedBy=default.target
                    """;
                File.WriteAllText(unitPath, unit);
                RunSync("systemctl", ["--user", "daemon-reload"]);
                var (code, outMsg) = RunSync("systemctl", ["--user", "enable", ServiceUnitName]);
                return code == 0
                    ? (true, $"Enabled systemd user service ({unitPath}).")
                    : (false, $"Created unit file, but systemctl enable reported: {outMsg}");
            }
            else
            {
                RunSync("systemctl", ["--user", "disable", ServiceUnitName]);
                if (File.Exists(unitPath))
                {
                    File.Delete(unitPath);
                }

                RunSync("systemctl", ["--user", "daemon-reload"]);
                return (true, "Disabled and removed systemd user service.");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string GetLinuxSystemdUnitPath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = !string.IsNullOrWhiteSpace(configHome)
            ? configHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(baseDir, "systemd", "user", ServiceUnitName);
    }

    private static string GetMacLaunchAgentPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "LaunchAgents",
            $"{MacAgentLabel}.plist");

    private static (int ExitCode, string Output) RunSync(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi);
        if (proc is null) return (-1, "Failed to start process");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(8_000);
        return (proc.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
    }
}
