namespace NvmeSentinel.Core.Common;

/// <summary>
/// Common constants and protocol definitions for NVMe Sentinel.
/// </summary>
public static class NvmeConstants
{
    /// <summary>
    /// NVMe Base Specification: Each Data Unit read/written represents 1,000 sectors of 512 bytes = 512,000 bytes.
    /// </summary>
    public const long BytesPerDataUnit = 512_000L;

    /// <summary>
    /// Storage industry standard decimal Terabyte (10^12 bytes).
    /// </summary>
    public const long OneTerabyte = 1_000_000_000_000L;
}

/// <summary>
/// IPC command verb constants used between GUI, CLI, and Daemon.
/// </summary>
public static class IpcCommands
{
    public const string Ping = "Ping";
    public const string SubscribeLive = "SubscribeLive";
    public const string GetDashboard = "GetDashboard";
    public const string GetStatus = "GetStatus";
    public const string ShutdownDaemon = "ShutdownDaemon";
    public const string FlushNow = "FlushNow";
    public const string StartSelfTest = "StartSelfTest";
    public const string ExportData = "ExportData";
    public const string AcknowledgeAlerts = "AcknowledgeAlerts";
    public const string ClearAlerts = "ClearAlerts";
    public const string UpdateSettings = "UpdateSettings";
    public const string InspectRange = "InspectRange";
}
