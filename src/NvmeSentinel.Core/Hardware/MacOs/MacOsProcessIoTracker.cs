using System.Diagnostics;
using System.Runtime.InteropServices;
using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Hardware.MacOs;

public sealed class MacOsProcessIoTracker : IProcessIoTracker
{
    private sealed class MacPidState
    {
        public string Name { get; set; } = string.Empty;
        public ulong LastRead { get; set; }
        public ulong LastWrite { get; set; }
        public long SessionRead { get; set; }
        public long SessionWrite { get; set; }
        public bool Touched { get; set; }
    }

    private readonly Dictionary<int, MacPidState> _states = new();
    private bool _initialized;

    private const int RusageInfoV2 = 2;

    public ProcessPollResult Poll(double elapsedSeconds, long blockWriteDeltaBytes)
    {
        var result = new ProcessPollResult();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return result;
        }

        double dt = Math.Max(0.2, elapsedSeconds);
        foreach (var s in _states.Values) s.Touched = false;

        var live = new List<ProcessIoEntry>(32);
        var deltas = new Dictionary<string, (string ExePath, long WriteDelta, long ReadDelta)>(StringComparer.OrdinalIgnoreCase);

        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                int pid = proc.Id;
                if (pid <= 0) continue;

                var info = new RusageInfoV2Native();
                if (proc_pid_rusage(pid, RusageInfoV2, ref info) != 0)
                {
                    continue;
                }

                if (!_states.TryGetValue(pid, out var st))
                {
                    string name = SafeGetName(proc, pid);
                    _states[pid] = new MacPidState
                    {
                        Name = name,
                        LastRead = info.ri_diskio_bytesread,
                        LastWrite = info.ri_diskio_byteswritten,
                        Touched = true
                    };
                    continue;
                }

                st.Touched = true;
                long dRead = info.ri_diskio_bytesread >= st.LastRead ? (long)(info.ri_diskio_bytesread - st.LastRead) : 0;
                long dWrite = info.ri_diskio_byteswritten >= st.LastWrite ? (long)(info.ri_diskio_byteswritten - st.LastWrite) : 0;
                st.LastRead = info.ri_diskio_bytesread;
                st.LastWrite = info.ri_diskio_byteswritten;

                if (_initialized)
                {
                    st.SessionRead += dRead;
                    st.SessionWrite += dWrite;
                    if (dWrite > 0 || dRead > 0)
                    {
                        if (deltas.TryGetValue(st.Name, out var ex))
                            deltas[st.Name] = ("", ex.WriteDelta + dWrite, ex.ReadDelta + dRead);
                        else
                            deltas[st.Name] = ("", dWrite, dRead);
                    }
                }

                if (dWrite > 0 || dRead > 0 || st.SessionWrite >= 1_048_576)
                {
                    live.Add(new ProcessIoEntry
                    {
                        Pid = pid,
                        ProcessName = st.Name,
                        WriteBytesPerSec = _initialized ? dWrite / dt : 0,
                        ReadBytesPerSec = _initialized ? dRead / dt : 0,
                        SessionBytesWritten = st.SessionWrite,
                        SessionBytesRead = st.SessionRead
                    });
                }
            }
        }

        foreach (var dead in _states.Where(kv => !kv.Value.Touched).Select(kv => kv.Key).ToList())
        {
            _states.Remove(dead);
        }

        _initialized = true;
        result.TopActiveProcesses = live
            .OrderByDescending(p => p.WriteBytesPerSec)
            .ThenByDescending(p => p.SessionBytesWritten)
            .Take(25)
            .ToList();

        result.IntervalDeltas = deltas
            .Where(kv => kv.Value.WriteDelta > 0)
            .Select(kv => new ProcessWriteDelta(kv.Key, kv.Value.ExePath, kv.Value.WriteDelta, kv.Value.ReadDelta))
            .ToList();

        return result;
    }

    private static string SafeGetName(Process p, int pid)
    {
        try { return p.ProcessName; }
        catch { return $"pid-{pid}"; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct RusageInfoV2Native
    {
        public fixed byte ri_uuid[16];
        public ulong ri_user_time;
        public ulong ri_system_time;
        public ulong ri_pkg_idle_wkups;
        public ulong ri_interrupt_wkups;
        public ulong ri_pageins;
        public ulong ri_wired_size;
        public ulong ri_resident_size;
        public ulong ri_phys_footprint;
        public ulong ri_proc_start_abstime;
        public ulong ri_proc_exit_abstime;
        public ulong ri_child_user_time;
        public ulong ri_child_system_time;
        public ulong ri_child_pkg_idle_wkups;
        public ulong ri_child_interrupt_wkups;
        public ulong ri_child_pageins;
        public ulong ri_child_elapsed_abstime;
        public ulong ri_diskio_bytesread;
        public ulong ri_diskio_byteswritten;
    }

    [DllImport("libproc.dylib")]
    private static extern int proc_pid_rusage(int pid, int flavor, ref RusageInfoV2Native buffer);
}
