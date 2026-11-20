using System.Diagnostics;
using System.Runtime.InteropServices;
using NvmeSentinel.Core.Models;

namespace NvmeSentinel.Core.Hardware.Windows;

public sealed class WindowsProcessIoTracker : IProcessIoTracker
{
    private sealed class WinPidState
    {
        public string Name { get; set; } = string.Empty;
        public string ExePath { get; set; } = string.Empty;
        public ulong LastRead { get; set; }
        public ulong LastWrite { get; set; }
        public long SessionRead { get; set; }
        public long SessionWrite { get; set; }
        public bool Touched { get; set; }
    }

    private readonly Dictionary<int, WinPidState> _states = new();
    private bool _initialized;

    private const uint ProcessQueryLimitedInformation = 0x1000;

    public ProcessPollResult Poll(double elapsedSeconds, long blockWriteDeltaBytes)
    {
        var result = new ProcessPollResult();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return result;
        }

        double dt = Math.Max(0.2, elapsedSeconds);
        foreach (var s in _states.Values) s.Touched = false;

        var live = new List<ProcessIoEntry>(32);
        var deltas = new Dictionary<string, (string ExePath, long WriteDelta, long ReadDelta)>(StringComparer.OrdinalIgnoreCase);
        long attributedWrite = 0;

        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                int pid = proc.Id;
                if (pid <= 4) continue;

                IntPtr hProc = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
                if (hProc == IntPtr.Zero) continue;

                try
                {
                    if (!GetProcessIoCounters(hProc, out var io))
                    {
                        continue;
                    }

                    if (!_states.TryGetValue(pid, out var st))
                    {
                        string name = SafeGetName(proc, pid);
                        string exe = SafeGetExePath(hProc);
                        _states[pid] = new WinPidState
                        {
                            Name = name,
                            ExePath = exe,
                            LastRead = io.ReadTransferCount,
                            LastWrite = io.WriteTransferCount,
                            Touched = true
                        };
                        continue;
                    }

                    st.Touched = true;
                    long dRead = io.ReadTransferCount >= st.LastRead ? (long)(io.ReadTransferCount - st.LastRead) : 0;
                    long dWrite = io.WriteTransferCount >= st.LastWrite ? (long)(io.WriteTransferCount - st.LastWrite) : 0;
                    st.LastRead = io.ReadTransferCount;
                    st.LastWrite = io.WriteTransferCount;

                    if (_initialized)
                    {
                        st.SessionRead += dRead;
                        st.SessionWrite += dWrite;
                        if (dWrite > 0 || dRead > 0)
                        {
                            attributedWrite += dWrite;
                            if (deltas.TryGetValue(st.Name, out var ex))
                            {
                                deltas[st.Name] = (ex.ExePath, ex.WriteDelta + dWrite, ex.ReadDelta + dRead);
                            }
                            else
                            {
                                deltas[st.Name] = (st.ExePath, dWrite, dRead);
                            }
                        }
                    }

                    if (dWrite > 0 || dRead > 0 || st.SessionWrite >= 1_048_576)
                    {
                        live.Add(new ProcessIoEntry
                        {
                            Pid = pid,
                            ProcessName = st.Name,
                            ExecutablePath = st.ExePath,
                            WriteBytesPerSec = _initialized ? dWrite / dt : 0,
                            ReadBytesPerSec = _initialized ? dRead / dt : 0,
                            SessionBytesWritten = st.SessionWrite,
                            SessionBytesRead = st.SessionRead
                        });
                    }
                }
                finally
                {
                    CloseHandle(hProc);
                }
            }
        }

        foreach (var dead in _states.Where(kv => !kv.Value.Touched).Select(kv => kv.Key).ToList())
        {
            _states.Remove(dead);
        }

        if (_initialized && blockWriteDeltaBytes > attributedWrite + 65_536)
        {
            deltas["System / Privileged Services"] = ("NT Kernel & Services", blockWriteDeltaBytes - attributedWrite, 0);
        }

        _initialized = true;
        result.TopActiveProcesses = live
            .OrderByDescending(p => p.WriteBytesPerSec)
            .ThenByDescending(p => p.SessionBytesWritten)
            .Take(25)
            .ToList();

        result.IntervalDeltas = deltas
            .Where(kv => kv.Value.WriteDelta > 0 || kv.Value.ReadDelta > 1_048_576)
            .Select(kv => new ProcessWriteDelta(kv.Key, kv.Value.ExePath, kv.Value.WriteDelta, kv.Value.ReadDelta))
            .ToList();

        return result;
    }

    private static string SafeGetName(Process p, int pid)
    {
        try { return p.ProcessName; }
        catch { return $"pid-{pid}"; }
    }

    private static string SafeGetExePath(IntPtr hProc)
    {
        var buf = new char[512];
        int len = buf.Length;
        return QueryFullProcessImageNameW(hProc, 0, buf, ref len) && len > 0
            ? new string(buf, 0, len)
            : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCountersNative
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IoCountersNative lpIoCounters);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, [Out] char[] lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
