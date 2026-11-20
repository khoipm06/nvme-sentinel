using System.Globalization;
using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Hardware.Linux;

public sealed class LinuxProcessIoTracker : IProcessIoTracker
{
    private sealed class PidState
    {
        public string Name { get; set; } = string.Empty;
        public string ExePath { get; set; } = string.Empty;
        public long LastReadBytes { get; set; }
        public long LastWriteBytes { get; set; }
        public long SessionReadBytes { get; set; }
        public long SessionWriteBytes { get; set; }
        public bool Touched { get; set; }
    }

    private readonly Dictionary<int, PidState> _pidStates = new();
    private bool _initialized;

    public ProcessPollResult Poll(double elapsedSeconds, long blockWriteDeltaBytes)
    {
        var result = new ProcessPollResult();
        double dt = Math.Max(0.2, elapsedSeconds);

        foreach (var state in _pidStates.Values)
        {
            state.Touched = false;
        }

        var liveList = new List<ProcessIoEntry>(32);
        var aggregatedDeltas = new Dictionary<string, (string ExePath, long WriteDelta, long ReadDelta)>(StringComparer.OrdinalIgnoreCase);
        long totalAttributedWriteDelta = 0;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var span = Path.GetFileName(dir.AsSpan());
                if (!int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
                {
                    continue;
                }

                if (!TryReadProcIo(dir, out long readBytes, out long writeBytes))
                {
                    continue;
                }

                if (!_pidStates.TryGetValue(pid, out var state))
                {
                    var (name, exePath) = ReadProcessIdentity(dir, pid);
                    state = new PidState
                    {
                        Name = name,
                        ExePath = exePath,
                        LastReadBytes = readBytes,
                        LastWriteBytes = writeBytes
                    };
                    _pidStates[pid] = state;
                    state.Touched = true;
                    continue;
                }

                state.Touched = true;
                long dRead = Math.Max(0, readBytes - state.LastReadBytes);
                long dWrite = Math.Max(0, writeBytes - state.LastWriteBytes);
                state.LastReadBytes = readBytes;
                state.LastWriteBytes = writeBytes;

                if (_initialized)
                {
                    state.SessionReadBytes += dRead;
                    state.SessionWriteBytes += dWrite;

                    if (dWrite > 0 || dRead > 0)
                    {
                        totalAttributedWriteDelta += dWrite;
                        if (aggregatedDeltas.TryGetValue(state.Name, out var existing))
                        {
                            var exe = string.IsNullOrEmpty(existing.ExePath) ? state.ExePath : existing.ExePath;
                            aggregatedDeltas[state.Name] = (exe, existing.WriteDelta + dWrite, existing.ReadDelta + dRead);
                        }
                        else
                        {
                            aggregatedDeltas[state.Name] = (state.ExePath, dWrite, dRead);
                        }
                    }
                }

                if (dWrite > 0 || dRead > 0 || state.SessionWriteBytes >= 1_048_576)
                {
                    liveList.Add(new ProcessIoEntry
                    {
                        Pid = pid,
                        ProcessName = state.Name,
                        ExecutablePath = state.ExePath,
                        WriteBytesPerSec = _initialized ? dWrite / dt : 0,
                        ReadBytesPerSec = _initialized ? dRead / dt : 0,
                        SessionBytesWritten = state.SessionWriteBytes,
                        SessionBytesRead = state.SessionReadBytes
                    });
                }
            }
        }
        catch
        {
            // Ignore races with exiting processes
        }

        // Prune exited PIDs
        var deadPids = _pidStates.Where(kv => !kv.Value.Touched).Select(kv => kv.Key).ToList();
        foreach (var dead in deadPids)
        {
            _pidStates.Remove(dead);
        }

        // Account for privileged/kernel writes invisible to unprivileged /proc/<pid>/io
        if (_initialized && blockWriteDeltaBytes > totalAttributedWriteDelta)
        {
            long unattributed = blockWriteDeltaBytes - totalAttributedWriteDelta;
            if (unattributed >= 65_536) // >= 64 KB
            {
                const string sysKey = "System / Privileged Services";
                aggregatedDeltas[sysKey] = ("kernel & root daemons", unattributed, 0);
            }
        }

        _initialized = true;

        result.TopActiveProcesses = liveList
            .OrderByDescending(p => p.WriteBytesPerSec)
            .ThenByDescending(p => p.SessionBytesWritten)
            .Take(25)
            .ToList();

        result.IntervalDeltas = aggregatedDeltas
            .Where(kv => kv.Value.WriteDelta > 0 || kv.Value.ReadDelta > 1_048_576)
            .Select(kv => new ProcessWriteDelta(kv.Key, kv.Value.ExePath, kv.Value.WriteDelta, kv.Value.ReadDelta))
            .ToList();

        return result;
    }

    private static bool TryReadProcIo(string procPidDir, out long readBytes, out long writeBytes)
    {
        readBytes = 0;
        writeBytes = 0;
        try
        {
            var ioPath = Path.Combine(procPidDir, "io");
            using var sr = new StreamReader(ioPath);
            string? line;
            int found = 0;
            while ((line = sr.ReadLine()) is not null)
            {
                if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
                {
                    if (long.TryParse(line.AsSpan(11).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                    {
                        readBytes = r;
                        found++;
                    }
                }
                else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
                {
                    if (long.TryParse(line.AsSpan(12).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w))
                    {
                        writeBytes = w;
                        found++;
                    }
                }

                if (found == 2)
                {
                    return true;
                }
            }

            return found > 0;
        }
        catch
        {
            return false;
        }
    }

    private static (string Name, string ExePath) ReadProcessIdentity(string procPidDir, int pid)
    {
        string name = $"pid-{pid}";
        string exePath = string.Empty;

        try
        {
            var commPath = Path.Combine(procPidDir, "comm");
            if (File.Exists(commPath))
            {
                var comm = File.ReadAllText(commPath).Trim();
                if (!string.IsNullOrWhiteSpace(comm))
                {
                    name = comm;
                }
            }
        }
        catch
        {
            // Ignore
        }

        try
        {
            var exeLink = Path.Combine(procPidDir, "exe");
            var target = File.ResolveLinkTarget(exeLink, returnFinalTarget: true);
            if (target is not null && !string.IsNullOrWhiteSpace(target.FullName))
            {
                exePath = target.FullName;
                var fileName = Path.GetFileName(exePath);
                if (!string.IsNullOrWhiteSpace(fileName) && name.Length >= 15)
                {
                    // Linux /proc/pid/comm truncates to 15 chars; prefer full binary basename
                    name = fileName;
                }
            }
        }
        catch
        {
            // Ignore permission denied on exe link
        }

        return (name, exePath);
    }
}
