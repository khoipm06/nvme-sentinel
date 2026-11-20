using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Hardware.Linux;

public sealed class LinuxNvmeProvider : INvmeProvider
{
    public string BackendName => "Linux Sysfs + UDisks2 NVMe";

    private static readonly ConcurrentDictionary<string, List<(string InputPath, string Label)>> _hwmonSensorCache = new(StringComparer.Ordinal);
    private DateTimeOffset _lastUdisksSmartRefresh = DateTimeOffset.MinValue;

    public async Task<List<NvmeDeviceInfo>> DiscoverDevicesAsync(CancellationToken ct = default)
    {
        var devices = new List<NvmeDeviceInfo>();
        var udisksMap = await QueryUdisksControllersAsync(ct);

        const string nvmeClassDir = "/sys/class/nvme";
        if (Directory.Exists(nvmeClassDir))
        {
            foreach (var ctrlDir in Directory.GetDirectories(nvmeClassDir, "nvme*").OrderBy(p => p, StringComparer.Ordinal))
            {
                var ctrlName = Path.GetFileName(ctrlDir);
                var model = ReadSysfsTrimmed(Path.Combine(ctrlDir, "model")) ?? "NVMe SSD";
                var serial = ReadSysfsTrimmed(Path.Combine(ctrlDir, "serial")) ?? ctrlName;
                var firmware = ReadSysfsTrimmed(Path.Combine(ctrlDir, "firmware_rev")) ?? "";
                var state = ReadSysfsTrimmed(Path.Combine(ctrlDir, "state")) ?? "live";

                var deviceLink = Path.Combine(ctrlDir, "device");
                var curSpeed = ReadSysfsTrimmed(Path.Combine(deviceLink, "current_link_speed")) ?? "";
                var maxSpeed = ReadSysfsTrimmed(Path.Combine(deviceLink, "max_link_speed")) ?? "";
                var curWidth = ParseIntSafe(ReadSysfsTrimmed(Path.Combine(deviceLink, "current_link_width")));
                var maxWidth = ParseIntSafe(ReadSysfsTrimmed(Path.Combine(deviceLink, "max_link_width")));

                // Locate primary namespace (e.g. nvme0n1)
                var nsDirs = Directory.GetDirectories(ctrlDir, $"{ctrlName}n*")
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
                var blockName = nsDirs.Count > 0 ? Path.GetFileName(nsDirs[0]) : $"{ctrlName}n1";
                long capacityBytes = ReadBlockCapacityBytes(blockName);

                // Match UDisks2 object path by serial or model
                var udisksPath = MatchUdisksObjectPath(udisksMap, serial, model);
                if (capacityBytes <= 0 && !string.IsNullOrEmpty(udisksPath) && udisksMap.TryGetValue(udisksPath, out var uInfo))
                {
                    capacityBytes = uInfo.Size;
                }

                var deviceId = SmartctlFallbackParser.SanitizeId($"{model}_{serial}");
                devices.Add(new NvmeDeviceInfo
                {
                    DeviceId = deviceId,
                    ControllerName = ctrlName,
                    BlockDeviceName = blockName,
                    DevicePath = $"/dev/{ctrlName}",
                    UdisksObjectPath = udisksPath,
                    Model = model,
                    SerialNumber = serial,
                    FirmwareRevision = firmware,
                    CapacityBytes = capacityBytes,
                    PcieCurrentLinkSpeed = curSpeed,
                    PcieMaxLinkSpeed = maxSpeed,
                    PcieCurrentLinkWidth = curWidth,
                    PcieMaxLinkWidth = maxWidth,
                    PowerState = FormatPowerState(ctrlDir, state),
                    BackendSource = !string.IsNullOrEmpty(udisksPath) ? "UDisks2 + Sysfs" : "Sysfs + smartctl"
                });
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
            TimestampUtc = DateTimeOffset.UtcNow
        };

        var statPath = $"/sys/block/{device.BlockDeviceName}/stat";
        if (File.Exists(statPath))
        {
            var raw = ReadSysfsTrimmed(statPath);
            if (!string.IsNullOrEmpty(raw))
            {
                var span = raw.AsSpan();
                int field = 0;
                while (!span.IsEmpty)
                {
                    int start = 0;
                    while (start < span.Length && span[start] == ' ') start++;
                    if (start >= span.Length) break;

                    span = span[start..];
                    int spaceIdx = span.IndexOf(' ');
                    var token = spaceIdx >= 0 ? span[..spaceIdx] : span;
                    span = spaceIdx >= 0 ? span[spaceIdx..] : ReadOnlySpan<char>.Empty;

                    if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val))
                    {
                        switch (field)
                        {
                            case 0: counters.ReadCompletedOps = val; break;
                            case 2: counters.TotalBytesRead = val * 512L; break;
                            case 4: counters.WriteCompletedOps = val; break;
                            case 6: counters.TotalBytesWritten = val * 512L; break;
                        }
                    }

                    field++;
                    if (field > 6) break;
                }
            }
        }

        var ctrlDir = $"/sys/class/nvme/{device.ControllerName}";
        if (Directory.Exists(ctrlDir))
        {
            counters.Sensors = ReadHwmonSensors(ctrlDir);
            if (counters.Sensors.Count > 0)
            {
                counters.CompositeTempCelsius = counters.Sensors[0].Celsius;
            }

            var deviceLink = Path.Combine(ctrlDir, "device");
            counters.PcieCurrentLinkSpeed = ReadSysfsTrimmed(Path.Combine(deviceLink, "current_link_speed")) ?? device.PcieCurrentLinkSpeed;
            counters.PcieCurrentLinkWidth = ParseIntSafe(ReadSysfsTrimmed(Path.Combine(deviceLink, "current_link_width")), device.PcieCurrentLinkWidth);
            var state = ReadSysfsTrimmed(Path.Combine(ctrlDir, "state")) ?? "live";
            counters.PowerState = FormatPowerState(ctrlDir, state);
        }

        return counters;
    }

    public async Task<SmartSnapshot?> ReadSmartAsync(NvmeDeviceInfo device, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(device.UdisksObjectPath))
        {
            var now = DateTimeOffset.UtcNow;
            if (forceRefresh || (now - _lastUdisksSmartRefresh).TotalSeconds >= 55)
            {
                await RunBusctlAsync(
                    ["--system", "call", "org.freedesktop.UDisks2", device.UdisksObjectPath, "org.freedesktop.UDisks2.NVMe.Controller", "SmartUpdate", "a{sv}", "0"],
                    ct);
                _lastUdisksSmartRefresh = now;
            }

            var snap = await ReadUdisksSmartAsync(device, ct);
            if (snap is not null)
            {
                // Prefer high-resolution hwmon labeled sensors when available
                var hwmonSensors = ReadHwmonSensors($"/sys/class/nvme/{device.ControllerName}");
                if (hwmonSensors.Count > 0)
                {
                    snap.Sensors = hwmonSensors;
                    snap.CompositeTempCelsius = hwmonSensors[0].Celsius;
                }

                return snap;
            }
        }

        return await SmartctlFallbackParser.ReadSmartAsync(device, ct);
    }

    public async Task<(bool Ok, string Message)> StartSelfTestAsync(NvmeDeviceInfo device, string testType, CancellationToken ct = default)
    {
        var normalized = testType.Equals("extended", StringComparison.OrdinalIgnoreCase) ? "extended" : "short";

        if (!string.IsNullOrWhiteSpace(device.UdisksObjectPath))
        {
            var (exitCode, _, stderr) = await RunProcessCaptureAsync(
                "busctl",
                [
                    "--system", "call",
                    "org.freedesktop.UDisks2",
                    device.UdisksObjectPath,
                    "org.freedesktop.UDisks2.NVMe.Controller",
                    "SmartSelftestStart",
                    "sa{sv}",
                    normalized,
                    "0"
                ],
                ct);

            if (exitCode == 0)
            {
                return (true, $"Started NVMe {normalized} self-test via UDisks2.");
            }

            if (!string.IsNullOrWhiteSpace(stderr) &&
                (stderr.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase) ||
                 stderr.Contains("dismissed", StringComparison.OrdinalIgnoreCase)))
            {
                return (false, "Authentication was canceled or denied by Polkit.");
            }
        }

        // Fallback to pkexec smartctl
        var smartctlType = normalized == "extended" ? "long" : "short";
        var (pkCode, _, pkErr) = await RunProcessCaptureAsync(
            "pkexec",
            ["smartctl", "-t", smartctlType, device.DevicePath],
            ct);

        return pkCode == 0
            ? (true, $"Started NVMe {normalized} self-test via smartctl.")
            : (false, string.IsNullOrWhiteSpace(pkErr) ? "Failed to start NVMe self-test." : pkErr.Trim());
    }

    private static async Task<SmartSnapshot?> ReadUdisksSmartAsync(NvmeDeviceInfo device, CancellationToken ct)
    {
        var attrJson = await RunBusctlAsync(
            [
                "--system", "--json=short", "call",
                "org.freedesktop.UDisks2",
                device.UdisksObjectPath,
                "org.freedesktop.UDisks2.NVMe.Controller",
                "SmartGetAttributes",
                "a{sv}", "0"
            ],
            ct);

        if (string.IsNullOrWhiteSpace(attrJson))
        {
            return null;
        }

        var propsJson = await RunBusctlAsync(
            [
                "--system", "--json=short", "call",
                "org.freedesktop.UDisks2",
                device.UdisksObjectPath,
                "org.freedesktop.DBus.Properties",
                "GetAll",
                "s", "org.freedesktop.UDisks2.NVMe.Controller"
            ],
            ct);

        try
        {
            using var attrDoc = JsonDocument.Parse(attrJson);
            var dataArr = attrDoc.RootElement.GetProperty("data");
            if (dataArr.GetArrayLength() == 0)
            {
                return null;
            }

            var attrs = dataArr[0];
            var snap = new SmartSnapshot
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                DeviceId = device.DeviceId,
                AvailableSpare = (int)ReadVariantLong(attrs, "avail_spare", 100),
                SpareThreshold = (int)ReadVariantLong(attrs, "spare_thresh", 10),
                PercentUsed = (int)ReadVariantLong(attrs, "percent_used", 0),
                TotalBytesRead = ReadVariantLong(attrs, "total_data_read", 0),
                TotalBytesWritten = ReadVariantLong(attrs, "total_data_written", 0),
                ControllerBusyMinutes = ReadVariantLong(attrs, "ctrl_busy_time", 0),
                PowerCycles = ReadVariantLong(attrs, "power_cycles", 0),
                UnsafeShutdowns = ReadVariantLong(attrs, "unsafe_shutdowns", 0),
                MediaErrors = ReadVariantLong(attrs, "media_errors", 0),
                ErrorLogEntries = ReadVariantLong(attrs, "num_err_log_entries", 0),
                WarningTempMinutes = ReadVariantLong(attrs, "warning_temp_time", 0),
                CriticalTempMinutes = ReadVariantLong(attrs, "critical_temp_time", 0)
            };

            var wcKelvin = ReadVariantLong(attrs, "wctemp", 0);
            var ccKelvin = ReadVariantLong(attrs, "cctemp", 0);
            if (wcKelvin > 273) snap.WarningTempCelsius = Math.Round(wcKelvin - 273.15, 1);
            if (ccKelvin > 273) snap.CriticalTempCelsius = Math.Round(ccKelvin - 273.15, 1);

            if (attrs.TryGetProperty("temp_sensors", out var tsWrap) &&
                tsWrap.TryGetProperty("data", out var tsArr) &&
                tsArr.ValueKind == JsonValueKind.Array)
            {
                int idx = 1;
                foreach (var kEl in tsArr.EnumerateArray())
                {
                    if (kEl.TryGetInt64(out var kelvin) && kelvin > 233 && kelvin < 473)
                    {
                        snap.Sensors.Add(new SensorReading
                        {
                            Label = $"Sensor {idx}",
                            Celsius = Math.Round(kelvin - 273.15, 1)
                        });
                    }
                    idx++;
                }
            }

            if (!string.IsNullOrWhiteSpace(propsJson))
            {
                using var propsDoc = JsonDocument.Parse(propsJson);
                var pData = propsDoc.RootElement.GetProperty("data");
                if (pData.GetArrayLength() > 0)
                {
                    var pObj = pData[0];
                    snap.PowerOnHours = ReadVariantLong(pObj, "SmartPowerOnHours", 0);
                    var smartTempK = ReadVariantLong(pObj, "SmartTemperature", 0);
                    if (smartTempK > 233)
                    {
                        snap.CompositeTempCelsius = Math.Round(smartTempK - 273.15, 1);
                    }

                    snap.SelfTestStatus = ReadVariantString(pObj, "SmartSelftestStatus", "unknown");
                    snap.SelfTestPercentRemaining = (int)ReadVariantLong(pObj, "SmartSelftestPercentRemaining", -1);

                    if (pObj.TryGetProperty("SmartCriticalWarning", out var cwWrap) &&
                        cwWrap.TryGetProperty("data", out var cwArr) &&
                        cwArr.ValueKind == JsonValueKind.Array &&
                        cwArr.GetArrayLength() > 0)
                    {
                        snap.CriticalWarningFlags = 1;
                    }
                }
            }

            if (snap.CompositeTempCelsius <= 0 && snap.Sensors.Count > 0)
            {
                snap.CompositeTempCelsius = snap.Sensors[0].Celsius;
            }

            return snap;
        }
        catch
        {
            return null;
        }
    }

    private sealed record UdisksDriveMeta(string ObjectPath, string Model, string Serial, long Size);

    private static async Task<Dictionary<string, UdisksDriveMeta>> QueryUdisksControllersAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, UdisksDriveMeta>(StringComparer.Ordinal);
        var json = await RunBusctlAsync(
            [
                "--system", "--json=short", "call",
                "org.freedesktop.UDisks2",
                "/org/freedesktop/UDisks2",
                "org.freedesktop.DBus.ObjectManager",
                "GetManagedObjects"
            ],
            ct);

        if (string.IsNullOrWhiteSpace(json))
        {
            return map;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0)
            {
                return map;
            }

            foreach (var prop in data[0].EnumerateObject())
            {
                var objPath = prop.Name;
                var interfaces = prop.Value;
                if (!interfaces.TryGetProperty("org.freedesktop.UDisks2.NVMe.Controller", out _))
                {
                    continue;
                }

                string model = "";
                string serial = "";
                long size = 0;
                if (interfaces.TryGetProperty("org.freedesktop.UDisks2.Drive", out var driveIface))
                {
                    model = ReadVariantString(driveIface, "Model", "");
                    serial = ReadVariantString(driveIface, "Serial", "");
                    size = ReadVariantLong(driveIface, "Size", 0);
                }

                map[objPath] = new UdisksDriveMeta(objPath, model, serial, size);
            }
        }
        catch
        {
            // UDisks2 may be absent on minimal installations
        }

        return map;
    }

    private static string MatchUdisksObjectPath(Dictionary<string, UdisksDriveMeta> map, string serial, string model)
    {
        if (map.Count == 0)
        {
            return string.Empty;
        }

        foreach (var kv in map.Values)
        {
            if (!string.IsNullOrWhiteSpace(serial) &&
                kv.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase))
            {
                return kv.ObjectPath;
            }
        }

        foreach (var kv in map.Values)
        {
            if (!string.IsNullOrWhiteSpace(model) &&
                kv.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
            {
                return kv.ObjectPath;
            }
        }

        return map.Count == 1 ? map.Values.First().ObjectPath : string.Empty;
    }

    private static List<SensorReading> ReadHwmonSensors(string ctrlDir)
    {
        var readings = new List<SensorReading>();
        try
        {
            if (!_hwmonSensorCache.TryGetValue(ctrlDir, out var cachedPaths))
            {
                cachedPaths = [];
                var hwmonDirs = Directory.GetDirectories(ctrlDir, "hwmon*");
                foreach (var hwDir in hwmonDirs)
                {
                    foreach (var inputFile in Directory.GetFiles(hwDir, "temp*_input").OrderBy(f => f, StringComparer.Ordinal))
                    {
                        var labelFile = inputFile.Replace("_input", "_label", StringComparison.Ordinal);
                        var label = ReadSysfsTrimmed(labelFile) ?? Path.GetFileNameWithoutExtension(inputFile);
                        cachedPaths.Add((inputFile, label));
                    }
                }
                _hwmonSensorCache[ctrlDir] = cachedPaths;
            }

            foreach (var (inputFile, label) in cachedPaths)
            {
                var raw = ReadSysfsTrimmed(inputFile);
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliC))
                {
                    var c = Math.Round(milliC / 1000.0, 1);
                    if (c > -40 && c < 180)
                    {
                        readings.Add(new SensorReading { Label = label, Celsius = c });
                    }
                }
            }
        }
        catch
        {
            _hwmonSensorCache.TryRemove(ctrlDir, out _);
        }

        return readings;
    }

    private static long ReadBlockCapacityBytes(string blockName)
    {
        var sizeStr = ReadSysfsTrimmed($"/sys/block/{blockName}/size");
        if (long.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sectors512) && sectors512 > 0)
        {
            return sectors512 * 512L;
        }

        return 0;
    }

    private static string FormatPowerState(string ctrlDir, string state)
    {
        var rtStatus = ReadSysfsTrimmed(Path.Combine(ctrlDir, "power", "runtime_status"));
        if (!string.IsNullOrWhiteSpace(rtStatus) && !rtStatus.Equals("unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return $"{Capitalize(state)} ({rtStatus})";
        }

        return Capitalize(state);
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? "Active" : char.ToUpperInvariant(s[0]) + s[1..];

    private static long ReadVariantLong(JsonElement parent, string key, long fallback)
    {
        if (!parent.TryGetProperty(key, out var wrap) || !wrap.TryGetProperty("data", out var data))
        {
            return fallback;
        }

        if (data.ValueKind == JsonValueKind.Number)
        {
            if (data.TryGetInt64(out var i64)) return i64;
            if (data.TryGetUInt64(out var u64)) return (long)Math.Min(u64, long.MaxValue);
            if (data.TryGetDouble(out var dbl)) return (long)dbl;
        }

        return fallback;
    }

    private static string ReadVariantString(JsonElement parent, string key, string fallback)
    {
        if (!parent.TryGetProperty(key, out var wrap) || !wrap.TryGetProperty("data", out var data))
        {
            return fallback;
        }

        return data.ValueKind == JsonValueKind.String ? data.GetString() ?? fallback : fallback;
    }

    private static string? ReadSysfsTrimmed(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static int ParseIntSafe(string? s, int fallback = 0) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static async Task<string?> RunBusctlAsync(string[] args, CancellationToken ct)
    {
        var (code, stdout, _) = await RunProcessCaptureAsync("busctl", args, ct);
        return code == 0 ? stdout : null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessCaptureAsync(
        string fileName,
        string[] args,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
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
                return (-1, "", "Failed to start process.");
            }

            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, await outTask, await errTask);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
