namespace NvmeSentinel.Core.Models;

public sealed class DaemonStatusInfo
{
    public int Pid { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public double UptimeSeconds { get; set; }
    public long MemoryWorkingSetBytes { get; set; }
    public string DatabasePath { get; set; } = string.Empty;
    public long DatabaseSizeBytes { get; set; }
    public DateTimeOffset? LastFlushUtc { get; set; }
    public int BufferedSamplesCount { get; set; }
    public long TotalStoredSnapshots { get; set; }
    public string HardwareBackend { get; set; } = string.Empty;
    public bool AutostartInstalled { get; set; }
    public int DevicesDetected { get; set; }
}

public sealed class DashboardSnapshot
{
    public DaemonStatusInfo Status { get; set; } = new();
    public List<NvmeDeviceInfo> Devices { get; set; } = [];
    public Dictionary<string, SmartSnapshot> LatestSmart { get; set; } = new();
    public Dictionary<string, LiveTelemetry> LatestLive { get; set; } = new();
    public Dictionary<string, LifespanForecast> Forecasts { get; set; } = new();
    public List<AlertEvent> Alerts { get; set; } = [];
    public DaemonSettings Settings { get; set; } = new();
}

public sealed class IpcRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Command { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
    public DateTimeOffset? StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }
    public string? SelfTestType { get; set; }
    public string? ExportFormat { get; set; }
    public string? ExportPath { get; set; }
    public DaemonSettings? Settings { get; set; }
}

public sealed class IpcResponse
{
    public string Id { get; set; } = string.Empty;
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public string? Message { get; set; }
    public DashboardSnapshot? Dashboard { get; set; }
    public TimeRangeInspection? Inspection { get; set; }
    public LiveTelemetry? Live { get; set; }
    public DaemonStatusInfo? Status { get; set; }
}
