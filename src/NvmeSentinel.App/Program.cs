using Avalonia;

namespace NvmeSentinel.App;

internal static class Program
{
    public static string[] StartupArgs { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        StartupArgs = args;
        var argsSet = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);

        if (argsSet.Contains("--version") || argsSet.Contains("-v"))
        {
            var ver = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            Console.WriteLine($"nvme-sentinel v{ver}");
            return;
        }

        if (argsSet.Contains("--help") || argsSet.Contains("-h"))
        {
            Console.WriteLine("""
                NVMe Sentinel - Hardware Health & Endurance Monitor
                Usage:
                  nvme-sentinel            Launch graphical desktop monitor
                  nvme-sentinel --version  Show version information
                  nvme-sentinel --help     Show this help message
                """);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
