using System.Diagnostics;
using NvmeSentinel.Core.Hardware;
using NvmeSentinel.Core.Ipc;
using NvmeSentinel.Core.Models;
using NvmeSentinel.Core.Services;
using NvmeSentinel.Core.Storage;

namespace NvmeSentinel.Daemon;

public sealed class SentinelMonitorEngine : IAsyncDisposable
{
    private readonly INvmeProvider _nvmeProvider;
    private readonly IProcessIoTracker _processTracker;
    private readonly TimeSeriesStore _store;
    private readonly SentinelIpcServer _ipcServer;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly object _stateLock = new();

    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private List<NvmeDeviceInfo> _devices = [];
    private Dictionary<string, SmartSnapshot> _latestSmart = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LiveTelemetry> _latestLive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlockIoCounters> _prevBlock = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _unpolledBlockWriteAccum = new(StringComparer.Ordinal);
    private readonly Dictionary<(string DeviceId, long BucketStartTs), BufferedBucket> _bufferedBuckets = new();
    private readonly Dictionary<string, int> _consecutiveSpikeCounts = new(StringComparer.Ordinal);
    private readonly List<AlertEvent> _pendingAlerts = [];
    private readonly Dictionary<string, DateTimeOffset> _thermalAlertCooldown = new(StringComparer.Ordinal);

    private DateTimeOffset _lastProcessScanUtc = DateTimeOffset.MinValue;
    private readonly Dictionary<string, (long WrittenBaseline, long ReadBaseline, DateOnly Day)> _todayBaselines = new(StringComparer.Ordinal);
    private DaemonSettings _settings = new();
    private DateTimeOffset? _lastFlushUtc;
    private DateTimeOffset _lastSmartPollUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRetentionUtc = DateTimeOffset.MinValue;
    private Task? _loopTask;

    public event Action? ShutdownRequested;

    public SentinelMonitorEngine(
        INvmeProvider? nvmeProvider = null,
        IProcessIoTracker? processTracker = null,
        TimeSeriesStore? store = null,
        string? customPipeName = null)
    {
        _nvmeProvider = nvmeProvider ?? HardwareFactory.CreateNvmeProvider();
        _processTracker = processTracker ?? HardwareFactory.CreateProcessTracker();
        _store = store ?? new TimeSeriesStore();
        _ipcServer = new SentinelIpcServer(HandleIpcRequestAsync, customPipeName);
    }

    public async Task InitializeAndStartAsync(CancellationToken ct = default)
    {
        _settings = _store.LoadSettings();
        if (!string.IsNullOrWhiteSpace(_settings.CustomDatabaseDirectory))
        {
            AppPaths.CustomDataDirectory = _settings.CustomDatabaseDirectory;
        }
        _settings.StartDaemonAtLogin = AutostartManager.IsAutostartEnabled();

        var persistedSmart = _store.LoadLatestSmartSnapshots();
        _devices = await _nvmeProvider.DiscoverDevicesAsync(ct);
        if (_devices.Count == 0)
        {
            var stored = _store.LoadDevices();
            if (stored.Count > 0)
            {
                _devices = stored;
            }
        }
        else
        {
            _store.UpsertDevices(_devices);
        }

        // Prime process tracker baseline
        _processTracker.Poll(1.0, 0);

        foreach (var dev in _devices)
        {
            var snap = await _nvmeProvider.ReadSmartAsync(dev, forceRefresh: true, ct);
            if (snap is null && persistedSmart.TryGetValue(dev.DeviceId, out var cachedSnap))
            {
                snap = cachedSnap;
            }

            if (snap is not null)
            {
                snap.TriggerReason = "startup";
                if (persistedSmart.TryGetValue(dev.DeviceId, out var prev) && !ReferenceEquals(prev, snap))
                {
                    EvaluateSmartTransitions(dev, prev, snap);
                }

                _latestSmart[dev.DeviceId] = snap;
                EnsureBaselineSample(dev, snap);
            }
            else if (persistedSmart.TryGetValue(dev.DeviceId, out var cached))
            {
                _latestSmart[dev.DeviceId] = cached;
            }

            _prevBlock[dev.DeviceId] = _nvmeProvider.ReadLiveBlockCounters(dev);
            _unpolledBlockWriteAccum[dev.DeviceId] = 0;
        }

        _lastSmartPollUtc = DateTimeOffset.UtcNow;
        FlushToDatabase();

        _ipcServer.Start();
        _loopTask = Task.Run(() => RunTelemetryLoopAsync(_stopCts.Token), CancellationToken.None);
    }

    private void EnsureBaselineSample(NvmeDeviceInfo dev, SmartSnapshot snap)
    {
        var now = DateTimeOffset.UtcNow;
        long startTs = AlignBucket5Min(now.ToUnixTimeSeconds());
        var bucket = new BufferedBucket
        {
            DeviceId = dev.DeviceId,
            BucketStartUtc = DateTimeOffset.FromUnixTimeSeconds(startTs),
            BucketEndUtc = now,
            StartBytesWritten = snap.TotalBytesWritten,
            EndBytesWritten = snap.TotalBytesWritten,
            StartBytesRead = snap.TotalBytesRead,
            EndBytesRead = snap.TotalBytesRead,
            TempSum = snap.CompositeTempCelsius,
            TempSamples = 1,
            MaxTempCelsius = snap.CompositeTempCelsius,
            PercentUsed = snap.PercentUsed,
            AvailableSpare = snap.AvailableSpare,
            UnsafeShutdowns = snap.UnsafeShutdowns,
            MediaErrors = snap.MediaErrors
        };
        _bufferedBuckets[(dev.DeviceId, startTs)] = bucket;
    }

    private async Task RunTelemetryLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var lastTick = DateTimeOffset.UtcNow;

        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = DateTimeOffset.UtcNow;
            double dt = Math.Clamp((now - lastTick).TotalSeconds, 0.25, 10.0);
            lastTick = now;

            try
            {
                await StepTickAsync(now, dt, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep daemon resilient across transient OS/device glitches
            }
        }
    }

    private async Task StepTickAsync(DateTimeOffset now, double dt, CancellationToken ct)
    {
        List<NvmeDeviceInfo> devices;
        DaemonSettings settings;
        lock (_stateLock)
        {
            devices = _devices.ToList();
            settings = _settings;
        }

        if (devices.Count == 0)
        {
            return;
        }

        // 1. Poll live block counters per drive
        long totalBlockWriteDeltaAllDrives = 0;
        var currentBlocks = new Dictionary<string, (BlockIoCounters Counters, long DRead, long DWrite, long DRops, long DWops)>(StringComparer.Ordinal);

        foreach (var dev in devices)
        {
            var cur = _nvmeProvider.ReadLiveBlockCounters(dev);
            long dRead = 0, dWrite = 0, dRops = 0, dWops = 0;

            if (_prevBlock.TryGetValue(dev.DeviceId, out var prev))
            {
                dRead = Math.Max(0, cur.TotalBytesRead - prev.TotalBytesRead);
                dWrite = Math.Max(0, cur.TotalBytesWritten - prev.TotalBytesWritten);
                dRops = Math.Max(0, cur.ReadCompletedOps - prev.ReadCompletedOps);
                dWops = Math.Max(0, cur.WriteCompletedOps - prev.WriteCompletedOps);
            }

            _prevBlock[dev.DeviceId] = cur;
            totalBlockWriteDeltaAllDrives += dWrite;
            currentBlocks[dev.DeviceId] = (cur, dRead, dWrite, dRops, dWops);
        }

        // 2. Poll per-process I/O attribution once across system
        // When running headless with zero disk writes, skip heavy /proc crawling to preserve CPU and battery
        bool needsProcessScan = _ipcServer.SubscriberCount > 0 || totalBlockWriteDeltaAllDrives > 65_536 || (now - _lastProcessScanUtc).TotalSeconds >= 10.0;
        ProcessPollResult procPoll;
        if (needsProcessScan)
        {
            _lastProcessScanUtc = now;
            procPoll = _processTracker.Poll(dt, totalBlockWriteDeltaAllDrives);
        }
        else
        {
            procPoll = new ProcessPollResult();
        }

        // 3. Periodically refresh NVMe SMART (faster when self-test is active)
        bool selfTestRunning;
        lock (_stateLock)
        {
            selfTestRunning = _latestSmart.Values.Any(s =>
                s.SelfTestPercentRemaining >= 0 ||
                s.SelfTestStatus.Contains("progress", StringComparison.OrdinalIgnoreCase));
        }

        int smartIntervalSec = selfTestRunning ? 8 : Math.Clamp(settings.SmartPollIntervalSeconds, 15, 600);
        if ((now - _lastSmartPollUtc).TotalSeconds >= smartIntervalSec)
        {
            _lastSmartPollUtc = now;
            foreach (var dev in devices)
            {
                var snap = await _nvmeProvider.ReadSmartAsync(dev, forceRefresh: false, ct);
                if (snap is null) continue;

                lock (_stateLock)
                {
                    if (_latestSmart.TryGetValue(dev.DeviceId, out var prevSnap))
                    {
                        EvaluateSmartTransitions(dev, prevSnap, snap);
                    }

                    _latestSmart[dev.DeviceId] = snap;
                    _unpolledBlockWriteAccum[dev.DeviceId] = 0;
                }
            }
        }

        // 4. Update RAM 5-minute bucket, check spikes/thermals, and build LiveTelemetry
        bool immediateFlushNeeded = false;
        long bucketStartTs = AlignBucket5Min(now.ToUnixTimeSeconds());
        var liveToBroadcast = new List<LiveTelemetry>();

        lock (_stateLock)
        {
            foreach (var dev in devices)
            {
                var (curBlock, dRead, dWrite, dRops, dWops) = currentBlocks[dev.DeviceId];
                _unpolledBlockWriteAccum[dev.DeviceId] = _unpolledBlockWriteAccum.GetValueOrDefault(dev.DeviceId) + dWrite;

                _latestSmart.TryGetValue(dev.DeviceId, out var smart);
                double compositeTemp = curBlock.CompositeTempCelsius > 0
                    ? curBlock.CompositeTempCelsius
                    : smart?.CompositeTempCelsius ?? 0;

                long estimatedTbw = (smart?.TotalBytesWritten ?? 0) + _unpolledBlockWriteAccum[dev.DeviceId];
                long estimatedTbr = (smart?.TotalBytesRead ?? 0) + dRead;

                var key = (dev.DeviceId, bucketStartTs);
                if (!_bufferedBuckets.TryGetValue(key, out var bucket))
                {
                    // Check previous bucket for this device: if it was normal, reset consecutive spike tracker
                    var prevBucket = _bufferedBuckets.Values
                        .Where(b => b.DeviceId == dev.DeviceId && b.BucketStartUtc < DateTimeOffset.FromUnixTimeSeconds(bucketStartTs))
                        .OrderByDescending(b => b.BucketStartUtc)
                        .FirstOrDefault();

                    if (prevBucket is not null && !prevBucket.IsAnomalySpike)
                    {
                        _consecutiveSpikeCounts[dev.DeviceId] = 0;
                    }

                    bucket = new BufferedBucket
                    {
                        DeviceId = dev.DeviceId,
                        BucketStartUtc = DateTimeOffset.FromUnixTimeSeconds(bucketStartTs),
                        BucketEndUtc = now,
                        StartBytesWritten = estimatedTbw - dWrite,
                        EndBytesWritten = estimatedTbw,
                        StartBytesRead = estimatedTbr - dRead,
                        EndBytesRead = estimatedTbr,
                        MaxTempCelsius = compositeTemp,
                        PercentUsed = smart?.PercentUsed ?? 0,
                        AvailableSpare = smart?.AvailableSpare ?? 100,
                        UnsafeShutdowns = smart?.UnsafeShutdowns ?? 0,
                        MediaErrors = smart?.MediaErrors ?? 0
                    };
                    _bufferedBuckets[key] = bucket;
                }

                bucket.BucketEndUtc = now;
                bucket.EndBytesWritten = Math.Max(bucket.EndBytesWritten, estimatedTbw);
                bucket.EndBytesRead = Math.Max(bucket.EndBytesRead, estimatedTbr);
                bucket.DeltaBytesWritten += dWrite;
                bucket.DeltaBytesRead += dRead;
                if (compositeTemp > 0)
                {
                    bucket.TempSum += compositeTemp;
                    bucket.TempSamples++;
                    bucket.MaxTempCelsius = Math.Max(bucket.MaxTempCelsius, compositeTemp);
                }

                if (smart is not null)
                {
                    bucket.PercentUsed = smart.PercentUsed;
                    bucket.AvailableSpare = smart.AvailableSpare;
                    bucket.UnsafeShutdowns = smart.UnsafeShutdowns;
                    bucket.MediaErrors = smart.MediaErrors;
                }

                // Attribute process writes proportionally when single drive or primary drive
                double driveShare = totalBlockWriteDeltaAllDrives > 0
                    ? (double)dWrite / totalBlockWriteDeltaAllDrives
                    : (devices.Count == 1 ? 1.0 : 0.0);

                if (driveShare > 0)
                {
                    foreach (var pd in procPoll.IntervalDeltas)
                    {
                        long attrW = (long)Math.Round(pd.WriteBytesDelta * driveShare);
                        long attrR = (long)Math.Round(pd.ReadBytesDelta * driveShare);
                        if (attrW <= 0 && attrR <= 0) continue;

                        if (bucket.ProcessTotals.TryGetValue(pd.ProcessName, out var curProc))
                        {
                            bucket.ProcessTotals[pd.ProcessName] = (
                                string.IsNullOrEmpty(curProc.ExePath) ? pd.ExecutablePath : curProc.ExePath,
                                curProc.BytesWritten + attrW,
                                curProc.BytesRead + attrR);
                        }
                        else
                        {
                            bucket.ProcessTotals[pd.ProcessName] = (pd.ExecutablePath, attrW, attrR);
                        }
                    }
                }

                // Anomaly Check 1: Runaway write spike in 5-min window
                var (ratedTbwBytes, _) = RatedTbwEstimator.Estimate(dev, smart, settings);
                double effectiveThresholdGb = SpikeThresholdCalculator.ResolveEffectiveThresholdGb(settings, ratedTbwBytes);
                long spikeBytesThreshold = (long)(Math.Max(0.5, effectiveThresholdGb) * 1_000_000_000.0);

                if (!bucket.IsAnomalySpike && bucket.DeltaBytesWritten >= spikeBytesThreshold)
                {
                    bucket.IsAnomalySpike = true;
                    int consecutive = _consecutiveSpikeCounts.GetValueOrDefault(dev.DeviceId, 0) + 1;
                    _consecutiveSpikeCounts[dev.DeviceId] = consecutive;

                    var topCulprit = bucket.ProcessTotals
                        .OrderByDescending(x => x.Value.BytesWritten)
                        .FirstOrDefault();

                    string culpritText = !string.IsNullOrEmpty(topCulprit.Key)
                        ? $" Top writer: {topCulprit.Key} ({ByteFormatter.FormatBytes(topCulprit.Value.BytesWritten)})."
                        : string.Empty;

                    // Calculate total whitelisted write share in this bucket
                    long whitelistedBytes = bucket.ProcessTotals
                        .Where(p => SpikeThresholdCalculator.IsProcessWhitelisted(p.Key, p.Value.ExePath, settings.ProcessWhitelist))
                        .Sum(p => p.Value.BytesWritten);
                    double whitelistedShare = bucket.DeltaBytesWritten > 0 ? (double)whitelistedBytes / bucket.DeltaBytesWritten : 0.0;
                    bool primaryWhitelisted = whitelistedShare >= 0.65;

                    // Critical emergency threshold: >= 50 GB / 5 min or >= 3x threshold
                    long emergencyCriticalBytes = Math.Max(50_000_000_000L, spikeBytesThreshold * 3);
                    bool isEmergencyCritical = bucket.DeltaBytesWritten >= emergencyCriticalBytes;

                    int requiredCycles = Math.Clamp(settings.SustainedSpikeThresholdCycles, 1, 5);

                    if (primaryWhitelisted && !isEmergencyCritical)
                    {
                        RaiseAlertLocked(new AlertEvent
                        {
                            TimestampUtc = now,
                            DeviceId = dev.DeviceId,
                            Severity = "Info",
                            Category = "ExpectedWorkload",
                            Title = "Whitelisted Process Write Burst",
                            Message = $"{dev.Model} wrote {ByteFormatter.FormatBytes(bucket.DeltaBytesWritten)} within 5 min (whitelisted process {topCulprit.Key}). Desktop alert suppressed."
                        });
                    }
                    else if (isEmergencyCritical)
                    {
                        RaiseAlertLocked(new AlertEvent
                        {
                            TimestampUtc = now,
                            DeviceId = dev.DeviceId,
                            Severity = "Critical",
                            Category = "CriticalRunawayWrite",
                            Title = "Critical Runaway Write Burst",
                            Message = $"{dev.Model} wrote {ByteFormatter.FormatBytes(bucket.DeltaBytesWritten)} in 5 min (Critical emergency rate).{culpritText}"
                        });
                        immediateFlushNeeded = true;
                    }
                    else if (consecutive >= requiredCycles)
                    {
                        RaiseAlertLocked(new AlertEvent
                        {
                            TimestampUtc = now,
                            DeviceId = dev.DeviceId,
                            Severity = "Warning",
                            Category = "SustainedRunawayWrite",
                            Title = $"Sustained Write Spike ({consecutive} Intervals)",
                            Message = $"{dev.Model} wrote {ByteFormatter.FormatBytes(bucket.DeltaBytesWritten)} in 5 min (sustained over {consecutive} intervals).{culpritText}"
                        });
                        immediateFlushNeeded = true;
                    }
                    else
                    {
                        RaiseAlertLocked(new AlertEvent
                        {
                            TimestampUtc = now,
                            DeviceId = dev.DeviceId,
                            Severity = "Info",
                            Category = "TransientBurst",
                            Title = $"Transient Write Burst (Cycle {consecutive}/{requiredCycles})",
                            Message = $"{dev.Model} wrote {ByteFormatter.FormatBytes(bucket.DeltaBytesWritten)} in 5 min. Monitoring for sustained write pattern.{culpritText}"
                        });
                    }
                }

                // Anomaly Check 2: Composite Temperature thresholds (15-min cooldown)
                if (compositeTemp >= settings.WarningTempCelsius)
                {
                    if (!_thermalAlertCooldown.TryGetValue(dev.DeviceId, out var lastAlert) ||
                        (now - lastAlert).TotalMinutes >= 15)
                    {
                        _thermalAlertCooldown[dev.DeviceId] = now;
                        bool isCrit = compositeTemp >= settings.CriticalTempCelsius;
                        RaiseAlertLocked(new AlertEvent
                        {
                            TimestampUtc = now,
                            DeviceId = dev.DeviceId,
                            Severity = isCrit ? "Critical" : "Warning",
                            Category = "Temperature",
                            Title = isCrit ? "Critical NVMe Temperature" : "High NVMe Temperature",
                            Message = $"{dev.Model} composite sensor reached {compositeTemp:F1} °C."
                        });
                        immediateFlushNeeded = true;
                    }
                }

                long curUnflushedW = 0, curUnflushedR = 0;
                foreach (var b in _bufferedBuckets.Values)
                {
                    if (b.DeviceId == dev.DeviceId)
                    {
                        curUnflushedW += b.DeltaBytesWritten;
                        curUnflushedR += b.DeltaBytesRead;
                    }
                }
                var (todayWritten, todayRead) = GetCachedTodayTotals(dev.DeviceId, now, curUnflushedW, curUnflushedR);

                var live = new LiveTelemetry
                {
                    TimestampUtc = now,
                    DeviceId = dev.DeviceId,
                    ReadBytesPerSec = dRead / dt,
                    WriteBytesPerSec = dWrite / dt,
                    ReadIops = dRops / dt,
                    WriteIops = dWops / dt,
                    CompositeTempCelsius = compositeTemp,
                    Sensors = curBlock.Sensors.Count > 0 ? curBlock.Sensors : (smart?.Sensors ?? []),
                    PcieCurrentLinkSpeed = !string.IsNullOrEmpty(curBlock.PcieCurrentLinkSpeed) ? curBlock.PcieCurrentLinkSpeed : dev.PcieCurrentLinkSpeed,
                    PcieCurrentLinkWidth = curBlock.PcieCurrentLinkWidth > 0 ? curBlock.PcieCurrentLinkWidth : dev.PcieCurrentLinkWidth,
                    PowerState = curBlock.PowerState,
                    EstimatedCurrentTbwBytes = estimatedTbw,
                    TodayBytesWritten = todayWritten,
                    TodayBytesRead = todayRead,
                    TopActiveProcesses = procPoll.TopActiveProcesses
                };

                _latestLive[dev.DeviceId] = live;
                liveToBroadcast.Add(live);
            }

            if (_pendingAlerts.Count > 0)
            {
                immediateFlushNeeded = true;
            }
        }

        // 5. Batched SQLite flush (default every 10 mins, or immediately on spike/alert)
        int flushMins = Math.Clamp(settings.FlushIntervalMinutes, 1, 120);
        if (immediateFlushNeeded || _lastFlushUtc is null || (now - _lastFlushUtc.Value).TotalMinutes >= flushMins)
        {
            FlushToDatabase();
        }

        if ((now - _lastRetentionUtc).TotalHours >= 6)
        {
            _lastRetentionUtc = now;
            _store.RunRetentionCleanup();
        }

        // 6. Push live telemetry only if GUI subscribers are listening
        if (_ipcServer.SubscriberCount > 0)
        {
            foreach (var l in liveToBroadcast)
            {
                await _ipcServer.BroadcastLiveAsync(l);
            }
        }
    }

    private void EvaluateSmartTransitions(NvmeDeviceInfo dev, SmartSnapshot prev, SmartSnapshot cur)
    {
        if (cur.MediaErrors > prev.MediaErrors)
        {
            RaiseAlertLocked(new AlertEvent
            {
                TimestampUtc = cur.TimestampUtc,
                DeviceId = dev.DeviceId,
                Severity = "Critical",
                Category = "MediaError",
                Title = "NVMe Media Integrity Error",
                Message = $"{dev.Model} reported {cur.MediaErrors - prev.MediaErrors} new unrecovered media error(s) (Total: {cur.MediaErrors}). Back up critical data immediately."
            });
        }

        if (cur.UnsafeShutdowns > prev.UnsafeShutdowns)
        {
            long diff = cur.UnsafeShutdowns - prev.UnsafeShutdowns;
            // On Linux laptops and modern platforms, clean warm reboots trigger an ACPI PCIe rail power drop
            // before standard NVMe shutdown completion, incrementing the hardware counter by 1.
            // When detected at startup with diff == 1, log as Info without firing noisy warning alarms.
            bool isNormalRebootArtifact = cur.TriggerReason == "startup" && diff == 1;

            RaiseAlertLocked(new AlertEvent
            {
                TimestampUtc = cur.TimestampUtc,
                DeviceId = dev.DeviceId,
                Severity = isNormalRebootArtifact ? "Info" : "Warning",
                Category = "UnsafeShutdown",
                Title = isNormalRebootArtifact ? "System Reboot Power State Handled" : "Unsafe Shutdown Recorded",
                Message = isNormalRebootArtifact
                    ? $"{dev.Model} power-cycled (Normal reboot power-state transition recorded by hardware. Total: {cur.UnsafeShutdowns})."
                    : $"{dev.Model} logged {diff} new unexpected power loss event(s) (Total: {cur.UnsafeShutdowns})."
            });
        }

        if (cur.AvailableSpare <= cur.SpareThreshold && prev.AvailableSpare > prev.SpareThreshold)
        {
            RaiseAlertLocked(new AlertEvent
            {
                TimestampUtc = cur.TimestampUtc,
                DeviceId = dev.DeviceId,
                Severity = "Critical",
                Category = "SpareLow",
                Title = "Available Spare Below Threshold",
                Message = $"{dev.Model} spare capacity dropped to {cur.AvailableSpare}% (threshold {cur.SpareThreshold}%)."
            });
        }

        if (cur.CriticalWarningFlags != 0 && prev.CriticalWarningFlags == 0)
        {
            RaiseAlertLocked(new AlertEvent
            {
                TimestampUtc = cur.TimestampUtc,
                DeviceId = dev.DeviceId,
                Severity = "Critical",
                Category = "CriticalWarning",
                Title = "NVMe Critical Warning Flag Active",
                Message = $"{dev.Model} raised SMART critical warning bitmask 0x{cur.CriticalWarningFlags:X2}."
            });
        }
    }

    private void RaiseAlertLocked(AlertEvent alert)
    {
        _pendingAlerts.Add(alert);
        if (_settings.EnableDesktopNotifications && !alert.Severity.Equals("Info", StringComparison.OrdinalIgnoreCase))
        {
            NotificationDispatcher.SendDesktopNotification(alert);
        }
    }

    private (long Written, long Read) GetCachedTodayTotals(string devId, DateTimeOffset now, long currentUnflushedW, long currentUnflushedR)
    {
        var localDay = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        if (!_todayBaselines.TryGetValue(devId, out var baseEntry) || baseEntry.Day != localDay)
        {
            var dbTotals = _store.GetTodayTransferTotals(devId, null);
            baseEntry = (dbTotals.TotalBytesWrittenDelta, dbTotals.TotalBytesReadDelta, localDay);
            _todayBaselines[devId] = baseEntry;
        }

        return (baseEntry.WrittenBaseline + currentUnflushedW, baseEntry.ReadBaseline + currentUnflushedR);
    }

    public void FlushToDatabase()
    {
        List<BufferedBucket> bucketsToFlush;
        Dictionary<string, SmartSnapshot> smartCopy;
        List<AlertEvent> alertsToSave;

        var now = DateTimeOffset.UtcNow;
        long activeBucketStart = AlignBucket5Min(now.ToUnixTimeSeconds());

        lock (_stateLock)
        {
            _todayBaselines.Clear();
            bucketsToFlush = _bufferedBuckets.Values.Select(b => b.Clone()).ToList();
            smartCopy = new Dictionary<string, SmartSnapshot>(_latestSmart, StringComparer.Ordinal);
            alertsToSave = _pendingAlerts.ToList();
            _pendingAlerts.Clear();

            // Clear all completed buckets; reset deltas on currently active 5-min bucket so incremental flush never double-counts!
            var keysToRemove = _bufferedBuckets.Keys.Where(k => k.BucketStartTs < activeBucketStart).ToList();
            foreach (var k in keysToRemove)
            {
                _bufferedBuckets.Remove(k);
            }

            foreach (var active in _bufferedBuckets.Values)
            {
                active.DeltaBytesWritten = 0;
                active.DeltaBytesRead = 0;
                active.ProcessTotals.Clear();
            }

            _lastFlushUtc = now;
        }

        _store.FlushBucketsAndState(bucketsToFlush, smartCopy, alertsToSave);
    }

    public DashboardSnapshot BuildDashboardSnapshot()
    {
        List<NvmeDeviceInfo> devices;
        Dictionary<string, SmartSnapshot> smart;
        Dictionary<string, LiveTelemetry> live;
        List<BufferedBucket> unflushed;
        DaemonSettings settings;

        lock (_stateLock)
        {
            devices = _devices.ToList();
            smart = new Dictionary<string, SmartSnapshot>(_latestSmart, StringComparer.Ordinal);
            live = new Dictionary<string, LiveTelemetry>(_latestLive, StringComparer.Ordinal);
            unflushed = _bufferedBuckets.Values.ToList();
            settings = _settings;
        }

        var forecasts = new Dictionary<string, LifespanForecast>(StringComparer.Ordinal);
        foreach (var dev in devices)
        {
            smart.TryGetValue(dev.DeviceId, out var s);
            forecasts[dev.DeviceId] = LifespanAnalyzer.Compute(dev, s, settings, _store, unflushed);
        }

        return new DashboardSnapshot
        {
            Status = BuildStatusInfo(),
            Devices = devices,
            LatestSmart = smart,
            LatestLive = live,
            Forecasts = forecasts,
            Alerts = _store.GetRecentAlerts(50),
            Settings = settings
        };
    }

    public DaemonStatusInfo BuildStatusInfo()
    {
        int bufferedCount;
        int devCount;
        DateTimeOffset? lastFlush;

        lock (_stateLock)
        {
            bufferedCount = _bufferedBuckets.Count;
            devCount = _devices.Count;
            lastFlush = _lastFlushUtc;
        }

        using var self = Process.GetCurrentProcess();
        return new DaemonStatusInfo
        {
            Pid = Environment.ProcessId,
            StartedAtUtc = _startedAtUtc,
            UptimeSeconds = (DateTimeOffset.UtcNow - _startedAtUtc).TotalSeconds,
            MemoryWorkingSetBytes = self.WorkingSet64,
            DatabasePath = _store.DatabasePath,
            DatabaseSizeBytes = _store.GetDatabaseFileSizeBytes(),
            LastFlushUtc = lastFlush,
            BufferedSamplesCount = bufferedCount,
            TotalStoredSnapshots = _store.GetTotalStoredSnapshotCount(),
            HardwareBackend = _nvmeProvider.BackendName,
            AutostartInstalled = AutostartManager.IsAutostartEnabled(),
            DevicesDetected = devCount
        };
    }

    private async Task<IpcResponse> HandleIpcRequestAsync(IpcRequest req, CancellationToken ct)
    {
        switch (req.Command)
        {
            case "Ping":
                return new IpcResponse { Ok = true, Message = "Pong", Status = BuildStatusInfo() };

            case "GetStatus":
                return new IpcResponse { Ok = true, Status = BuildStatusInfo() };

            case "GetDashboard":
                return new IpcResponse { Ok = true, Dashboard = BuildDashboardSnapshot() };

            case "InspectRange":
            {
                string devId;
                List<BufferedBucket> unflushed;
                lock (_stateLock)
                {
                    devId = !string.IsNullOrWhiteSpace(req.DeviceId)
                        ? req.DeviceId
                        : (_devices.FirstOrDefault()?.DeviceId ?? "");
                    unflushed = _bufferedBuckets.Values.ToList();
                }

                var end = req.EndUtc ?? DateTimeOffset.UtcNow;
                var start = req.StartUtc ?? end.AddHours(-24);
                var inspection = _store.InspectRange(devId, start, end, unflushed);
                return new IpcResponse { Ok = true, Inspection = inspection };
            }

            case "FlushNow":
            {
                // Refresh SMART before flushing so manual snapshot captures latest controller registers
                List<NvmeDeviceInfo> devs;
                lock (_stateLock) devs = _devices.ToList();
                foreach (var d in devs)
                {
                    var snap = await _nvmeProvider.ReadSmartAsync(d, forceRefresh: true, ct);
                    if (snap is not null)
                    {
                        lock (_stateLock)
                        {
                            _latestSmart[d.DeviceId] = snap;
                            _unpolledBlockWriteAccum[d.DeviceId] = 0;
                        }
                    }
                }

                FlushToDatabase();
                return new IpcResponse { Ok = true, Message = "Flushed telemetry buffer to SQLite.", Dashboard = BuildDashboardSnapshot() };
            }

            case "AcknowledgeAlerts":
                _store.AcknowledgeAllAlerts();
                return new IpcResponse { Ok = true, Dashboard = BuildDashboardSnapshot() };

            case "ClearAlerts":
                _store.ClearAlerts();
                return new IpcResponse { Ok = true, Message = "All alerts cleared.", Dashboard = BuildDashboardSnapshot() };

            case "UpdateSettings":
            {
                if (req.Settings is null)
                {
                    return new IpcResponse { Ok = false, Error = "Missing settings payload." };
                }

                string? statusMessage = "Settings saved.";
                bool currentAutostart = AutostartManager.IsAutostartEnabled();
                if (req.Settings.StartDaemonAtLogin != currentAutostart)
                {
                    string exePath = ResolveSelfExecutablePath();
                    var (autoOk, autoMsg) = AutostartManager.SetAutostart(req.Settings.StartDaemonAtLogin, exePath);
                    if (!autoOk)
                    {
                        req.Settings.StartDaemonAtLogin = currentAutostart;
                        return new IpcResponse { Ok = false, Error = autoMsg, Dashboard = BuildDashboardSnapshot() };
                    }

                    statusMessage = autoMsg;
                }

                AppPaths.CustomDataDirectory = string.IsNullOrWhiteSpace(req.Settings.CustomDatabaseDirectory)
                    ? null
                    : req.Settings.CustomDatabaseDirectory;

                lock (_stateLock)
                {
                    _settings = req.Settings;
                }

                _store.SaveSettings(req.Settings);
                return new IpcResponse { Ok = true, Message = statusMessage, Dashboard = BuildDashboardSnapshot() };
            }

            case "StartSelfTest":
            {
                NvmeDeviceInfo? dev;
                lock (_stateLock)
                {
                    dev = _devices.FirstOrDefault(d => d.DeviceId == req.DeviceId) ?? _devices.FirstOrDefault();
                }

                if (dev is null)
                {
                    return new IpcResponse { Ok = false, Error = "No NVMe device found." };
                }

                var (ok, msg) = await _nvmeProvider.StartSelfTestAsync(dev, req.SelfTestType ?? "short", ct);
                if (ok)
                {
                    var refreshed = await _nvmeProvider.ReadSmartAsync(dev, forceRefresh: true, ct);
                    if (refreshed is not null)
                    {
                        lock (_stateLock) _latestSmart[dev.DeviceId] = refreshed;
                    }
                }

                return new IpcResponse { Ok = ok, Error = ok ? null : msg, Message = msg, Dashboard = BuildDashboardSnapshot() };
            }

            case "ExportData":
            {
                NvmeDeviceInfo? dev;
                SmartSnapshot? smart;
                DaemonSettings settings;
                List<BufferedBucket> unflushed;

                lock (_stateLock)
                {
                    dev = _devices.FirstOrDefault(d => d.DeviceId == req.DeviceId) ?? _devices.FirstOrDefault();
                    smart = dev is not null && _latestSmart.TryGetValue(dev.DeviceId, out var s) ? s : null;
                    settings = _settings;
                    unflushed = _bufferedBuckets.Values.ToList();
                }

                if (dev is null)
                {
                    return new IpcResponse { Ok = false, Error = "No NVMe device available for export." };
                }

                var end = req.EndUtc ?? DateTimeOffset.UtcNow;
                var start = req.StartUtc ?? end.AddDays(-30);
                var inspection = _store.InspectRange(dev.DeviceId, start, end, unflushed);
                var forecast = LifespanAnalyzer.Compute(dev, smart, settings, _store, unflushed);
                var savedPath = DataExporter.ExportInspection(dev, smart, forecast, inspection, req.ExportFormat ?? "csv", req.ExportPath);
                return new IpcResponse { Ok = true, Message = savedPath };
            }

            case "ShutdownDaemon":
                FlushToDatabase();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(150, CancellationToken.None);
                    ShutdownRequested?.Invoke();
                });
                return new IpcResponse { Ok = true, Message = "Daemon shutting down." };

            default:
                return new IpcResponse { Ok = false, Error = $"Unknown command '{req.Command}'." };
        }
    }

    private static string ResolveSelfExecutablePath()
    {
        var procPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(procPath) &&
            !Path.GetFileNameWithoutExtension(procPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return procPath;
        }

        var entryDll = Path.Combine(AppContext.BaseDirectory, "nvme-sentinel-daemon.dll");
        return File.Exists(entryDll) ? entryDll : (procPath ?? "nvme-sentinel-daemon");
    }

    private static long AlignBucket5Min(long unixSeconds) => unixSeconds - (unixSeconds % 300L);

    public async ValueTask DisposeAsync()
    {
        await _stopCts.CancelAsync();
        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* Ignore */ }
        }

        FlushToDatabase();
        await _ipcServer.DisposeAsync();
        _store.Dispose();
        _stopCts.Dispose();
    }
}
