using NvmeSentinel.Core.Hardware;
using NvmeSentinel.Core.Models;
using NvmeSentinel.Core.Storage;

namespace NvmeSentinel.Core.Services;

public static class LifespanAnalyzer
{
    public static LifespanForecast Compute(
        NvmeDeviceInfo device,
        SmartSnapshot? smart,
        DaemonSettings settings,
        TimeSeriesStore store,
        IReadOnlyList<BufferedBucket>? unflushed = null)
    {
        var (ratedBytes, ratedSource) = RatedTbwEstimator.Estimate(device, smart, settings);
        long currentTbw = smart?.TotalBytesWritten ?? 0;
        long remaining = Math.Max(0, ratedBytes - currentTbw);
        double tbwPct = ratedBytes > 0 ? Math.Round(currentTbw * 100.0 / ratedBytes, 2) : 0;
        int hwWear = smart?.PercentUsed ?? 0;

        var now = DateTimeOffset.UtcNow;
        double rate24h = ComputeWindowBurnRatePerDay(store, device.DeviceId, now.AddHours(-24), now, minWindowHours: 0.25, unflushed);
        double rate7d = ComputeWindowBurnRatePerDay(store, device.DeviceId, now.AddDays(-7), now, minWindowHours: 6.0, unflushed);
        double rate30d = ComputeWindowBurnRatePerDay(store, device.DeviceId, now.AddDays(-30), now, minWindowHours: 24.0, unflushed);

        double lifetimePerActiveDay = 0;
        if (smart is { PowerOnHours: > 0, TotalBytesWritten: > 0 })
        {
            double activeDays = Math.Max(1.0, smart.PowerOnHours / 24.0);
            lifetimePerActiveDay = smart.TotalBytesWritten / activeDays;
        }

        // Choose most representative burn rate: 30d -> 7d -> blended 24h+lifetime -> lifetime
        double effectiveRate = rate30d > 0
            ? rate30d
            : rate7d > 0
                ? rate7d
                : rate24h > 0 && lifetimePerActiveDay > 0
                    ? (rate24h * 0.6 + lifetimePerActiveDay * 0.4)
                    : rate24h > 0
                        ? rate24h
                        : lifetimePerActiveDay;

        double? estDays = null;
        DateTimeOffset? exhaustionDate = null;
        const double OneGigabyte = 1_000_000_000.0;
        const double MaxPracticalHorizonDays = 3652.5; // Cap practical silicon exhaustion projection at 10.0 years

        // If daily burn rate is negligible (< 1 GB/day), endurance wear is not the limiting failure factor;
        // physical component aging, capacitor degradation, and dielectric charge leakage (JEDEC JESD218) dominate.
        if (effectiveRate >= OneGigabyte && remaining > 0)
        {
            double days = remaining / effectiveRate;
            if (days <= MaxPracticalHorizonDays)
            {
                estDays = Math.Round(days, 1);
                exhaustionDate = now.AddDays(days);
            }
            else
            {
                // Leave exhaustionDate null so the presentation layer shows "> 10 yrs" without a misleading century date
                estDays = MaxPracticalHorizonDays;
            }
        }
        else if (remaining == 0 && ratedBytes > 0)
        {
            estDays = 0;
            exhaustionDate = now;
        }

        double effectiveWear = Math.Max((double)hwWear, tbwPct * 0.85);
        double baseHealthPrecise = Math.Clamp(100.0 - effectiveWear, 0.0, 100.0);
        if (smart is not null)
        {
            if (smart.MediaErrors > 0) baseHealthPrecise -= 35.0;
            if (smart.CriticalWarningFlags != 0) baseHealthPrecise -= 40.0;
            if (smart.AvailableSpare <= smart.SpareThreshold) baseHealthPrecise -= 35.0;
            else if (smart.AvailableSpare < 50) baseHealthPrecise -= 10.0;
        }

        double preciseScore = Math.Clamp(baseHealthPrecise, 0.0, 100.0);
        int score = (int)Math.Round(preciseScore);
        string grade = score switch
        {
            >= 90 => "Excellent",
            >= 75 => "Good",
            >= 50 => "Fair",
            >= 25 => "Caution",
            _ => "Critical"
        };

        return new LifespanForecast
        {
            DeviceId = device.DeviceId,
            RatedTbwBytes = ratedBytes,
            RatedTbwSource = ratedSource,
            CurrentTbwBytes = currentTbw,
            RemainingTbwBytes = remaining,
            TbwUsedPercentage = tbwPct,
            HardwarePercentUsed = hwWear,
            HealthScore = score,
            HealthScorePrecise = Math.Round(preciseScore, 1),
            HealthGrade = grade,
            BurnRate24hBytesPerDay = rate24h,
            BurnRate7dBytesPerDay = rate7d,
            BurnRate30dBytesPerDay = rate30d,
            LifetimeAvgBytesPerDay = lifetimePerActiveDay,
            EffectiveBurnRateBytesPerDay = effectiveRate,
            EstimatedDaysRemaining = estDays,
            EstimatedExhaustionDateUtc = exhaustionDate
        };
    }

    private static double ComputeWindowBurnRatePerDay(
        TimeSeriesStore store,
        string deviceId,
        DateTimeOffset start,
        DateTimeOffset end,
        double minWindowHours,
        IReadOnlyList<BufferedBucket>? unflushed)
    {
        var insp = store.InspectRange(deviceId, start, end, unflushed);
        if (insp.Points.Count == 0 || insp.TotalBytesWrittenDelta <= 0)
        {
            return 0;
        }

        var firstTs = insp.Points[0].TimestampUtc;
        var lastTs = insp.Points[^1].EndTimestampUtc;
        double observedHours = (lastTs - firstTs).TotalHours;
        if (observedHours < minWindowHours)
        {
            return 0;
        }

        double days = Math.Max(minWindowHours / 24.0, observedHours / 24.0);
        return insp.TotalBytesWrittenDelta / days;
    }
}
