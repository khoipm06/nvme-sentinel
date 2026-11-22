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

        // Trailing zero suppression
        long exact12Tb = 12_000_000_000_000L;
        Assert.Equal("12 TB", ByteFormatter.FormatBytes(exact12Tb, decimals: 3, ByteUnitStandard.DecimalSi));
        Assert.Equal("12.5 TB", ByteFormatter.FormatBytes(12_500_000_000_000L, decimals: 3, ByteUnitStandard.DecimalSi));
        Assert.Equal("12.54 TB", ByteFormatter.FormatBytes(12_540_000_000_000L, decimals: 3, ByteUnitStandard.DecimalSi));
        Assert.Equal("0 B", ByteFormatter.FormatBytes(0));
        Assert.Equal("0 MB/s", ByteFormatter.FormatRate(0));
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

    [Fact]
    public void RatedTbwEstimator_CorrectlyBinsEnterpriseAndProtectsPlausibility()
    {
        var settings = new DaemonSettings();

        // 1. Enterprise Read-Intensive: 1.92 TB should not be snapped to 2.0 TB
        var enterpriseDev = new NvmeDeviceInfo
        {
            DeviceId = "ENT_1920",
            Model = "Micron_7450_MTFDKCC1T9TFR",
            CapacityBytes = 1_920_383_410_176L // ~1.92 TB
        };
        var (ratedBytes192, source192) = RatedTbwEstimator.Estimate(enterpriseDev, null, settings);
        Assert.Equal(1_920_000_000_000L * 600 / 1000 * 1000, ratedBytes192); // Normalized capacity is 1.92 TB
        Assert.Equal(1_152_000_000_000_000L, ratedBytes192); // 1.92 * 600 TBW = 1,152 TBW

        // 2. Extrapolation plausibility envelope: Reject wild extrapolation from counterfeit or corrupt SMART
        var normalDev = new NvmeDeviceInfo
        {
            DeviceId = "DEV_1TB",
            Model = "Generic NVMe SSD",
            CapacityBytes = 1_000_000_000_000L
        };
        // If counterfeit firmware reports 1% wear despite 100 TB written (claiming 10,000 TBW on 1TB drive)
        var corruptSmart = new SmartSnapshot
        {
            DeviceId = "DEV_1TB",
            PercentUsed = 5,
            TotalBytesWritten = 250_000_000_000_000L // 250 TB written at 5% implies 5,000 TBW/TB (exceeds 3,500 ceiling)
        };
        var (ratedBytesCorrupt, sourceCorrupt) = RatedTbwEstimator.Estimate(normalDev, corruptSmart, settings);
        Assert.Equal("Standard TLC Estimate (600 TBW/TB)", sourceCorrupt);
    }

    [Fact]
    public void LifespanAnalyzer_CapsRealisticHorizonUnderNegligibleBurnRate()
    {
        var dev = new NvmeDeviceInfo
        {
            DeviceId = "IDLE_DEV",
            Model = "Samsung 980 PRO 1TB",
            CapacityBytes = 1_000_000_000_000L
        };
        var smart = new SmartSnapshot
        {
            DeviceId = "IDLE_DEV",
            PercentUsed = 1,
            TotalBytesWritten = 5_000_000_000_000L, // 5 TB
            PowerOnHours = 8760, // 1 year
            AvailableSpare = 100
        };
        var settings = new DaemonSettings();
        var tempDir = Path.Combine(Path.GetTempPath(), $"nvme-sentinel-life-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using var store = new TimeSeriesStore(Path.Combine(tempDir, "life.db"));
            var forecast = LifespanAnalyzer.Compute(dev, smart, settings, store);

            // With almost zero time-series writes and small lifetime rate, horizon should be capped at 10 years
            if (forecast.EffectiveBurnRateBytesPerDay >= 1_000_000_000.0)
            {
                Assert.True(forecast.EstimatedDaysRemaining <= 3652.5);
            }
            else
            {
                // Negligible daily burn rate leaves exhaustion date null (displayed as "> 10 yrs")
                Assert.Null(forecast.EstimatedExhaustionDateUtc);
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SpikeThresholdCalculator_CalculatesDynamicThresholdAndResolvesProfiles()
    {
        // 600 TBW @ 1825 days with 12x burst multiplier:
        // Daily: (600 * 1000) / 1825 = 328.767 GB/day
        // 5-min avg: 328.767 / 288 = 1.14155 GB
        // Threshold: 1.14155 * 12 = 13.70 GB
        long tbw1Tb = 600_000_000_000_000L;
        double dyn1Tb = SpikeThresholdCalculator.CalculateDynamicGbPer5Min(tbw1Tb, 1825.0, 12.0);
        Assert.InRange(dyn1Tb, 13.5, 14.0);

        // 1200 TBW (2TB High-endurance)
        long tbw2Tb = 1200_000_000_000_000L;
        double dyn2Tb = SpikeThresholdCalculator.CalculateDynamicGbPer5Min(tbw2Tb, 1825.0, 12.0);
        Assert.InRange(dyn2Tb, 27.0, 28.0);

        // Test Profile Resolution
        var settings = new DaemonSettings
        {
            AlertProfile = AlertProfile.Office,
            OfficeProfileThresholdGb = 8.0,
            GamingProfileThresholdGb = 35.0,
            CreatorProfileThresholdGb = 100.0,
            SpikeThresholdGbPer5Min = 15.0
        };

        Assert.Equal(8.0, SpikeThresholdCalculator.ResolveEffectiveThresholdGb(settings, tbw1Tb));

        settings.AlertProfile = AlertProfile.Gaming;
        Assert.Equal(35.0, SpikeThresholdCalculator.ResolveEffectiveThresholdGb(settings, tbw1Tb));

        settings.AlertProfile = AlertProfile.Creator;
        Assert.Equal(100.0, SpikeThresholdCalculator.ResolveEffectiveThresholdGb(settings, tbw1Tb));

        settings.AlertProfile = AlertProfile.Custom;
        Assert.Equal(15.0, SpikeThresholdCalculator.ResolveEffectiveThresholdGb(settings, tbw1Tb));

        settings.AlertProfile = AlertProfile.DynamicDwpd;
        Assert.Equal(dyn1Tb, SpikeThresholdCalculator.ResolveEffectiveThresholdGb(settings, tbw1Tb));
    }

    [Fact]
    public void SpikeThresholdCalculator_MatchesWhitelistedProcessesCrossPlatform()
    {
        var whitelist = new List<string> { "steam", "epicgameslauncher", "rsync", "tar", "timeshift" };

        Assert.True(SpikeThresholdCalculator.IsProcessWhitelisted("steam", "/usr/bin/steam", whitelist));
        Assert.True(SpikeThresholdCalculator.IsProcessWhitelisted("steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", whitelist));
        Assert.True(SpikeThresholdCalculator.IsProcessWhitelisted("EpicGamesLauncher", @"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", whitelist));
        Assert.True(SpikeThresholdCalculator.IsProcessWhitelisted("rsync", "/usr/bin/rsync", whitelist));
        Assert.True(SpikeThresholdCalculator.IsProcessWhitelisted("tar", "/bin/tar", whitelist));

        // Non-whitelisted processes
        Assert.False(SpikeThresholdCalculator.IsProcessWhitelisted("cryptominer", "/tmp/cryptominer", whitelist));
        Assert.False(SpikeThresholdCalculator.IsProcessWhitelisted("ransomware.exe", @"C:\Users\User\Downloads\ransomware.exe", whitelist));
    }

    [Fact]
    public void DaemonSettings_RoundTripsThroughSqliteStore()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nvme-sentinel-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "settings_test.db");

        try
        {
            using var store = new TimeSeriesStore(dbPath);
            var initial = new DaemonSettings
            {
                AlertProfile = AlertProfile.DynamicDwpd,
                SpikeThresholdGbPer5Min = 25.0,
                SustainedSpikeThresholdCycles = 3,
                ProcessWhitelist = ["steam", "timeshift", "defrag"],
                WarningTempCelsius = 78.0,
                CriticalTempCelsius = 86.0,
                CloseToTray = false,
                UseBinaryUnits = true
            };

            store.SaveSettings(initial);
            var loaded = store.LoadSettings();

            Assert.Equal(AlertProfile.DynamicDwpd, loaded.AlertProfile);
            Assert.Equal(25.0, loaded.SpikeThresholdGbPer5Min);
            Assert.Equal(3, loaded.SustainedSpikeThresholdCycles);
            Assert.Equal(3, loaded.ProcessWhitelist.Count);
            Assert.Contains("steam", loaded.ProcessWhitelist);
            Assert.Contains("timeshift", loaded.ProcessWhitelist);
            Assert.Equal(78.0, loaded.WarningTempCelsius);
            Assert.Equal(86.0, loaded.CriticalTempCelsius);
            Assert.False(loaded.CloseToTray);
            Assert.True(loaded.UseBinaryUnits);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AppPaths_UsesHistoryDbAndSupportsCustomDirectory()
    {
        var originalCustom = AppPaths.CustomDataDirectory;
        var testDir = Path.Combine(Path.GetTempPath(), $"sentinel-custom-{Guid.NewGuid():N}");

        try
        {
            AppPaths.CustomDataDirectory = testDir;
            Assert.Equal(testDir, AppPaths.GetDataDirectory());
            Assert.Equal(Path.Combine(testDir, "history.db"), AppPaths.GetDatabasePath());
        }
        finally
        {
            AppPaths.CustomDataDirectory = originalCustom;
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
    }

    [Fact]
    public void AppPaths_MigratesLegacyTelemetryDbToHistoryDb()
    {
        var originalCustom = AppPaths.CustomDataDirectory;
        var testDir = Path.Combine(Path.GetTempPath(), $"sentinel-mig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);

        try
        {
            AppPaths.CustomDataDirectory = testDir;
            var oldDb = Path.Combine(testDir, "telemetry.db");
            var oldWal = Path.Combine(testDir, "telemetry.db-wal");
            var newDb = Path.Combine(testDir, "history.db");
            var newWal = Path.Combine(testDir, "history.db-wal");

            File.WriteAllText(oldDb, "legacy-data");
            File.WriteAllText(oldWal, "legacy-wal");

            var resolvedPath = AppPaths.GetDatabasePath();
            Assert.Equal(newDb, resolvedPath);
            Assert.True(File.Exists(newDb));
            Assert.True(File.Exists(newWal));
            Assert.False(File.Exists(oldDb));
            Assert.False(File.Exists(oldWal));
            Assert.Equal("legacy-data", File.ReadAllText(newDb));
        }
        finally
        {
            AppPaths.CustomDataDirectory = originalCustom;
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
    }
}
