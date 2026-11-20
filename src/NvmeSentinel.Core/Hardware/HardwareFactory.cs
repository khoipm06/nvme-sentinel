using System.Runtime.InteropServices;
using NvmeSentinel.Core.Hardware.Linux;
using NvmeSentinel.Core.Hardware.MacOs;
using NvmeSentinel.Core.Hardware.Windows;

namespace NvmeSentinel.Core.Hardware;

public static class HardwareFactory
{
    public static INvmeProvider CreateNvmeProvider()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsNvmeProvider();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOsNvmeProvider();
        }

        return new LinuxNvmeProvider();
    }

    public static IProcessIoTracker CreateProcessTracker()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsProcessIoTracker();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOsProcessIoTracker();
        }

        return new LinuxProcessIoTracker();
    }
}
