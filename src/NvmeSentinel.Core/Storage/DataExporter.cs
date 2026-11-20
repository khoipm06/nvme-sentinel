using System.Globalization;
using System.Text;
using System.Text.Json;
using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Storage;

public static class DataExporter
{
    public static string ExportInspection(
        NvmeDeviceInfo device,
        SmartSnapshot? latestSmart,
        LifespanForecast? forecast,
        TimeRangeInspection inspection,
        string format,
        string? targetPath)
    {
        bool isJson = format.Equals("json", StringComparison.OrdinalIgnoreCase);
        string ext = isJson ? "json" : "csv";
        string path = !string.IsNullOrWhiteSpace(targetPath)
            ? targetPath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                $"nvme-sentinel-report-{DateTime.Now:yyyyMMdd-HHmmss}.{ext}");

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (isJson)
        {
            var payload = new
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Device = device,
                CurrentSmart = latestSmart,
                LifespanForecast = forecast,
                TimeRangeInspection = inspection
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("# NVMe Sentinel Telemetry Export");
            sb.AppendLine($"# Device,{EscapeCsv(device.Model)},Serial,{EscapeCsv(device.SerialNumber)},Firmware,{EscapeCsv(device.FirmwareRevision)}");
            if (latestSmart is not null)
            {
                sb.AppendLine($"# TotalBytesWritten,{latestSmart.TotalBytesWritten},PercentUsed,{latestSmart.PercentUsed}%,PowerOnHours,{latestSmart.PowerOnHours}");
            }

            sb.AppendLine();
            sb.AppendLine("Section,TimestampUtc,TotalBytesWritten,DeltaBytesWritten,DeltaBytesRead,AvgTempC,MaxTempC,PercentUsed,AvailableSpare,IsSpike");
            foreach (var pt in inspection.Points)
            {
                sb.AppendLine(string.Join(",",
                    "TimeSeries",
                    pt.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                    pt.TotalBytesWritten,
                    pt.DeltaBytesWritten,
                    pt.DeltaBytesRead,
                    pt.AvgTempCelsius.ToString("F1", CultureInfo.InvariantCulture),
                    pt.MaxTempCelsius.ToString("F1", CultureInfo.InvariantCulture),
                    pt.PercentUsed,
                    pt.AvailableSpare,
                    pt.IsAnomalySpike ? 1 : 0));
            }

            sb.AppendLine();
            sb.AppendLine("Section,ProcessName,BytesWritten,BytesRead,SharePercent,ExecutablePath");
            foreach (var proc in inspection.TopProcesses)
            {
                sb.AppendLine(string.Join(",",
                    "ProcessAttribution",
                    EscapeCsv(proc.ProcessName),
                    proc.BytesWritten,
                    proc.BytesRead,
                    proc.SharePercentage.ToString("F1", CultureInfo.InvariantCulture),
                    EscapeCsv(proc.ExecutablePath)));
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        return path;
    }

    private static string EscapeCsv(string? val)
    {
        if (string.IsNullOrEmpty(val)) return "";
        if (val.Contains(',') || val.Contains('"') || val.Contains('\n'))
        {
            return $"\"{val.Replace("\"", "\"\"")}\"";
        }

        return val;
    }
}
