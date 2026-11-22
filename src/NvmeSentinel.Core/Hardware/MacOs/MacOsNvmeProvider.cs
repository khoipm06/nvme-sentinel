using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Hardware.MacOs;

public sealed class MacOsNvmeProvider : INvmeProvider
{
    private static readonly Regex SafeBsdDiskRegex = new(@"^/dev/disk\d+(s\d+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public string BackendName => "macOS SPNVMe + IOKit / smartctl";

    public async Task<List<NvmeDeviceInfo>> DiscoverDevicesAsync(CancellationToken ct = default)
    {
        var devices = new List<NvmeDeviceInfo>();
        var json = await RunCaptureAsync("system_profiler", ["SPNVMeDataType", "-json"], ct);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("SPNVMeDataType", out var controllers) && controllers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ctrl in controllers.EnumerateArray())
                    {
                        if (!ctrl.TryGetProperty("_items", out var items) || items.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var item in items.EnumerateArray())
                        {
                            var model = GetStr(item, "device_model", "Apple NVMe SSD");
                            var serial = GetStr(item, "device_serial", "0");
                            var rev = GetStr(item, "device_revision", "");
                            var bsdName = GetStr(item, "bsd_name", "disk0");
                            var linkWidth = GetStr(item, "spnvme_linkwidth", "x4");
                            var linkSpeed = GetStr(item, "spnvme_linkspeed", "");
                            long sizeBytes = item.TryGetProperty("size_in_bytes", out var sb) && sb.TryGetInt64(out var cap) ? cap : 0;

                            int width = int.TryParse(linkWidth.TrimStart('x', 'X'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : 4;

                            devices.Add(new NvmeDeviceInfo
                            {
                                DeviceId = SmartctlFallbackParser.SanitizeId($"{model}_{serial}"),
                                ControllerName = bsdName,
                                BlockDeviceName = bsdName,
                                DevicePath = $"/dev/{bsdName}",
                                Model = model,
                                SerialNumber = serial,
                                FirmwareRevision = rev,
                                CapacityBytes = sizeBytes,
                                PcieCurrentLinkSpeed = linkSpeed,
                                PcieMaxLinkSpeed = linkSpeed,
                                PcieCurrentLinkWidth = width,
                                PcieMaxLinkWidth = width,
                                PowerState = "Active",
                                BackendSource = "macOS IOKit / SPNVMe"
                            });
                        }
                    }
                }
            }
            catch
            {
                // Fallback below
            }
        }

        if (devices.Count == 0)
        {
            devices = await SmartctlFallbackParser.ScanDevicesAsync(ct);
        }

        return devices;
    }

    public BlockIoCounters ReadLiveBlockCounters(NvmeDeviceInfo device)
    {
        var counters = new BlockIoCounters
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            PowerState = "Active",
            PcieCurrentLinkSpeed = device.PcieCurrentLinkSpeed,
            PcieCurrentLinkWidth = device.PcieCurrentLinkWidth
        };

        try
        {
            // iostat -Id <disk0> prints total KB/t, xfrs, MB transferred
            var psi = new ProcessStartInfo
            {
                FileName = "iostat",
                Arguments = $"-Id {device.BlockDeviceName}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var text = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(2_000);
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length >= 3)
                {
                    var parts = lines[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 &&
                        long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var xfrs) &&
                        double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mb))
                    {
                        counters.WriteCompletedOps = xfrs;
                        counters.TotalBytesWritten = (long)(mb * 1_048_576.0);
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }

        return counters;
    }

    public async Task<SmartSnapshot?> ReadSmartAsync(NvmeDeviceInfo device, bool forceRefresh = false, CancellationToken ct = default)
    {
        return await SmartctlFallbackParser.ReadSmartAsync(device, ct);
    }

    public async Task<(bool Ok, string Message)> StartSelfTestAsync(NvmeDeviceInfo device, string testType, CancellationToken ct = default)
    {
        var path = device.DevicePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !SafeBsdDiskRegex.IsMatch(path))
        {
            return (false, $"Invalid device path format '{device.DevicePath}'. Self-test rejected for system safety.");
        }

        var smartType = testType.Equals("extended", StringComparison.OrdinalIgnoreCase) ? "long" : "short";
        var script = $"do shell script \"smartctl -t {smartType} {path}\" with administrator privileges";
        var outStr = await RunCaptureAsync("osascript", ["-e", script], ct);
        return outStr is not null
            ? (true, $"Started NVMe {testType} self-test.")
            : (false, "Administrator authorization canceled or smartctl is not installed.");
    }

    private static string GetStr(JsonElement el, string prop, string fallback) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static async Task<string?> RunCaptureAsync(string file, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            try
            {
                var stdout = await p.StandardOutput.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                return p.ExitCode == 0 ? stdout : null;
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
