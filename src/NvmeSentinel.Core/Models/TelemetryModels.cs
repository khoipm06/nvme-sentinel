namespace NvmeSentinel.Core.Models;

public sealed class NvmeDeviceInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public string ControllerName { get; set; } = string.Empty;
    public string BlockDeviceName { get; set; } = string.Empty;
    public string DevicePath { get; set; } = string.Empty;
    public string UdisksObjectPath { get; set; } = string.Empty;
    public string Model { get; set; } = "Unknown NVMe SSD";
    public string SerialNumber { get; set; } = string.Empty;
    public string FirmwareRevision { get; set; } = string.Empty;
    public long CapacityBytes { get; set; }
    public string PcieCurrentLinkSpeed { get; set; } = string.Empty;
    public string PcieMaxLinkSpeed { get; set; } = string.Empty;
    public int PcieCurrentLinkWidth { get; set; }
    public int PcieMaxLinkWidth { get; set; }
    public string PowerState { get; set; } = "Active";
    public string BackendSource { get; set; } = string.Empty;

    public string PcieSummary
    {
        get
        {
            var gen = MapSpeedToPcieGen(PcieCurrentLinkSpeed);
            if (string.IsNullOrWhiteSpace(gen) && PcieCurrentLinkWidth <= 0)
            {
                return "NVMe PCIe";
            }

            var width = PcieCurrentLinkWidth > 0 ? $"x{PcieCurrentLinkWidth}" : "";
            var maxGen = MapSpeedToPcieGen(PcieMaxLinkSpeed);
            var maxWidth = PcieMaxLinkWidth > 0 ? $"x{PcieMaxLinkWidth}" : "";

            if (!string.IsNullOrEmpty(maxGen) && (maxGen != gen || (PcieMaxLinkWidth > 0 && PcieCurrentLinkWidth < PcieMaxLinkWidth)))
            {
                return $"{gen} {width} (Max {maxGen} {maxWidth})".Trim();
            }

            return $"{gen} {width}".Trim();
        }
    }

    public bool IsPcieLinkSuboptimal =>
        (PcieMaxLinkWidth > 0 && PcieCurrentLinkWidth > 0 && PcieCurrentLinkWidth < PcieMaxLinkWidth) ||
        (ParseGtPerSec(PcieMaxLinkSpeed) > 0 && ParseGtPerSec(PcieCurrentLinkSpeed) > 0 &&
         ParseGtPerSec(PcieCurrentLinkSpeed) + 0.1 < ParseGtPerSec(PcieMaxLinkSpeed));

    public static string MapSpeedToPcieGen(string speed)
    {
        var gt = ParseGtPerSec(speed);
        return gt switch
        {
            >= 60.0 => "PCIe 6.0",
            >= 30.0 => "PCIe 5.0",
            >= 15.0 => "PCIe 4.0",
            >= 7.5 => "PCIe 3.0",
            >= 4.5 => "PCIe 2.0",
            >= 2.0 => "PCIe 1.0",
            _ => string.IsNullOrWhiteSpace(speed) ? "" : speed
        };
    }

    public static double ParseGtPerSec(string speed)
    {
        if (string.IsNullOrWhiteSpace(speed))
        {
            return 0;
        }

        var token = speed.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var val)
            ? val
            : 0;
    }
}

public sealed class SensorReading
{
    public string Label { get; set; } = string.Empty;
    public double Celsius { get; set; }
}

public sealed class SmartSnapshot
{
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string DeviceId { get; set; } = string.Empty;
    public long TotalBytesRead { get; set; }
    public long TotalBytesWritten { get; set; }
    public int PercentUsed { get; set; }
    public int AvailableSpare { get; set; } = 100;
    public int SpareThreshold { get; set; } = 10;
    public double CompositeTempCelsius { get; set; }
    public List<SensorReading> Sensors { get; set; } = [];
    public double WarningTempCelsius { get; set; } = 80;
    public double CriticalTempCelsius { get; set; } = 85;
    public long PowerOnHours { get; set; }
    public long PowerCycles { get; set; }
    public long UnsafeShutdowns { get; set; }
    public long MediaErrors { get; set; }
    public long ErrorLogEntries { get; set; }
    public long ControllerBusyMinutes { get; set; }
    public long WarningTempMinutes { get; set; }
    public long CriticalTempMinutes { get; set; }
    public int CriticalWarningFlags { get; set; }
    public string SelfTestStatus { get; set; } = "unknown";
    public int SelfTestPercentRemaining { get; set; } = -1;
    public string TriggerReason { get; set; } = "scheduled";
}

public sealed class ProcessIoEntry
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public double WriteBytesPerSec { get; set; }
    public double ReadBytesPerSec { get; set; }
    public long SessionBytesWritten { get; set; }
    public long SessionBytesRead { get; set; }
}

public sealed class LiveTelemetry
{
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string DeviceId { get; set; } = string.Empty;
    public double ReadBytesPerSec { get; set; }
    public double WriteBytesPerSec { get; set; }
    public double ReadIops { get; set; }
    public double WriteIops { get; set; }
    public double CompositeTempCelsius { get; set; }
    public List<SensorReading> Sensors { get; set; } = [];
    public string PcieCurrentLinkSpeed { get; set; } = string.Empty;
    public int PcieCurrentLinkWidth { get; set; }
    public string PowerState { get; set; } = "Active";
    public long EstimatedCurrentTbwBytes { get; set; }
    public long TodayBytesWritten { get; set; }
    public long TodayBytesRead { get; set; }
    public List<ProcessIoEntry> TopActiveProcesses { get; set; } = [];
}

public sealed class TimeSeriesPoint
{
    public DateTimeOffset TimestampUtc { get; set; }
    public DateTimeOffset EndTimestampUtc { get; set; }
    public long TotalBytesWritten { get; set; }
    public long TotalBytesRead { get; set; }
    public long DeltaBytesWritten { get; set; }
    public long DeltaBytesRead { get; set; }
    public double AvgTempCelsius { get; set; }
    public double MaxTempCelsius { get; set; }
    public int PercentUsed { get; set; }
    public int AvailableSpare { get; set; } = 100;
    public long UnsafeShutdowns { get; set; }
    public long MediaErrors { get; set; }
    public bool IsAnomalySpike { get; set; }
}

public sealed class ProcessWriteSummary
{
    public string ProcessName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public long BytesWritten { get; set; }
    public long BytesRead { get; set; }
    public double SharePercentage { get; set; }
}

public sealed class TimeRangeInspection
{
    public string DeviceId { get; set; } = string.Empty;
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public string Resolution { get; set; } = "raw";
    public long TotalBytesWrittenDelta { get; set; }
    public long TotalBytesReadDelta { get; set; }
    public long StartTbwBytes { get; set; }
    public long EndTbwBytes { get; set; }
    public double PeakTempCelsius { get; set; }
    public double AvgTempCelsius { get; set; }
    public double AvgWriteRateBytesPerSec { get; set; }
    public long PeakBucketWriteBytes { get; set; }
    public List<TimeSeriesPoint> Points { get; set; } = [];
    public List<ProcessWriteSummary> TopProcesses { get; set; } = [];
    public List<AlertEvent> AlertsInWindow { get; set; } = [];
}

public sealed class LifespanForecast
{
    public string DeviceId { get; set; } = string.Empty;
    public long RatedTbwBytes { get; set; }
    public string RatedTbwSource { get; set; } = "Capacity Estimate";
    public long CurrentTbwBytes { get; set; }
    public long RemainingTbwBytes { get; set; }
    public double TbwUsedPercentage { get; set; }
    public int HardwarePercentUsed { get; set; }
    public int HealthScore { get; set; } = 100;
    public double HealthScorePrecise { get; set; } = 100.0;
    public string HealthGrade { get; set; } = "Excellent";
    public double BurnRate24hBytesPerDay { get; set; }
    public double BurnRate7dBytesPerDay { get; set; }
    public double BurnRate30dBytesPerDay { get; set; }
    public double LifetimeAvgBytesPerDay { get; set; }
    public double EffectiveBurnRateBytesPerDay { get; set; }
    public double? EstimatedDaysRemaining { get; set; }
    public DateTimeOffset? EstimatedExhaustionDateUtc { get; set; }
}

public sealed class AlertEvent
{
    public long Id { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string DeviceId { get; set; } = string.Empty;
    public string Severity { get; set; } = "Warning";
    public string Category { get; set; } = "General";
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool Acknowledged { get; set; }
}

public sealed class DaemonSettings
{
    public int FlushIntervalMinutes { get; set; } = 10;
    public int SmartPollIntervalSeconds { get; set; } = 60;
    public double SpikeThresholdGbPer5Min { get; set; } = 5.0;
    public double WarningTempCelsius { get; set; } = 75.0;
    public double CriticalTempCelsius { get; set; } = 83.0;
    public bool EnableDesktopNotifications { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool DarkTheme { get; set; } = true;
    public bool StartDaemonAtLogin { get; set; }
    public bool UseBinaryUnits { get; set; }
    public Dictionary<string, double> CustomRatedTbwTb { get; set; } = new();
}
