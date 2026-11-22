using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Hardware.Windows;

public sealed class WindowsNvmeProvider : INvmeProvider
{
    public string BackendName => "Windows Storage IOCTL + CIM NVMe";

    private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlDiskPerformance = 0x00070020;
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    public async Task<List<NvmeDeviceInfo>> DiscoverDevicesAsync(CancellationToken ct = default)
    {
        var devices = new List<NvmeDeviceInfo>();

        var psScript = "Get-PhysicalDisk | Where-Object { $_.BusType -eq 'NVMe' } | Select-Object DeviceId,FriendlyName,SerialNumber,FirmwareVersion,Size,HealthStatus | ConvertTo-Json -Compress";
        var json = await RunPowerShellJsonAsync(psScript, ct);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var items = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().ToList()
                    : [doc.RootElement];

                foreach (var item in items)
                {
                    var diskNum = item.TryGetProperty("DeviceId", out var did) ? did.ToString() : "0";
                    var model = item.TryGetProperty("FriendlyName", out var fn) ? fn.GetString() ?? "NVMe SSD" : "NVMe SSD";
                    var serial = item.TryGetProperty("SerialNumber", out var sn) ? (sn.GetString() ?? diskNum).Trim() : diskNum;
                    var fw = item.TryGetProperty("FirmwareVersion", out var fv) ? (fv.GetString() ?? "").Trim() : "";
                    long size = item.TryGetProperty("Size", out var sz) && sz.TryGetInt64(out var cap) ? cap : 0;

                    devices.Add(new NvmeDeviceInfo
                    {
                        DeviceId = SmartctlFallbackParser.SanitizeId($"{model}_{serial}"),
                        ControllerName = $"PhysicalDrive{diskNum}",
                        BlockDeviceName = diskNum,
                        DevicePath = $@"\\.\PhysicalDrive{diskNum}",
                        Model = model,
                        SerialNumber = serial,
                        FirmwareRevision = fw,
                        CapacityBytes = size,
                        PowerState = "Active",
                        BackendSource = "Windows Storage API"
                    });
                }
            }
            catch
            {
                // Fallback to smartctl below
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
            PowerState = "Active"
        };

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || string.IsNullOrWhiteSpace(device.DevicePath))
        {
            return counters;
        }

        IntPtr handle = CreateFileW(device.DevicePath, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return counters;
        }

        try
        {
            var perf = new DiskPerformanceNative();
            int size = Marshal.SizeOf<DiskPerformanceNative>();
            if (DeviceIoControlDiskPerf(handle, IoctlDiskPerformance, IntPtr.Zero, 0, ref perf, size, out _, IntPtr.Zero))
            {
                counters.TotalBytesRead = perf.BytesRead;
                counters.TotalBytesWritten = perf.BytesWritten;
                counters.ReadCompletedOps = perf.ReadCount;
                counters.WriteCompletedOps = perf.WriteCount;
            }
        }
        finally
        {
            CloseHandle(handle);
        }

        return counters;
    }

    public async Task<SmartSnapshot?> ReadSmartAsync(NvmeDeviceInfo device, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var ioctlSnap = TryReadNvmeHealthLogIoctl(device);
            if (ioctlSnap is not null)
            {
                return ioctlSnap;
            }

            // Try WMI StorageReliabilityCounter fallback
            var cimSnap = await TryReadReliabilityCounterCimAsync(device, ct);
            if (cimSnap is not null)
            {
                return cimSnap;
            }
        }

        return await SmartctlFallbackParser.ReadSmartAsync(device, ct);
    }

    public async Task<(bool Ok, string Message)> StartSelfTestAsync(NvmeDeviceInfo device, string testType, CancellationToken ct = default)
    {
        var smartctlType = testType.Equals("extended", StringComparison.OrdinalIgnoreCase) ? "long" : "short";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "smartctl",
                Arguments = $"-t {smartctlType} \"{device.DevicePath}\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, "Could not launch elevated smartctl process.");
            }

            try
            {
                await proc.WaitForExitAsync(ct);
                return proc.ExitCode == 0
                    ? (true, $"Started NVMe {testType} self-test.")
                    : (false, $"smartctl exited with status {proc.ExitCode}. Ensure smartmontools is installed.");
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
        catch (Exception ex)
        {
            return (false, $"Elevation canceled or smartctl unavailable: {ex.Message}");
        }
    }

    private static SmartSnapshot? TryReadNvmeHealthLogIoctl(NvmeDeviceInfo device)
    {
        // Query StorageAdapterProtocolSpecificProperty (PropertyId = 49) for NVMe LogPage 0x02 (Health Info, 512 bytes)
        const uint GenericRead = 0x80000000;
        IntPtr handle = CreateFileW(device.DevicePath, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            handle = CreateFileW(device.DevicePath, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        }

        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            const int headerOffset = 48; // STORAGE_PROPERTY_QUERY (8) + STORAGE_PROTOCOL_SPECIFIC_DATA (40)
            const int logSize = 512;
            const int bufferSize = headerOffset + logSize;
            byte[] buffer = new byte[bufferSize];

            // PropertyId = 49 (StorageAdapterProtocolSpecificProperty), QueryType = 0 (PropertyStandardQuery)
            BitConverter.GetBytes(49).CopyTo(buffer, 0);
            BitConverter.GetBytes(0).CopyTo(buffer, 4);
            // ProtocolType = 3 (ProtocolTypeNvme), DataType = 2 (NVMeDataTypeLogPage)
            BitConverter.GetBytes(3).CopyTo(buffer, 8);
            BitConverter.GetBytes(2).CopyTo(buffer, 12);
            // ProtocolDataRequestValue = 2 (NVME_LOG_PAGE_HEALTH_INFO)
            BitConverter.GetBytes(2).CopyTo(buffer, 16);
            BitConverter.GetBytes(0).CopyTo(buffer, 20);
            // ProtocolDataOffset = 40 (sizeof STORAGE_PROTOCOL_SPECIFIC_DATA), ProtocolDataLength = 512
            BitConverter.GetBytes(40).CopyTo(buffer, 24);
            BitConverter.GetBytes(logSize).CopyTo(buffer, 28);

            if (!DeviceIoControlBytes(handle, IoctlStorageQueryProperty, buffer, bufferSize, buffer, bufferSize, out int returned, IntPtr.Zero) ||
                returned < headerOffset + 192)
            {
                return null;
            }

            // Parse NVMe 512-byte SMART / Health Information Log (NVMe Base Spec Figure 207)
            var log = buffer.AsSpan(headerOffset, logSize);
            byte critWarn = log[0];
            ushort compositeTempK = BitConverter.ToUInt16(log.Slice(1, 2));
            byte availSpare = log[3];
            byte spareThresh = log[4];
            byte percentUsed = log[5];

            // NVMe spec: Data Units Read/Written are 128-bit unsigned integers (16 bytes) at offsets 32 and 48
            UInt128 dataUnitsRead = System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(log.Slice(32, 16));
            UInt128 dataUnitsWritten = System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(log.Slice(48, 16));
            ulong ctrlBusy = BitConverter.ToUInt64(log.Slice(96, 8));
            ulong powerCycles = BitConverter.ToUInt64(log.Slice(112, 8));
            ulong powerOnHours = BitConverter.ToUInt64(log.Slice(128, 8));
            ulong unsafeShutdowns = BitConverter.ToUInt64(log.Slice(144, 8));
            ulong mediaErrors = BitConverter.ToUInt64(log.Slice(160, 8));
            ulong errorLogEntries = BitConverter.ToUInt64(log.Slice(176, 8));
            uint warnTempTime = BitConverter.ToUInt32(log.Slice(192, 4));
            uint critTempTime = BitConverter.ToUInt32(log.Slice(196, 4));

            UInt128 bytesRead128 = dataUnitsRead * (UInt128)NvmeSentinel.Core.Common.NvmeConstants.BytesPerDataUnit;
            UInt128 bytesWritten128 = dataUnitsWritten * (UInt128)NvmeSentinel.Core.Common.NvmeConstants.BytesPerDataUnit;
            long totalBytesRead = bytesRead128 > (UInt128)long.MaxValue ? long.MaxValue : (long)bytesRead128;
            long totalBytesWritten = bytesWritten128 > (UInt128)long.MaxValue ? long.MaxValue : (long)bytesWritten128;

            var snap = new SmartSnapshot
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                DeviceId = device.DeviceId,
                CriticalWarningFlags = critWarn,
                CompositeTempCelsius = compositeTempK > 233 ? Math.Round(compositeTempK - 273.15, 1) : 0,
                AvailableSpare = availSpare,
                SpareThreshold = spareThresh,
                PercentUsed = percentUsed,
                TotalBytesRead = totalBytesRead,
                TotalBytesWritten = totalBytesWritten,
                ControllerBusyMinutes = (long)ctrlBusy,
                PowerCycles = (long)powerCycles,
                PowerOnHours = (long)powerOnHours,
                UnsafeShutdowns = (long)unsafeShutdowns,
                MediaErrors = (long)mediaErrors,
                ErrorLogEntries = (long)errorLogEntries,
                WarningTempMinutes = warnTempTime,
                CriticalTempMinutes = critTempTime
            };

            snap.Sensors.Add(new SensorReading { Label = "Composite", Celsius = snap.CompositeTempCelsius });
            for (int i = 0; i < 8; i++)
            {
                ushort sK = BitConverter.ToUInt16(log.Slice(200 + i * 2, 2));
                if (sK > 233 && sK < 473)
                {
                    snap.Sensors.Add(new SensorReading { Label = $"Sensor {i + 1}", Celsius = Math.Round(sK - 273.15, 1) });
                }
            }

            return snap;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static async Task<SmartSnapshot?> TryReadReliabilityCounterCimAsync(NvmeDeviceInfo device, CancellationToken ct)
    {
        if (!int.TryParse(device.BlockDeviceName, out var diskNum))
        {
            return null;
        }

        var ps = $"Get-PhysicalDisk -DeviceNumber {diskNum} | Get-StorageReliabilityCounter | Select-Object Temperature,Wear,PowerOnHours,StartStopCycleCount,ReadErrorsTotal,WriteErrorsTotal | ConvertTo-Json -Compress";
        var json = await RunPowerShellJsonAsync(ps, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            double temp = root.TryGetProperty("Temperature", out var t) && t.TryGetDouble(out var tv) ? tv : 0;
            int wear = root.TryGetProperty("Wear", out var w) && w.TryGetInt32(out var wv) ? wv : 0;
            long poh = root.TryGetProperty("PowerOnHours", out var p) && p.TryGetInt64(out var pv) ? pv : 0;

            var snap = new SmartSnapshot
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                DeviceId = device.DeviceId,
                CompositeTempCelsius = temp,
                PercentUsed = wear,
                PowerOnHours = poh
            };
            if (temp > 0)
            {
                snap.Sensors.Add(new SensorReading { Label = "Composite", Celsius = temp });
            }

            return snap;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> RunPowerShellJsonAsync(string command, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DiskPerformanceNative
    {
        public long BytesRead;
        public long BytesWritten;
        public long ReadTime;
        public long WriteTime;
        public long IdleTime;
        public uint ReadCount;
        public uint WriteCount;
        public uint QueueDepth;
        public uint SplitCount;
        public long QueryTime;
        public uint StorageDeviceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
        public string StorageManagerName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlDiskPerf(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        ref DiskPerformanceNative lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlBytes(
        IntPtr hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
