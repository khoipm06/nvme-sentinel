using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Hardware;

public static class RatedTbwEstimator
{
    private const long OneTerabyte = 1_000_000_000_000L;

    public static (long RatedTbwBytes, string Source) Estimate(
        NvmeDeviceInfo device,
        SmartSnapshot? smart,
        DaemonSettings settings)
    {
        if (settings.CustomRatedTbwTb.TryGetValue(device.DeviceId, out var customTb) && customTb > 0)
        {
            return ((long)Math.Round(customTb * OneTerabyte), "Custom User Override");
        }

        double capacityTb = device.CapacityBytes > 0
            ? Math.Max(0.125, Math.Round(device.CapacityBytes / (double)OneTerabyte * 4.0) / 4.0)
            : 1.0;

        // Normalize common capacities (e.g., 1.024 TB -> 1.0 TB, 0.512 TB -> 0.5 TB, 2.048 TB -> 2.0 TB)
        capacityTb = NormalizeCapacityTb(device.CapacityBytes);

        var modelUpper = (device.Model ?? string.Empty).ToUpperInvariant();

        // Check known QLC models (~300-400 TBW per TB) vs high-endurance TLC (~600-1000 TBW per TB)
        if (ContainsAny(modelUpper, "P3 ", "P3 PLUS", "NV2", "NV3", "SN350", "670P", "660P", "MP600 CORE"))
        {
            return ((long)Math.Round(capacityTb * 320.0 * OneTerabyte), "Model Preset (QLC Tier)");
        }

        if (ContainsAny(modelUpper, "KC3000", "FURY RENEGADE", "FIRECUDA 530", "NM790"))
        {
            return ((long)Math.Round(capacityTb * 800.0 * OneTerabyte), "Model Preset (High-Endurance TLC)");
        }

        if (ContainsAny(modelUpper, "SAMSUNG", "MZAL8", "MZVL", "990 PRO", "980 PRO", "970 EVO", "SN850", "SN770", "SN580", "T500", "P41", "P31"))
        {
            return ((long)Math.Round(capacityTb * 600.0 * OneTerabyte), "Model Preset (TLC 600 TBW/TB)");
        }

        // Extrapolate from firmware wear curve if meaningful wear has accumulated
        if (smart is { PercentUsed: >= 5 and <= 150, TotalBytesWritten: > 5 * OneTerabyte })
        {
            var extrapolatedBytes = (long)Math.Round(smart.TotalBytesWritten / (smart.PercentUsed / 100.0));
            // Round to nearest 50 TB
            var tbRounded = Math.Round(extrapolatedBytes / (50.0 * OneTerabyte)) * 50.0;
            if (tbRounded >= 100 && tbRounded <= 10_000)
            {
                return ((long)(tbRounded * OneTerabyte), "SMART Wear Extrapolation");
            }
        }

        return ((long)Math.Round(capacityTb * 600.0 * OneTerabyte), "Standard TLC Estimate (600 TBW/TB)");
    }

    private static double NormalizeCapacityTb(long capacityBytes)
    {
        if (capacityBytes <= 0)
        {
            return 1.0;
        }

        double rawTb = capacityBytes / (double)OneTerabyte;
        double[] standardSizes = [0.128, 0.25, 0.5, 1.0, 2.0, 4.0, 8.0, 16.0];
        var closest = standardSizes.MinBy(s => Math.Abs(s - rawTb));
        return Math.Abs(closest - rawTb) / closest < 0.18 ? closest : Math.Round(rawTb, 2);
    }

    private static bool ContainsAny(string text, params string[] patterns) =>
        patterns.Any(p => text.Contains(p, StringComparison.Ordinal));
}
