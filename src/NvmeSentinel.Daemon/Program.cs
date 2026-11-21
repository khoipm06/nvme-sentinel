using System.Text.Json;
using NvmeSentinel.Core.Ipc;
using NvmeSentinel.Core.Models;
using NvmeSentinel.Daemon;

var argsSet = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);

if (argsSet.Contains("--version") || argsSet.Contains("-v"))
{
    var ver = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    Console.WriteLine($"nvme-sentinel-daemon v{ver}");
    return 0;
}

if (argsSet.Contains("--help") || argsSet.Contains("-h"))
{
    Console.WriteLine("""
        NVMe Sentinel Background Telemetry Daemon
        Usage:
          nvme-sentinel-daemon              Run background daemon & IPC server
          nvme-sentinel-daemon --status     Print running daemon status as JSON
          nvme-sentinel-daemon --waybar     Output status formatted for Waybar / Polybar
          nvme-sentinel-daemon --export     Export telemetry history (csv or json)
          nvme-sentinel-daemon --once       Collect a one-shot hardware snapshot as JSON
          nvme-sentinel-daemon --stop       Gracefully stop a running daemon instance
          nvme-sentinel-daemon --version    Show version information
          nvme-sentinel-daemon --help       Show this help message
        """);
    return 0;
}

var ipcClient = new SentinelIpcClient();

if (argsSet.Contains("--waybar") || argsSet.Contains("--bar"))
{
    if (!await ipcClient.PingAsync(300))
    {
        Console.WriteLine("{\"text\":\"NVMe: off\",\"tooltip\":\"NVMe Sentinel Daemon offline\",\"class\":\"offline\",\"percentage\":0}");
        return 0;
    }

    var resp = await ipcClient.SendAsync(new IpcRequest { Command = "GetDashboard" });
    if (resp.Ok && resp.Dashboard != null)
    {
        var dev = resp.Dashboard.Devices.FirstOrDefault();
        SmartSnapshot? smart = null;
        LiveTelemetry? live = null;
        if (dev != null)
        {
            resp.Dashboard.LatestSmart.TryGetValue(dev.DeviceId, out smart);
            resp.Dashboard.LatestLive.TryGetValue(dev.DeviceId, out live);
        }

        var tempC = smart?.CompositeTempCelsius ?? live?.CompositeTempCelsius ?? 0;
        var healthPct = smart != null ? Math.Max(0, 100 - smart.PercentUsed) : 100.0;
        var writeSpeedMb = (live?.WriteBytesPerSec ?? 0.0) / (1024.0 * 1024.0);
        var tbwTb = smart != null ? smart.TotalBytesWritten / (1000.0 * 1000.0 * 1000.0 * 1000.0) : 0.0;

        var cls = tempC >= 75 ? "critical" : (tempC >= 65 || healthPct < 90 ? "warning" : "healthy");
        var text = $"{tempC:F0}°C {writeSpeedMb:F1}MB/s";
        var tooltip = $"{dev?.Model ?? "NVMe SSD"}\nHealth: {healthPct:F0}%\nTBW: {tbwTb:F2} TB\nTemp: {tempC:F0}°C\nWrites: {writeSpeedMb:F1} MB/s";

        var payload = new
        {
            text = text,
            tooltip = tooltip,
            @class = cls,
            percentage = (int)Math.Clamp(healthPct, 0, 100)
        };
        Console.WriteLine(JsonSerializer.Serialize(payload));
        return 0;
    }

    Console.WriteLine("{\"text\":\"NVMe: ?\",\"tooltip\":\"Connecting...\",\"class\":\"unknown\",\"percentage\":0}");
    return 0;
}

if (argsSet.Contains("--export"))
{
    int idx = Array.FindIndex(args, a => a.Equals("--export", StringComparison.OrdinalIgnoreCase));
    string fmt = idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--") ? args[idx + 1] : "csv";
    string? path = idx >= 0 && idx + 2 < args.Length && !args[idx + 2].StartsWith("--") ? args[idx + 2] : null;

    if (!await ipcClient.PingAsync())
    {
        Console.Error.WriteLine("Error: NVMe Sentinel daemon is not running. Please start the daemon first.");
        return 1;
    }

    var resp = await ipcClient.SendAsync(new IpcRequest
    {
        Command = "ExportData",
        ExportFormat = fmt,
        ExportPath = path
    });

    if (resp.Ok && !string.IsNullOrWhiteSpace(resp.Message))
    {
        Console.WriteLine(resp.Message);
        return 0;
    }

    Console.Error.WriteLine($"Export failed: {resp.Error ?? "Unknown error"}");
    return 1;
}

if (argsSet.Contains("--status"))
{
    if (!await ipcClient.PingAsync())
    {
        Console.WriteLine("{\"running\":false}");
        return 1;
    }

    var resp = await ipcClient.SendAsync(new IpcRequest { Command = "GetStatus" });
    Console.WriteLine(JsonSerializer.Serialize(resp.Status, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (argsSet.Contains("--stop"))
{
    if (!await ipcClient.PingAsync())
    {
        Console.WriteLine("Daemon is not running.");
        return 0;
    }

    await ipcClient.SendAsync(new IpcRequest { Command = "ShutdownDaemon" });
    Console.WriteLine("Daemon shutdown signal sent.");
    return 0;
}

if (argsSet.Contains("--once"))
{
    var provider = NvmeSentinel.Core.Hardware.HardwareFactory.CreateNvmeProvider();
    var devices = await provider.DiscoverDevicesAsync();
    var snapshots = new List<object>();
    foreach (var dev in devices)
    {
        var smart = await provider.ReadSmartAsync(dev, forceRefresh: true);
        var live = provider.ReadLiveBlockCounters(dev);
        snapshots.Add(new { Device = dev, Smart = smart, LiveCounters = live });
    }

    Console.WriteLine(JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

// Single-instance check: if another daemon is already listening on user's IPC pipe, exit cleanly
if (await ipcClient.PingAsync(500))
{
    Console.WriteLine("NVMe Sentinel daemon is already running.");
    return 0;
}

using var shutdownSignal = new ManualResetEventSlim(false);
await using var engine = new SentinelMonitorEngine();

engine.ShutdownRequested += () => shutdownSignal.Set();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdownSignal.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdownSignal.Set();

await engine.InitializeAndStartAsync();
Console.WriteLine($"NVMe Sentinel daemon started (PID {Environment.ProcessId}). Listening for IPC connections...");

shutdownSignal.Wait();
Console.WriteLine("Flushing telemetry buffers and shutting down...");
return 0;
