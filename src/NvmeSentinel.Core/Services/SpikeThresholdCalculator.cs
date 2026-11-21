using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Services;

public static class SpikeThresholdCalculator
{
    public const double DefaultOfficeGb = 8.0;
    public const double DefaultGamingGb = 35.0;
    public const double DefaultCreatorGb = 100.0;
    public const double DefaultWarrantyDays = 1825.0; // 5 years standard manufacturer warranty
    public const double DefaultBurstMultiplier = 12.0;

    /// <summary>
    /// Calculates dynamic 5-minute write alert threshold based on hardware endurance rating (TBW):
    /// DailySafeWrite (GB) = (TBW_TB * 1000) / WarrantyDays
    /// Avg5MinWrite (GB) = DailySafeWrite / 288
    /// AlertThreshold (GB/5m) = Avg5MinWrite * BurstMultiplier
    /// </summary>
    public static double CalculateDynamicGbPer5Min(
        long ratedTbwBytes,
        double warrantyDays = DefaultWarrantyDays,
        double burstMultiplier = DefaultBurstMultiplier)
    {
        if (ratedTbwBytes <= 0)
        {
            return DefaultOfficeGb;
        }

        double tbwTb = ratedTbwBytes / 1_000_000_000_000.0;
        double days = warrantyDays > 0 ? warrantyDays : DefaultWarrantyDays;
        double multiplier = burstMultiplier > 0 ? burstMultiplier : DefaultBurstMultiplier;

        // Daily safe write allotment according to standard warranty endurance
        double dailySafeGb = (tbwTb * 1000.0) / days;

        // 288 five-minute intervals per 24 hours (1440 / 5)
        double avg5MinGb = dailySafeGb / 288.0;

        // Burst threshold (10x-15x burst above normal 5-min baseline)
        double thresholdGb = avg5MinGb * multiplier;

        return Math.Round(Math.Clamp(thresholdGb, 1.0, 500.0), 2);
    }

    /// <summary>
    /// Resolves the effective 5-minute write spike threshold in GB according to user profile.
    /// </summary>
    public static double ResolveEffectiveThresholdGb(
        DaemonSettings settings,
        long ratedTbwBytes)
    {
        return settings.AlertProfile switch
        {
            AlertProfile.Office => settings.OfficeProfileThresholdGb > 0 ? settings.OfficeProfileThresholdGb : DefaultOfficeGb,
            AlertProfile.Gaming => settings.GamingProfileThresholdGb > 0 ? settings.GamingProfileThresholdGb : DefaultGamingGb,
            AlertProfile.Creator => settings.CreatorProfileThresholdGb > 0 ? settings.CreatorProfileThresholdGb : DefaultCreatorGb,
            AlertProfile.DynamicDwpd => CalculateDynamicGbPer5Min(ratedTbwBytes, settings.WarrantyDays, settings.DynamicBurstMultiplier),
            AlertProfile.Custom => settings.SpikeThresholdGbPer5Min > 0 ? settings.SpikeThresholdGbPer5Min : DefaultOfficeGb,
            _ => settings.SpikeThresholdGbPer5Min > 0 ? settings.SpikeThresholdGbPer5Min : DefaultOfficeGb
        };
    }

    /// <summary>
    /// Checks whether a process name or executable path matches any entry in the user whitelist.
    /// Supports exact matches and extension-agnostic matches (e.g. "steam" matches "steam.exe").
    /// </summary>
    public static bool IsProcessWhitelisted(string processName, string executablePath, IEnumerable<string>? whitelist)
    {
        if (whitelist is null) return false;

        var procTrimmed = processName.Trim();
        var exeName = Path.GetFileName(executablePath).Trim();

        foreach (var entry in whitelist)
        {
            var pattern = entry.Trim();
            if (string.IsNullOrEmpty(pattern)) continue;

            if (string.Equals(procTrimmed, pattern, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(exeName, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Extension-agnostic check: "steam" matches "steam.exe" or vice-versa
            var patWithoutExt = pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? pattern[..^4]
                : pattern;

            var procWithoutExt = procTrimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? procTrimmed[..^4]
                : procTrimmed;

            var exeWithoutExt = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? exeName[..^4]
                : exeName;

            if (string.Equals(procWithoutExt, patWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(exeWithoutExt, patWithoutExt, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
