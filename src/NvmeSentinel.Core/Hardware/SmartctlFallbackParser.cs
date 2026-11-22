using System.Diagnostics;
using System.Text.Json;
using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Hardware;

public static class SmartctlFallbackParser
{
    private const long DataUnitBytes = 512_000L; // NVMe spec: 1,000 * 512B

    public static async Task<List<NvmeDeviceInfo>> ScanDevicesAsync(CancellationToken ct = default)
    {
        var results = new List<NvmeDeviceInfo>();
        var json = await RunSmartctlAsync(["--scan-open", "-j"], ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return results;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            int idx = 0;
            foreach (var dev in devices.EnumerateArray())
            {
                var type = dev.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                var name = dev.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!type.Contains("nvme", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("nvme", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var infoJson = await RunSmartctlAsync(["-i", "-j", name], ct);
                var model = dev.TryGetProperty("info_name", out var infoName) ? infoName.GetString() ?? name : name;
                var serial = $"IDX{idx}";
                var firmware = "";
                long capacity = 0;

                if (!string.IsNullOrWhiteSpace(infoJson))
                {
                    using var infoDoc = JsonDocument.Parse(infoJson);
                    var root = infoDoc.RootElement;
                    if (root.TryGetProperty("model_name", out var mn) && mn.GetString() is { Length: > 0 } sModel)
                    {
                        model = sModel.Trim();
                    }

                    if (root.TryGetProperty("serial_number", out var sn) && sn.GetString() is { Length: > 0 } sSerial)
                    {
                        serial = sSerial.Trim();
                    }

                    if (root.TryGetProperty("firmware_version", out var fw) && fw.GetString() is { Length: > 0 } sFw)
                    {
                        firmware = sFw.Trim();
                    }

                    if (root.TryGetProperty("user_capacity", out var uc) && uc.TryGetProperty("bytes", out var cb) && cb.TryGetInt64(out var cap))
                    {
                        capacity = cap;
                    }
                }

                var cleanId = SanitizeId($"{model}_{serial}");
                results.Add(new NvmeDeviceInfo
                {
                    DeviceId = cleanId,
                    ControllerName = Path.GetFileName(name),
                    BlockDeviceName = Path.GetFileName(name),
                    DevicePath = name,
                    Model = model,
                    SerialNumber = serial,
                    FirmwareRevision = firmware,
                    CapacityBytes = capacity,
                    BackendSource = "smartctl"
                });
                idx++;
            }
        }
        catch
        {
            // Ignore malformed JSON from older smartctl builds
        }

        return results;
    }

    public static async Task<SmartSnapshot?> ReadSmartAsync(NvmeDeviceInfo device, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(device.DevicePath))
        {
            return null;
        }

        var json = await RunSmartctlAsync(["-a", "-j", device.DevicePath], ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return ParseSmartctlJson(device.DeviceId, json);
    }

    public static SmartSnapshot? ParseSmartctlJson(string deviceId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("nvme_smart_health_information_log", out var log))
            {
                return null;
            }

            var snap = new SmartSnapshot
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                DeviceId = deviceId,
                CriticalWarningFlags = GetInt(log, "critical_warning"),
                CompositeTempCelsius = GetDouble(log, "temperature"),
                AvailableSpare = GetInt(log, "available_spare", 100),
                SpareThreshold = GetInt(log, "available_spare_threshold", 10),
                PercentUsed = GetInt(log, "percentage_used"),
                TotalBytesRead = GetLong(log, "data_units_read") * DataUnitBytes,
                TotalBytesWritten = GetLong(log, "data_units_written") * DataUnitBytes,
                ControllerBusyMinutes = GetLong(log, "controller_busy_time"),
                PowerCycles = GetLong(log, "power_cycles"),
                PowerOnHours = GetLong(log, "power_on_hours"),
                UnsafeShutdowns = GetLong(log, "unsafe_shutdowns"),
                MediaErrors = GetLong(log, "media_errors"),
                ErrorLogEntries = GetLong(log, "num_err_log_entries"),
                WarningTempMinutes = GetLong(log, "warning_temp_time"),
                CriticalTempMinutes = GetLong(log, "critical_comp_time")
            };

            snap.Sensors.Add(new SensorReading { Label = "Composite", Celsius = snap.CompositeTempCelsius });
            if (log.TryGetProperty("temperature_sensors", out var sensors) && sensors.ValueKind == JsonValueKind.Array)
            {
                int sIdx = 1;
                foreach (var s in sensors.EnumerateArray())
                {
                    if (s.TryGetDouble(out var tempC) && tempC > -40 && tempC < 200)
                    {
                        snap.Sensors.Add(new SensorReading { Label = $"Sensor {sIdx}", Celsius = tempC });
                    }

                    sIdx++;
                }
            }

            return snap;
        }
        catch
        {
            return null;
        }
    }

    private static int GetInt(JsonElement el, string prop, int defaultValue = 0) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var res) ? res : defaultValue;

    private static long GetLong(JsonElement el, string prop, long defaultValue = 0) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt64(out var res) ? res : defaultValue;

    private static double GetDouble(JsonElement el, string prop, double defaultValue = 0) =>
        el.TryGetProperty(prop, out var v) && v.TryGetDouble(out var res) ? res : defaultValue;

    public static string SanitizeId(string raw)
    {
        var chars = raw.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
        var collapsed = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(collapsed) ? "nvme_default" : collapsed;
    }

    public static async Task<string?> RunSmartctlAsync(string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "smartctl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            try
            {
                var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                return stdout;
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
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
