using System.Globalization;
using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Hardware.Linux;

public sealed class LinuxProcessIoTracker : IProcessIoTracker
{
    private sealed class PidState
    {
        public string Name { get; set; } = string.Empty;
        public string ExePath { get; set; } = string.Empty;
        public string IoPath { get; set; } = string.Empty;
        public long LastReadBytes { get; set; }
        public long LastWriteBytes { get; set; }
        public long SessionReadBytes { get; set; }
        public long SessionWriteBytes { get; set; }
        public bool Touched { get; set; }
    }

    private readonly Dictionary<int, PidState> _pidStates = new();
    private bool _initialized;

    [ThreadStatic]
    private static byte[]? t_ioBuffer;

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
                if (span.IsEmpty || !char.IsAsciiDigit(span[0]) || !int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
                {
                    continue;
                }

                bool isExisting = _pidStates.TryGetValue(pid, out var state);
                string ioPath = isExisting && state is not null ? state.IoPath : Path.Combine(dir, "io");

                if (!TryReadProcIo(ioPath, out long readBytes, out long writeBytes))
                {
                    continue;
                }

                if (!isExisting || state is null)
                {
                    var (name, exePath) = ReadProcessIdentity(dir, pid);
                    state = new PidState
                    {
                        Name = name,
                        ExePath = exePath,
                        IoPath = ioPath,
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

    private static bool TryReadProcIo(string ioPath, out long readBytes, out long writeBytes)
    {
        readBytes = 0;
        writeBytes = 0;
        try
        {
            using var fs = new FileStream(ioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256, FileOptions.SequentialScan);
            t_ioBuffer ??= new byte[512];
            int read = fs.Read(t_ioBuffer, 0, t_ioBuffer.Length);
            if (read <= 0) return false;

            var span = t_ioBuffer.AsSpan(0, read);
            int found = 0;

            int idx = 0;
            while (idx < span.Length)
            {
                var lineEnd = span[idx..].IndexOf((byte)'\n');
                var line = lineEnd >= 0 ? span.Slice(idx, lineEnd) : span[idx..];
                idx += lineEnd >= 0 ? lineEnd + 1 : span.Length;

                if (line.StartsWith("read_bytes:"u8))
                {
                    if (TryParseAsciiLong(line[11..].Trim((byte)' '), out var r))
                    {
                        readBytes = r;
                        found++;
                    }
                }
                else if (line.StartsWith("write_bytes:"u8))
                {
                    if (TryParseAsciiLong(line[12..].Trim((byte)' '), out var w))
                    {
                        writeBytes = w;
                        found++;
                    }
                }

                if (found == 2) break;
            }

            return found > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseAsciiLong(ReadOnlySpan<byte> bytes, out long value)
    {
        value = 0;
        if (bytes.IsEmpty) return false;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b is < (byte)'0' or > (byte)'9') return false;
            value = value * 10 + (b - '0');
        }
        return true;
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
