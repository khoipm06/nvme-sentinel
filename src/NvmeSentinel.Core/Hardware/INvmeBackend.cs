using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Hardware;

public sealed class BlockIoCounters
{
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public long TotalBytesRead { get; set; }
    public long TotalBytesWritten { get; set; }
    public long ReadCompletedOps { get; set; }
    public long WriteCompletedOps { get; set; }
    public double CompositeTempCelsius { get; set; }
    public List<SensorReading> Sensors { get; set; } = [];
    public string PcieCurrentLinkSpeed { get; set; } = string.Empty;
    public int PcieCurrentLinkWidth { get; set; }
    public string PowerState { get; set; } = "Active";
}

public sealed record ProcessWriteDelta(
    string ProcessName,
    string ExecutablePath,
    long WriteBytesDelta,
    long ReadBytesDelta);

public sealed class ProcessPollResult
{
    public List<ProcessIoEntry> TopActiveProcesses { get; set; } = [];
    public List<ProcessWriteDelta> IntervalDeltas { get; set; } = [];
}

public interface INvmeProvider
{
    string BackendName { get; }
    Task<List<NvmeDeviceInfo>> DiscoverDevicesAsync(CancellationToken ct = default);
    Task<SmartSnapshot?> ReadSmartAsync(NvmeDeviceInfo device, bool forceRefresh = false, CancellationToken ct = default);
    BlockIoCounters ReadLiveBlockCounters(NvmeDeviceInfo device);
    Task<(bool Ok, string Message)> StartSelfTestAsync(NvmeDeviceInfo device, string testType, CancellationToken ct = default);
}

public interface IProcessIoTracker
{
    ProcessPollResult Poll(double elapsedSeconds, long blockWriteDeltaBytes);
}
