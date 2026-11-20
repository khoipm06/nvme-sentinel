using System.Text.Json;
using Microsoft.Data.Sqlite;
using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Storage;

public sealed class BufferedBucket
{
    public string DeviceId { get; set; } = string.Empty;
    public DateTimeOffset BucketStartUtc { get; set; }
    public DateTimeOffset BucketEndUtc { get; set; }
    public long StartBytesWritten { get; set; }
    public long EndBytesWritten { get; set; }
    public long StartBytesRead { get; set; }
    public long EndBytesRead { get; set; }
    public long DeltaBytesWritten { get; set; }
    public long DeltaBytesRead { get; set; }
    public double TempSum { get; set; }
    public int TempSamples { get; set; }
    public double MaxTempCelsius { get; set; }
    public int PercentUsed { get; set; }
    public int AvailableSpare { get; set; } = 100;
    public long UnsafeShutdowns { get; set; }
    public long MediaErrors { get; set; }
    public bool IsAnomalySpike { get; set; }
    public Dictionary<string, (string ExePath, long BytesWritten, long BytesRead)> ProcessTotals { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public double AvgTempCelsius => TempSamples > 0 ? Math.Round(TempSum / TempSamples, 1) : MaxTempCelsius;
}

public sealed class TimeSeriesStore : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connString;
    private readonly object _sync = new();

    public string DatabasePath => _dbPath;

    public TimeSeriesStore(string? customDbPath = null)
    {
        _dbPath = customDbPath ?? AppPaths.GetDatabasePath();
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();

        InitializeSchema();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA busy_timeout = 5000;
            """;
        cmd.ExecuteNonQuery();
        return conn;
    }

    private void InitializeSchema()
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS devices (
                    device_id TEXT PRIMARY KEY,
                    model TEXT NOT NULL,
                    serial TEXT NOT NULL,
                    firmware TEXT NOT NULL,
                    capacity_bytes INTEGER NOT NULL,
                    first_seen_ts INTEGER NOT NULL,
                    last_seen_ts INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS smart_samples_raw (
                    device_id TEXT NOT NULL,
                    bucket_start_ts INTEGER NOT NULL,
                    bucket_end_ts INTEGER NOT NULL,
                    total_bytes_written INTEGER NOT NULL,
                    total_bytes_read INTEGER NOT NULL,
                    delta_bytes_written INTEGER NOT NULL,
                    delta_bytes_read INTEGER NOT NULL,
                    avg_temp_c REAL NOT NULL,
                    max_temp_c REAL NOT NULL,
                    percent_used INTEGER NOT NULL,
                    avail_spare INTEGER NOT NULL,
                    unsafe_shutdowns INTEGER NOT NULL,
                    media_errors INTEGER NOT NULL,
                    is_spike INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (device_id, bucket_start_ts)
                );

                CREATE INDEX IF NOT EXISTS idx_raw_device_ts ON smart_samples_raw(device_id, bucket_start_ts);

                CREATE TABLE IF NOT EXISTS smart_samples_hourly (
                    device_id TEXT NOT NULL,
                    hour_start_ts INTEGER NOT NULL,
                    total_bytes_written INTEGER NOT NULL,
                    total_bytes_read INTEGER NOT NULL,
                    delta_bytes_written INTEGER NOT NULL,
                    delta_bytes_read INTEGER NOT NULL,
                    avg_temp_c REAL NOT NULL,
                    max_temp_c REAL NOT NULL,
                    percent_used INTEGER NOT NULL,
                    avail_spare INTEGER NOT NULL,
                    unsafe_shutdowns INTEGER NOT NULL,
                    media_errors INTEGER NOT NULL,
                    has_spike INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (device_id, hour_start_ts)
                );

                CREATE TABLE IF NOT EXISTS smart_samples_daily (
                    device_id TEXT NOT NULL,
                    day_start_ts INTEGER NOT NULL,
                    total_bytes_written INTEGER NOT NULL,
                    total_bytes_read INTEGER NOT NULL,
                    delta_bytes_written INTEGER NOT NULL,
                    delta_bytes_read INTEGER NOT NULL,
                    avg_temp_c REAL NOT NULL,
                    max_temp_c REAL NOT NULL,
                    percent_used INTEGER NOT NULL,
                    avail_spare INTEGER NOT NULL,
                    unsafe_shutdowns INTEGER NOT NULL,
                    media_errors INTEGER NOT NULL,
                    has_spike INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (device_id, day_start_ts)
                );

                CREATE TABLE IF NOT EXISTS process_writes_raw (
                    device_id TEXT NOT NULL,
                    bucket_start_ts INTEGER NOT NULL,
                    process_name TEXT NOT NULL,
                    exe_path TEXT NOT NULL,
                    bytes_written INTEGER NOT NULL,
                    bytes_read INTEGER NOT NULL,
                    PRIMARY KEY (device_id, bucket_start_ts, process_name)
                );

                CREATE INDEX IF NOT EXISTS idx_proc_raw_ts ON process_writes_raw(device_id, bucket_start_ts);

                CREATE TABLE IF NOT EXISTS process_writes_hourly (
                    device_id TEXT NOT NULL,
                    hour_start_ts INTEGER NOT NULL,
                    process_name TEXT NOT NULL,
                    exe_path TEXT NOT NULL,
                    bytes_written INTEGER NOT NULL,
                    bytes_read INTEGER NOT NULL,
                    PRIMARY KEY (device_id, hour_start_ts, process_name)
                );

                CREATE TABLE IF NOT EXISTS process_writes_daily (
                    device_id TEXT NOT NULL,
                    day_start_ts INTEGER NOT NULL,
                    process_name TEXT NOT NULL,
                    exe_path TEXT NOT NULL,
                    bytes_written INTEGER NOT NULL,
                    bytes_read INTEGER NOT NULL,
                    PRIMARY KEY (device_id, day_start_ts, process_name)
                );

                CREATE TABLE IF NOT EXISTS latest_smart_state (
                    device_id TEXT PRIMARY KEY,
                    snapshot_json TEXT NOT NULL,
                    updated_ts INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS alerts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts INTEGER NOT NULL,
                    device_id TEXT NOT NULL,
                    severity TEXT NOT NULL,
                    category TEXT NOT NULL,
                    title TEXT NOT NULL,
                    message TEXT NOT NULL,
                    acknowledged INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS settings_kv (
                    key TEXT PRIMARY KEY,
                    value_json TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
    }

    public void UpsertDevices(IEnumerable<NvmeDeviceInfo> devices)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var d in devices)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO devices (device_id, model, serial, firmware, capacity_bytes, first_seen_ts, last_seen_ts)
                    VALUES ($id, $model, $serial, $fw, $cap, $now, $now)
                    ON CONFLICT(device_id) DO UPDATE SET
                        model = excluded.model,
                        firmware = excluded.firmware,
                        capacity_bytes = CASE WHEN excluded.capacity_bytes > 0 THEN excluded.capacity_bytes ELSE devices.capacity_bytes END,
                        last_seen_ts = excluded.last_seen_ts;
                    """;
                cmd.Parameters.AddWithValue("$id", d.DeviceId);
                cmd.Parameters.AddWithValue("$model", d.Model);
                cmd.Parameters.AddWithValue("$serial", d.SerialNumber);
                cmd.Parameters.AddWithValue("$fw", d.FirmwareRevision);
                cmd.Parameters.AddWithValue("$cap", d.CapacityBytes);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void FlushBucketsAndState(
        IReadOnlyList<BufferedBucket> buckets,
        IReadOnlyDictionary<string, SmartSnapshot> latestSmart,
        IReadOnlyList<AlertEvent> newAlerts)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            foreach (var kv in latestSmart)
            {
                using var sCmd = conn.CreateCommand();
                sCmd.Transaction = tx;
                sCmd.CommandText = """
                    INSERT INTO latest_smart_state (device_id, snapshot_json, updated_ts)
                    VALUES ($id, $json, $ts)
                    ON CONFLICT(device_id) DO UPDATE SET
                        snapshot_json = excluded.snapshot_json,
                        updated_ts = excluded.updated_ts;
                    """;
                sCmd.Parameters.AddWithValue("$id", kv.Key);
                sCmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(kv.Value));
                sCmd.Parameters.AddWithValue("$ts", kv.Value.TimestampUtc.ToUnixTimeSeconds());
                sCmd.ExecuteNonQuery();
            }

            foreach (var b in buckets)
            {
                long startTs = b.BucketStartUtc.ToUnixTimeSeconds();
                long endTs = Math.Max(startTs + 1, b.BucketEndUtc.ToUnixTimeSeconds());

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO smart_samples_raw (
                            device_id, bucket_start_ts, bucket_end_ts,
                            total_bytes_written, total_bytes_read,
                            delta_bytes_written, delta_bytes_read,
                            avg_temp_c, max_temp_c,
                            percent_used, avail_spare, unsafe_shutdowns, media_errors, is_spike
                        ) VALUES (
                            $id, $start, $end,
                            $tbw, $tbr,
                            $dw, $dr,
                            $avgT, $maxT,
                            $pu, $sp, $us, $me, $spike
                        )
                        ON CONFLICT(device_id, bucket_start_ts) DO UPDATE SET
                            bucket_end_ts = MAX(smart_samples_raw.bucket_end_ts, excluded.bucket_end_ts),
                            total_bytes_written = MAX(smart_samples_raw.total_bytes_written, excluded.total_bytes_written),
                            total_bytes_read = MAX(smart_samples_raw.total_bytes_read, excluded.total_bytes_read),
                            delta_bytes_written = smart_samples_raw.delta_bytes_written + excluded.delta_bytes_written,
                            delta_bytes_read = smart_samples_raw.delta_bytes_read + excluded.delta_bytes_read,
                            avg_temp_c = (smart_samples_raw.avg_temp_c + excluded.avg_temp_c) / 2.0,
                            max_temp_c = MAX(smart_samples_raw.max_temp_c, excluded.max_temp_c),
                            percent_used = MAX(smart_samples_raw.percent_used, excluded.percent_used),
                            avail_spare = MIN(smart_samples_raw.avail_spare, excluded.avail_spare),
                            unsafe_shutdowns = MAX(smart_samples_raw.unsafe_shutdowns, excluded.unsafe_shutdowns),
                            media_errors = MAX(smart_samples_raw.media_errors, excluded.media_errors),
                            is_spike = MAX(smart_samples_raw.is_spike, excluded.is_spike);
                        """;
                    cmd.Parameters.AddWithValue("$id", b.DeviceId);
                    cmd.Parameters.AddWithValue("$start", startTs);
                    cmd.Parameters.AddWithValue("$end", endTs);
                    cmd.Parameters.AddWithValue("$tbw", b.EndBytesWritten);
                    cmd.Parameters.AddWithValue("$tbr", b.EndBytesRead);
                    cmd.Parameters.AddWithValue("$dw", b.DeltaBytesWritten);
                    cmd.Parameters.AddWithValue("$dr", b.DeltaBytesRead);
                    cmd.Parameters.AddWithValue("$avgT", b.AvgTempCelsius);
                    cmd.Parameters.AddWithValue("$maxT", b.MaxTempCelsius);
                    cmd.Parameters.AddWithValue("$pu", b.PercentUsed);
                    cmd.Parameters.AddWithValue("$sp", b.AvailableSpare);
                    cmd.Parameters.AddWithValue("$us", b.UnsafeShutdowns);
                    cmd.Parameters.AddWithValue("$me", b.MediaErrors);
                    cmd.Parameters.AddWithValue("$spike", b.IsAnomalySpike ? 1 : 0);
                    cmd.ExecuteNonQuery();
                }

                // Also update hourly & daily rollups incrementally in the same transaction!
                long hourStartTs = startTs - (startTs % 3600);
                long dayStartTs = startTs - (startTs % 86400);
                UpsertRollupRow(conn, tx, "smart_samples_hourly", "hour_start_ts", hourStartTs, b);
                UpsertRollupRow(conn, tx, "smart_samples_daily", "day_start_ts", dayStartTs, b);

                foreach (var p in b.ProcessTotals.OrderByDescending(x => x.Value.BytesWritten).Take(20))
                {
                    if (p.Value.BytesWritten <= 0 && p.Value.BytesRead <= 0)
                    {
                        continue;
                    }

                    UpsertProcessRow(conn, tx, "process_writes_raw", "bucket_start_ts", startTs, b.DeviceId, p.Key, p.Value.ExePath, p.Value.BytesWritten, p.Value.BytesRead);
                    UpsertProcessRow(conn, tx, "process_writes_hourly", "hour_start_ts", hourStartTs, b.DeviceId, p.Key, p.Value.ExePath, p.Value.BytesWritten, p.Value.BytesRead);
                    UpsertProcessRow(conn, tx, "process_writes_daily", "day_start_ts", dayStartTs, b.DeviceId, p.Key, p.Value.ExePath, p.Value.BytesWritten, p.Value.BytesRead);
                }
            }

            foreach (var alert in newAlerts)
            {
                using var aCmd = conn.CreateCommand();
                aCmd.Transaction = tx;
                aCmd.CommandText = """
                    INSERT INTO alerts (ts, device_id, severity, category, title, message, acknowledged)
                    VALUES ($ts, $id, $sev, $cat, $title, $msg, 0);
                    """;
                aCmd.Parameters.AddWithValue("$ts", alert.TimestampUtc.ToUnixTimeSeconds());
                aCmd.Parameters.AddWithValue("$id", alert.DeviceId);
                aCmd.Parameters.AddWithValue("$sev", alert.Severity);
                aCmd.Parameters.AddWithValue("$cat", alert.Category);
                aCmd.Parameters.AddWithValue("$title", alert.Title);
                aCmd.Parameters.AddWithValue("$msg", alert.Message);
                aCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void RunRetentionCleanup()
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long rawCutoff = now - 7L * 86400L;      // 7 days raw
            long hourlyCutoff = now - 365L * 86400L; // 1 year hourly (daily kept forever)

            ExecuteDeleteCutoff(conn, tx, "DELETE FROM smart_samples_raw WHERE bucket_start_ts < $cut;", rawCutoff);
            ExecuteDeleteCutoff(conn, tx, "DELETE FROM process_writes_raw WHERE bucket_start_ts < $cut;", rawCutoff);
            ExecuteDeleteCutoff(conn, tx, "DELETE FROM smart_samples_hourly WHERE hour_start_ts < $cut;", hourlyCutoff);
            ExecuteDeleteCutoff(conn, tx, "DELETE FROM process_writes_hourly WHERE hour_start_ts < $cut;", hourlyCutoff);

            tx.Commit();
        }
    }

    public TimeRangeInspection InspectRange(
        string deviceId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        IReadOnlyList<BufferedBucket>? unflushedBuckets = null)
    {
        var span = endUtc - startUtc;
        string resolution = span.TotalDays <= 7.5
            ? "raw"
            : span.TotalDays <= 95
                ? "hourly"
                : "daily";

        string sampleTable = resolution switch
        {
            "raw" => "smart_samples_raw",
            "hourly" => "smart_samples_hourly",
            _ => "smart_samples_daily"
        };

        string tsCol = resolution switch
        {
            "raw" => "bucket_start_ts",
            "hourly" => "hour_start_ts",
            _ => "day_start_ts"
        };

        string procTable = resolution switch
        {
            "raw" => "process_writes_raw",
            "hourly" => "process_writes_hourly",
            _ => "process_writes_daily"
        };

        long startTs = startUtc.ToUnixTimeSeconds();
        long endTs = endUtc.ToUnixTimeSeconds();

        var points = new List<TimeSeriesPoint>();
        var processMap = new Dictionary<string, (string ExePath, long Written, long Read)>(StringComparer.OrdinalIgnoreCase);
        var alerts = new List<AlertEvent>();

        lock (_sync)
        {
            using var conn = OpenConnection();

            string endExpr = resolution == "raw" ? "bucket_end_ts" : $"{tsCol} + {(resolution == "hourly" ? 3600 : 86400)}";
            string spikeCol = resolution == "raw" ? "is_spike" : "has_spike";

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT {tsCol}, {endExpr}, total_bytes_written, total_bytes_read,
                           delta_bytes_written, delta_bytes_read, avg_temp_c, max_temp_c,
                           percent_used, avail_spare, unsafe_shutdowns, media_errors, {spikeCol}
                    FROM {sampleTable}
                    WHERE device_id = $id AND {tsCol} >= $start AND {tsCol} <= $end
                    ORDER BY {tsCol} ASC;
                    """;
                cmd.Parameters.AddWithValue("$id", deviceId);
                cmd.Parameters.AddWithValue("$start", startTs);
                cmd.Parameters.AddWithValue("$end", endTs);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    points.Add(new TimeSeriesPoint
                    {
                        TimestampUtc = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)),
                        EndTimestampUtc = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)),
                        TotalBytesWritten = r.GetInt64(2),
                        TotalBytesRead = r.GetInt64(3),
                        DeltaBytesWritten = r.GetInt64(4),
                        DeltaBytesRead = r.GetInt64(5),
                        AvgTempCelsius = r.GetDouble(6),
                        MaxTempCelsius = r.GetDouble(7),
                        PercentUsed = r.GetInt32(8),
                        AvailableSpare = r.GetInt32(9),
                        UnsafeShutdowns = r.GetInt64(10),
                        MediaErrors = r.GetInt64(11),
                        IsAnomalySpike = r.GetInt32(12) != 0
                    });
                }
            }

            using (var pCmd = conn.CreateCommand())
            {
                pCmd.CommandText = $"""
                    SELECT process_name, MAX(exe_path), SUM(bytes_written), SUM(bytes_read)
                    FROM {procTable}
                    WHERE device_id = $id AND {tsCol} >= $start AND {tsCol} <= $end
                    GROUP BY process_name
                    ORDER BY SUM(bytes_written) DESC
                    LIMIT 25;
                    """;
                pCmd.Parameters.AddWithValue("$id", deviceId);
                pCmd.Parameters.AddWithValue("$start", startTs);
                pCmd.Parameters.AddWithValue("$end", endTs);

                using var pr = pCmd.ExecuteReader();
                while (pr.Read())
                {
                    var pName = pr.GetString(0);
                    var exe = pr.IsDBNull(1) ? "" : pr.GetString(1);
                    long bw = pr.GetInt64(2);
                    long br = pr.GetInt64(3);
                    processMap[pName] = (exe, bw, br);
                }
            }

            using (var aCmd = conn.CreateCommand())
            {
                aCmd.CommandText = """
                    SELECT id, ts, device_id, severity, category, title, message, acknowledged
                    FROM alerts
                    WHERE device_id = $id AND ts >= $start AND ts <= $end
                    ORDER BY ts DESC
                    LIMIT 50;
                    """;
                aCmd.Parameters.AddWithValue("$id", deviceId);
                aCmd.Parameters.AddWithValue("$start", startTs);
                aCmd.Parameters.AddWithValue("$end", endTs);

                using var ar = aCmd.ExecuteReader();
                while (ar.Read())
                {
                    alerts.Add(ReadAlertRow(ar));
                }
            }
        }

        // Overlay unflushed in-memory buckets so real-time inspection is always up-to-the-second
        if (unflushedBuckets is { Count: > 0 })
        {
            foreach (var b in unflushedBuckets.Where(x => x.DeviceId == deviceId && x.BucketEndUtc >= startUtc && x.BucketStartUtc <= endUtc))
            {
                var existingIdx = points.FindIndex(p => p.TimestampUtc == b.BucketStartUtc);
                var pt = new TimeSeriesPoint
                {
                    TimestampUtc = b.BucketStartUtc,
                    EndTimestampUtc = b.BucketEndUtc,
                    TotalBytesWritten = b.EndBytesWritten,
                    TotalBytesRead = b.EndBytesRead,
                    DeltaBytesWritten = b.DeltaBytesWritten,
                    DeltaBytesRead = b.DeltaBytesRead,
                    AvgTempCelsius = b.AvgTempCelsius,
                    MaxTempCelsius = b.MaxTempCelsius,
                    PercentUsed = b.PercentUsed,
                    AvailableSpare = b.AvailableSpare,
                    UnsafeShutdowns = b.UnsafeShutdowns,
                    MediaErrors = b.MediaErrors,
                    IsAnomalySpike = b.IsAnomalySpike
                };

                if (existingIdx >= 0)
                {
                    points[existingIdx] = pt;
                }
                else
                {
                    points.Add(pt);
                }

                foreach (var kv in b.ProcessTotals)
                {
                    if (processMap.TryGetValue(kv.Key, out var cur))
                    {
                        processMap[kv.Key] = (string.IsNullOrEmpty(cur.ExePath) ? kv.Value.ExePath : cur.ExePath,
                            cur.Written + kv.Value.BytesWritten,
                            cur.Read + kv.Value.BytesRead);
                    }
                    else
                    {
                        processMap[kv.Key] = (kv.Value.ExePath, kv.Value.BytesWritten, kv.Value.BytesRead);
                    }
                }
            }

            points.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        }

        long deltaWrittenSum = points.Sum(p => p.DeltaBytesWritten);
        long deltaReadSum = points.Sum(p => p.DeltaBytesRead);
        long startTbw = points.Count > 0 ? Math.Max(0, points[0].TotalBytesWritten - points[0].DeltaBytesWritten) : 0;
        long endTbw = points.Count > 0 ? points[^1].TotalBytesWritten : 0;
        if (endTbw > startTbw && points.Count >= 2)
        {
            deltaWrittenSum = Math.Max(deltaWrittenSum, endTbw - startTbw);
        }

        long totalProcWritten = processMap.Values.Sum(x => x.Written);
        long unattributed = deltaWrittenSum > totalProcWritten ? deltaWrittenSum - totalProcWritten : 0;
        if (unattributed > 1_048_576) // > 1 MB unaccounted
        {
            const string sysName = "System & Unattributed I/O";
            if (processMap.TryGetValue(sysName, out var existing))
            {
                processMap[sysName] = (existing.ExePath, existing.Written + unattributed, existing.Read);
            }
            else
            {
                processMap[sysName] = ("kernel pagecache, journal, swap & unprivileged daemons", unattributed, 0);
            }
        }

        long denom = Math.Max(deltaWrittenSum, processMap.Values.Sum(x => x.Written));

        var topProcs = processMap
            .Where(x => x.Value.Written > 0 || x.Value.Read > 0)
            .OrderByDescending(x => x.Value.Written)
            .Take(20)
            .Select(x => new ProcessWriteSummary
            {
                ProcessName = x.Key,
                ExecutablePath = x.Value.ExePath,
                BytesWritten = x.Value.Written,
                BytesRead = x.Value.Read,
                SharePercentage = denom > 0 ? Math.Min(100.0, Math.Round(x.Value.Written * 100.0 / denom, 1)) : 0
            })
            .ToList();

        double elapsedSec = points.Count > 0
            ? Math.Max(1.0, (points[^1].EndTimestampUtc - points[0].TimestampUtc).TotalSeconds)
            : Math.Max(1.0, (endUtc - startUtc).TotalSeconds);

        return new TimeRangeInspection
        {
            DeviceId = deviceId,
            StartUtc = startUtc,
            EndUtc = endUtc,
            Resolution = resolution,
            TotalBytesWrittenDelta = deltaWrittenSum,
            TotalBytesReadDelta = deltaReadSum,
            StartTbwBytes = startTbw,
            EndTbwBytes = endTbw,
            PeakTempCelsius = points.Count > 0 ? points.Max(p => p.MaxTempCelsius) : 0,
            AvgTempCelsius = points.Count > 0 ? Math.Round(points.Average(p => p.AvgTempCelsius), 1) : 0,
            AvgWriteRateBytesPerSec = deltaWrittenSum / elapsedSec,
            PeakBucketWriteBytes = points.Count > 0 ? points.Max(p => p.DeltaBytesWritten) : 0,
            Points = points,
            TopProcesses = topProcs,
            AlertsInWindow = alerts
        };
    }

    public (long TotalBytesWrittenDelta, long TotalBytesReadDelta) GetTodayTransferTotals(string deviceId, IReadOnlyList<BufferedBucket>? unflushed = null)
    {
        var nowLocal = DateTimeOffset.Now;
        var startOfLocalDayUtc = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset).ToUniversalTime();
        long startTs = startOfLocalDayUtc.ToUnixTimeSeconds();
        long endTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;

        long dbW = 0, dbR = 0;
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(SUM(delta_bytes_written), 0), COALESCE(SUM(delta_bytes_read), 0)
                FROM smart_samples_raw
                WHERE device_id = $id AND bucket_start_ts >= $start AND bucket_start_ts <= $end;
                """;
            cmd.Parameters.AddWithValue("$id", deviceId);
            cmd.Parameters.AddWithValue("$start", startTs);
            cmd.Parameters.AddWithValue("$end", endTs);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                dbW = r.GetInt64(0);
                dbR = r.GetInt64(1);
            }
        }

        if (unflushed is { Count: > 0 })
        {
            foreach (var b in unflushed.Where(x => x.DeviceId == deviceId && x.BucketStartUtc >= startOfLocalDayUtc))
            {
                dbW += b.DeltaBytesWritten;
                dbR += b.DeltaBytesRead;
            }
        }

        return (dbW, dbR);
    }

    public Dictionary<string, SmartSnapshot> LoadLatestSmartSnapshots()
    {
        var map = new Dictionary<string, SmartSnapshot>(StringComparer.Ordinal);
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT device_id, snapshot_json FROM latest_smart_state;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                try
                {
                    var snap = JsonSerializer.Deserialize<SmartSnapshot>(r.GetString(1));
                    if (snap is not null)
                    {
                        map[r.GetString(0)] = snap;
                    }
                }
                catch
                {
                    // Skip corrupt JSON
                }
            }
        }

        return map;
    }

    public List<AlertEvent> GetRecentAlerts(int limit = 50)
    {
        var list = new List<AlertEvent>();
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, ts, device_id, severity, category, title, message, acknowledged
                FROM alerts
                ORDER BY ts DESC
                LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(ReadAlertRow(r));
            }
        }

        return list;
    }

    public void AcknowledgeAllAlerts()
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE alerts SET acknowledged = 1 WHERE acknowledged = 0;";
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearAlerts()
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM alerts;";
            cmd.ExecuteNonQuery();
        }
    }

    public DaemonSettings LoadSettings()
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value_json FROM settings_kv WHERE key = 'daemon_settings';";
            var val = cmd.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(val))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<DaemonSettings>(val);
                    if (parsed is not null)
                    {
                        return parsed;
                    }
                }
                catch
                {
                    // Return defaults
                }
            }
        }

        return new DaemonSettings();
    }

    public void SaveSettings(DaemonSettings settings)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO settings_kv (key, value_json)
                VALUES ('daemon_settings', $json)
                ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json;
                """;
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings));
            cmd.ExecuteNonQuery();
        }
    }

    public long GetTotalStoredSnapshotCount()
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM smart_samples_raw) +
                    (SELECT COUNT(*) FROM smart_samples_hourly) +
                    (SELECT COUNT(*) FROM smart_samples_daily);
                """;
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
    }

    public long GetDatabaseFileSizeBytes()
    {
        long size = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + suffix;
            if (File.Exists(p))
            {
                size += new FileInfo(p).Length;
            }
        }

        return size;
    }

    private static void UpsertRollupRow(
        SqliteConnection conn,
        SqliteTransaction tx,
        string table,
        string tsColumn,
        long bucketTs,
        BufferedBucket b)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {table} (
                device_id, {tsColumn},
                total_bytes_written, total_bytes_read,
                delta_bytes_written, delta_bytes_read,
                avg_temp_c, max_temp_c,
                percent_used, avail_spare, unsafe_shutdowns, media_errors, has_spike
            ) VALUES (
                $id, $ts,
                $tbw, $tbr,
                $dw, $dr,
                $avgT, $maxT,
                $pu, $sp, $us, $me, $spike
            )
            ON CONFLICT(device_id, {tsColumn}) DO UPDATE SET
                total_bytes_written = MAX({table}.total_bytes_written, excluded.total_bytes_written),
                total_bytes_read = MAX({table}.total_bytes_read, excluded.total_bytes_read),
                delta_bytes_written = {table}.delta_bytes_written + excluded.delta_bytes_written,
                delta_bytes_read = {table}.delta_bytes_read + excluded.delta_bytes_read,
                avg_temp_c = ROUND(({table}.avg_temp_c + excluded.avg_temp_c) / 2.0, 1),
                max_temp_c = MAX({table}.max_temp_c, excluded.max_temp_c),
                percent_used = MAX({table}.percent_used, excluded.percent_used),
                avail_spare = MIN({table}.avail_spare, excluded.avail_spare),
                unsafe_shutdowns = MAX({table}.unsafe_shutdowns, excluded.unsafe_shutdowns),
                media_errors = MAX({table}.media_errors, excluded.media_errors),
                has_spike = MAX({table}.has_spike, excluded.has_spike);
            """;
        cmd.Parameters.AddWithValue("$id", b.DeviceId);
        cmd.Parameters.AddWithValue("$ts", bucketTs);
        cmd.Parameters.AddWithValue("$tbw", b.EndBytesWritten);
        cmd.Parameters.AddWithValue("$tbr", b.EndBytesRead);
        cmd.Parameters.AddWithValue("$dw", b.DeltaBytesWritten);
        cmd.Parameters.AddWithValue("$dr", b.DeltaBytesRead);
        cmd.Parameters.AddWithValue("$avgT", b.AvgTempCelsius);
        cmd.Parameters.AddWithValue("$maxT", b.MaxTempCelsius);
        cmd.Parameters.AddWithValue("$pu", b.PercentUsed);
        cmd.Parameters.AddWithValue("$sp", b.AvailableSpare);
        cmd.Parameters.AddWithValue("$us", b.UnsafeShutdowns);
        cmd.Parameters.AddWithValue("$me", b.MediaErrors);
        cmd.Parameters.AddWithValue("$spike", b.IsAnomalySpike ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertProcessRow(
        SqliteConnection conn,
        SqliteTransaction tx,
        string table,
        string tsColumn,
        long bucketTs,
        string deviceId,
        string processName,
        string exePath,
        long written,
        long read)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {table} (device_id, {tsColumn}, process_name, exe_path, bytes_written, bytes_read)
            VALUES ($id, $ts, $name, $exe, $bw, $br)
            ON CONFLICT(device_id, {tsColumn}, process_name) DO UPDATE SET
                exe_path = CASE WHEN excluded.exe_path <> '' THEN excluded.exe_path ELSE {table}.exe_path END,
                bytes_written = {table}.bytes_written + excluded.bytes_written,
                bytes_read = {table}.bytes_read + excluded.bytes_read;
            """;
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.Parameters.AddWithValue("$ts", bucketTs);
        cmd.Parameters.AddWithValue("$name", processName);
        cmd.Parameters.AddWithValue("$exe", exePath ?? string.Empty);
        cmd.Parameters.AddWithValue("$bw", written);
        cmd.Parameters.AddWithValue("$br", read);
        cmd.ExecuteNonQuery();
    }

    private static void ExecuteDeleteCutoff(SqliteConnection conn, SqliteTransaction tx, string sql, long cutoff)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$cut", cutoff);
        cmd.ExecuteNonQuery();
    }

    private static AlertEvent ReadAlertRow(SqliteDataReader r) =>
        new()
        {
            Id = r.GetInt64(0),
            TimestampUtc = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)),
            DeviceId = r.GetString(2),
            Severity = r.GetString(3),
            Category = r.GetString(4),
            Title = r.GetString(5),
            Message = r.GetString(6),
            Acknowledged = r.GetInt32(7) != 0
        };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
