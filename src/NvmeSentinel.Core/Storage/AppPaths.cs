using System.Globalization;
using System.Runtime.InteropServices;

namespace NvmeSentinel.Core.Storage;

public static class AppPaths
{
    public const string AppFolderName = "nvme-sentinel";
    public const string DatabaseFileName = "history.db";
    public static string? CustomDataDirectory { get; set; }

    public static string GetDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(CustomDataDirectory))
        {
            Directory.CreateDirectory(CustomDataDirectory);
            return CustomDataDirectory;
        }

        var overrideDir = Environment.GetEnvironmentVariable("NVME_SENTINEL_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            Directory.CreateDirectory(overrideDir);
            return overrideDir;
        }

        string baseDir;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support");
        }
        else
        {
            var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            baseDir = !string.IsNullOrWhiteSpace(xdgData)
                ? xdgData
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        var dir = Path.Combine(baseDir, AppFolderName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetDatabasePath()
    {
        var dir = GetDataDirectory();
        var newDbPath = Path.Combine(dir, DatabaseFileName);
        var oldDbPath = Path.Combine(dir, "telemetry.db");

        // Transparent automatic migration from old telemetry.db to user-friendly history.db
        if (!File.Exists(newDbPath) && File.Exists(oldDbPath))
        {
            try
            {
                File.Move(oldDbPath, newDbPath);
                if (File.Exists(oldDbPath + "-wal")) File.Move(oldDbPath + "-wal", newDbPath + "-wal");
                if (File.Exists(oldDbPath + "-shm")) File.Move(oldDbPath + "-shm", newDbPath + "-shm");
            }
            catch
            {
                return oldDbPath;
            }
        }

        return newDbPath;
    }

    public static string GetSettingsPath() => Path.Combine(GetDataDirectory(), "settings.json");

    public static string GetDaemonLockPath() => Path.Combine(GetDataDirectory(), "daemon.lock");

    public static string GetIpcPipeName()
    {
        var customPipe = Environment.GetEnvironmentVariable("NVME_SENTINEL_PIPE");
        if (!string.IsNullOrWhiteSpace(customPipe))
        {
            return customPipe;
        }

        var user = Environment.UserName;
        var sanitized = new string(user.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrEmpty(sanitized)
            ? "nvme-sentinel-ipc"
            : $"nvme-sentinel-ipc-{sanitized}";
    }
}

public enum ByteUnitStandard
{
    DecimalSi, // 1000^n: KB, MB, GB, TB
    BinaryIec  // 1024^n: KiB, MiB, GiB, TiB
}

public static class ByteFormatter
{
    private static readonly string[] s_binaryUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
    private static readonly string[] s_decimalUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static ByteUnitStandard CurrentStandard { get; set; } = ByteUnitStandard.DecimalSi;

    public static string FormatBytes(long bytes, int decimals = 2, ByteUnitStandard? standard = null)
    {
        var std = standard ?? CurrentStandard;
        if (bytes == long.MinValue)
        {
            return "-" + FormatBytes(long.MaxValue, decimals, std);
        }

        if (bytes < 0)
        {
            return "-" + FormatBytes(-bytes, decimals, std);
        }

        if (bytes == 0)
        {
            return "0 B";
        }

        double value = bytes;
        string unit;
        if (std == ByteUnitStandard.BinaryIec)
        {
            int idx = 0;
            while (value >= 1024.0 && idx < s_binaryUnits.Length - 1)
            {
                value /= 1024.0;
                idx++;
            }

            if (idx == 0) return $"{bytes} B";
            unit = s_binaryUnits[idx];
        }
        else
        {
            // Hardware storage manufacturers define TBW in decimal Terabytes (1 TB = 10^12 bytes).
            int idx = 0;
            while (value >= 1000.0 && idx < s_decimalUnits.Length - 1)
            {
                value /= 1000.0;
                idx++;
            }

            if (idx == 0) return $"{bytes} B";
            unit = s_decimalUnits[idx];
        }

        string numStr = value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
        if (numStr.Contains('.'))
        {
            numStr = numStr.TrimEnd('0').TrimEnd('.');
        }
        return $"{numStr} {unit}";
    }

    public static string FormatRate(double bytesPerSec, ByteUnitStandard? standard = null)
    {
        var std = standard ?? CurrentStandard;
        if (bytesPerSec <= 0.5)
        {
            return std == ByteUnitStandard.BinaryIec ? "0 MiB/s" : "0 MB/s";
        }

        double baseUnit = std == ByteUnitStandard.BinaryIec ? 1024.0 : 1000.0;
        string kb = std == ByteUnitStandard.BinaryIec ? "KiB/s" : "KB/s";
        string mb = std == ByteUnitStandard.BinaryIec ? "MiB/s" : "MB/s";
        string gb = std == ByteUnitStandard.BinaryIec ? "GiB/s" : "GB/s";

        if (bytesPerSec < baseUnit)
        {
            return $"{bytesPerSec:F0} B/s";
        }

        if (bytesPerSec < baseUnit * baseUnit)
        {
            string s = (bytesPerSec / baseUnit).ToString("F1", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
            return $"{s} {kb}";
        }

        if (bytesPerSec < baseUnit * baseUnit * baseUnit)
        {
            string s = (bytesPerSec / (baseUnit * baseUnit)).ToString("F1", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
            return $"{s} {mb}";
        }

        string sg = (bytesPerSec / (baseUnit * baseUnit * baseUnit)).ToString("F2", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return $"{sg} {gb}";
    }
}
