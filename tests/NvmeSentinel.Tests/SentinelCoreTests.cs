using NvmeSentinel.Core.Hardware;
using NvmeSentinel.Core.Ipc;
using NvmeSentinel.Core.Models;
using NvmeSentinel.Core.Services;
using NvmeSentinel.Core.Storage;
using NvmeSentinel.Daemon;
using Xunit;

namespace NvmeSentinel.Tests;

public sealed class SentinelCoreTests
{
    [Fact]
    public void TimeSeriesStore_PersistsMultiTierRollupsAndProcessAttribution()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nvme-sentinel-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "test.db");

        try
        {
            using var store = new TimeSeriesStore(dbPath);
            var dev = new NvmeDeviceInfo
            {
                DeviceId = "TEST_NVME_1TB",
                Model = "SAMSUNG MZAL81T0HDLB-00BL2",
                SerialNumber = "S77TEST",
                FirmwareRevision = "1.0",
                CapacityBytes = 1_000_000_000_000L
            };
            store.UpsertDevices([dev]);

            var baseTime = DateTimeOffset.UtcNow.AddHours(-2);
            long baseTs = baseTime.ToUnixTimeSeconds() - (baseTime.ToUnixTimeSeconds() % 300);

            var bucket1 = new BufferedBucket
            {
                DeviceId = dev.DeviceId,
                BucketStartUtc = DateTimeOffset.FromUnixTimeSeconds(baseTs),
                BucketEndUtc = DateTimeOffset.FromUnixTimeSeconds(baseTs + 300),
                StartBytesWritten = 50_000_000_000_000L,
                EndBytesWritten = 50_012_000_000_000L,
                DeltaBytesWritten = 12_000_000_000L, // 12 GB
                DeltaBytesRead = 4_000_000_000L,
                TempSum = 52.0,
                TempSamples = 1,
                MaxTempCelsius = 55.5,
                PercentUsed = 8,
                AvailableSpare = 100,
                IsAnomalySpike = true
            };
            bucket1.ProcessTotals["podman"] = ("/usr/bin/podman", 9_000_000_000L, 1_000_000_000L);
            bucket1.ProcessTotals["chrome"] = ("/opt/google/chrome/chrome", 3_000_000_000L, 3_000_000_000L);

            var bucket2 = new BufferedBucket
            {
                DeviceId = dev.DeviceId,
                BucketStartUtc = DateTimeOffset.FromUnixTimeSeconds(baseTs + 300),
                BucketEndUtc = DateTimeOffset.FromUnixTimeSeconds(baseTs + 600),
                StartBytesWritten = 50_012_000_000_000L,
                EndBytesWritten = 50_015_000_000_000L,
                DeltaBytesWritten = 3_000_000_000L, // 3 GB
                DeltaBytesRead = 1_000_000_000L,
                TempSum = 46.0,
                TempSamples = 1,
                MaxTempCelsius = 48.0,
                PercentUsed = 8,
                AvailableSpare = 100
            };
            bucket2.ProcessTotals["chrome"] = ("/opt/google/chrome/chrome", 3_000_000_000L, 1_000_000_000L);

            var smart = new SmartSnapshot
            {
                DeviceId = dev.DeviceId,
                TotalBytesWritten = 50_015_000_000_000L,
                PercentUsed = 8,
                AvailableSpare = 100,
                PowerOnHours = 2400,
                CompositeTempCelsius = 46.0
            };

            store.FlushBucketsAndState(
                [bucket1, bucket2],
                new Dictionary<string, SmartSnapshot> { [dev.DeviceId] = smart },
                [
                    new AlertEvent
                    {
                        DeviceId = dev.DeviceId,
                        Severity = "Warning",
                        Category = "RunawayWrite",
                        Title = "Write Spike",
                        Message = "podman wrote 9 GB"
                    }
                ]);

            // Query 24h raw inspection
            var rawInsp = store.InspectRange(dev.DeviceId, DateTimeOffset.UtcNow.AddHours(-6), DateTimeOffset.UtcNow);
            Assert.Equal("raw", rawInsp.Resolution);
            Assert.Equal(2, rawInsp.Points.Count);
            Assert.Equal(15_000_000_000L, rawInsp.TotalBytesWrittenDelta);
            Assert.Equal(55.5, rawInsp.PeakTempCelsius);
            Assert.Equal(2, rawInsp.TopProcesses.Count);
            Assert.Equal("podman", rawInsp.TopProcesses[0].ProcessName);
            Assert.Equal(9_000_000_000L, rawInsp.TopProcesses[0].BytesWritten);
            Assert.Equal(60.0, rawInsp.TopProcesses[0].SharePercentage);

            // Query 30d hourly rollup inspection
            var hourlyInsp = store.InspectRange(dev.DeviceId, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow);
            Assert.Equal("hourly", hourlyInsp.Resolution);
            Assert.NotEmpty(hourlyInsp.Points);
            Assert.Equal(15_000_000_000L, hourlyInsp.TotalBytesWrittenDelta);

            // Lifespan forecast validation
            var forecast = LifespanAnalyzer.Compute(dev, smart, new DaemonSettings(), store);
            Assert.Equal(600_000_000_000_000L, forecast.RatedTbwBytes); // 1TB Samsung TLC -> 600 TBW
            Assert.True(forecast.HealthScore >= 90);
            Assert.NotNull(forecast.EstimatedDaysRemaining);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ByteFormatter_SupportsDecimalAndBinaryWithConfigurablePrecision()
    {
        long bytes = 78_618_427_904_000L; // ~78.618 TB or ~71.504 TiB

        var dec3 = ByteFormatter.FormatBytes(bytes, decimals: 3, ByteUnitStandard.DecimalSi);
        Assert.Equal("78.618 TB", dec3);

        var bin3 = ByteFormatter.FormatBytes(bytes, decimals: 3, ByteUnitStandard.BinaryIec);
        Assert.Equal("71.503 TiB", bin3);

        var decRate = ByteFormatter.FormatRate(155_500_000, ByteUnitStandard.DecimalSi);
        Assert.Equal("155.5 MB/s", decRate);

        var binRate = ByteFormatter.FormatRate(155_500_000, ByteUnitStandard.BinaryIec);
        Assert.Equal("148.3 MiB/s", binRate);
    }

    [Fact]
    public void TimeSeriesStore_ClearsAllAlerts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nvme-clear-alerts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "test.db");

        try
        {
            using var store = new TimeSeriesStore(dbPath);
            store.FlushBucketsAndState(
                [],
                new Dictionary<string, SmartSnapshot>(),
                [
                    new AlertEvent
                    {
                        DeviceId = "dev1",
                        Severity = "Critical",
                        Category = "Thermal",
                        Title = "Overheating",
                        Message = "84 C"
                    }
                ]);

            Assert.Single(store.GetRecentAlerts());

            store.ClearAlerts();
            Assert.Empty(store.GetRecentAlerts());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DaemonEngine_HandlesIpcRequestsEndToEnd()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nvme-sentinel-ipc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var pipeName = $"nvme-sentinel-test-{Guid.NewGuid():N}";

        try
        {
            using var store = new TimeSeriesStore(Path.Combine(tempDir, "ipc.db"));
            await using var engine = new SentinelMonitorEngine(store: store, customPipeName: pipeName);
            await engine.InitializeAndStartAsync();

            var client = new SentinelIpcClient(pipeName);
            Assert.True(await client.PingAsync(1500));

            var dashResp = await client.SendAsync(new IpcRequest { Command = "GetDashboard" });
            Assert.True(dashResp.Ok);
            Assert.NotNull(dashResp.Dashboard);
            Assert.True(dashResp.Dashboard!.Status.Pid > 0);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
