using System.Diagnostics;
using System.Runtime.InteropServices;
using NvmeSentinel.Core.Ipc;

namespace NvmeSentinel.App.Services;

public static class DaemonProcessLauncher
{
    public static async Task<bool> EnsureDaemonRunningAsync(SentinelIpcClient client, CancellationToken ct = default)
    {
        if (await client.PingAsync(500, ct))
        {
            return true;
        }

        if (!TrySpawnDaemonProcess())
        {
            return false;
        }

        // Wait up to 5 seconds for daemon IPC socket to come online
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(250, ct);
            if (await client.PingAsync(400, ct))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySpawnDaemonProcess()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "nvme-sentinel-daemon.exe"
                : "nvme-sentinel-daemon";

            // Search candidate paths (published side-by-side binary, or sibling project build output during development)
            var candidates = new List<string>
            {
                Path.Combine(baseDir, exeName),
                Path.Combine(baseDir, "nvme-sentinel-daemon.dll")
            };

            // Sibling build folder when running via `dotnet run`
            var devDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "NvmeSentinel.Daemon", "bin"));
            if (Directory.Exists(devDir))
            {
                candidates.AddRange(Directory.GetFiles(devDir, exeName, SearchOption.AllDirectories));
                candidates.AddRange(Directory.GetFiles(devDir, "nvme-sentinel-daemon.dll", SearchOption.AllDirectories));
            }

            var target = candidates.FirstOrDefault(File.Exists);
            if (string.IsNullOrEmpty(target))
            {
                return false;
            }

            ProcessStartInfo psi;
            if (target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };
                psi.ArgumentList.Add(target);
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };
            }

            var proc = Process.Start(psi);
            return proc is not null;
        }
        catch
        {
            return false;
        }
    }
}
