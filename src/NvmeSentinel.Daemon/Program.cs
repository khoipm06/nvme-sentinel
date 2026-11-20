using System.Text.Json;
using NvmeSentinel.Core.Ipc;
using NvmeSentinel.Core.Models;
using NvmeSentinel.Daemon;

var argsSet = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);

if (argsSet.Contains("--help") || argsSet.Contains("-h"))
{
    Console.WriteLine("""
        NVMe Sentinel Background Telemetry Daemon
        Usage:
          nvme-sentinel-daemon              Run background daemon & IPC server
          nvme-sentinel-daemon --status     Print running daemon status as JSON
          nvme-sentinel-daemon --once       Collect a one-shot hardware snapshot as JSON
          nvme-sentinel-daemon --stop       Gracefully stop a running daemon instance
        """);
    return 0;
}

var ipcClient = new SentinelIpcClient();

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
