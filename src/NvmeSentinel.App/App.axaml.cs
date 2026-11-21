using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using NvmeSentinel.App.Services;
using NvmeSentinel.App.ViewModels;
using NvmeSentinel.App.Views;
using SukiUI;

namespace NvmeSentinel.App;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private MainWindowViewModel? _viewModel;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _statusMenuItem;
    private TrayIconGenerator.TrayState _lastTrayState = TrayIconGenerator.TrayState.Healthy;
    private bool _allowRealClose;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        var theme = SukiTheme.GetInstance();
        var graphiteTheme = new SukiUI.Models.SukiColorTheme(
            "GraphiteInk",
            Avalonia.Media.Color.Parse("#71717A"),
            Avalonia.Media.Color.Parse("#52525B"));
        theme.AddColorTheme(graphiteTheme);
        theme.ChangeColorTheme(graphiteTheme);
        theme.ChangeBaseTheme(ThemeVariant.Dark);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) =>
            {
                _allowRealClose = true;
                if (Interlocked.Exchange(ref _isShuttingDown, 1) == 1) return;
                CleanupAppResources();
                // Note: Do NOT call desktop.Shutdown() here; Avalonia is already running its shutdown sequence.
            };

            if (OperatingSystem.IsLinux())
            {
                System.Runtime.InteropServices.PosixSignalRegistration.Create(
                    System.Runtime.InteropServices.PosixSignal.SIGTERM,
                    _ =>
                    {
                        _allowRealClose = true;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => ExitApplication(desktop));
                    });
            }

            _viewModel = new MainWindowViewModel();
            _mainWindow = new MainWindow
            {
                DataContext = _viewModel,
                Icon = TrayIconGenerator.CreateIcon(TrayIconGenerator.TrayState.Healthy)
            };

            _mainWindow.Closing += OnMainWindowClosing;

            _viewModel.RequestQuitGuiKeepDaemon += () => ExitApplication(desktop);
            _viewModel.RequestStopDaemonAndQuitAll += () => ExitApplication(desktop);
            _viewModel.TrayStatusUpdated += OnTrayStatusUpdated;

            SetupSystemTray(desktop);

            desktop.MainWindow = _mainWindow;

            if (!Program.StartupArgs.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            {
                _mainWindow.Show();
            }

            _ = InitializeAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_viewModel is null || _mainWindow is null) return;

        await _viewModel.StartAsync();

        // Optional automated UI screenshot tour mode for visual verification
        int tourIdx = Array.FindIndex(Program.StartupArgs, a => a.Equals("--screenshot-tour", StringComparison.OrdinalIgnoreCase));
        if (tourIdx >= 0 && tourIdx + 1 < Program.StartupArgs.Length)
        {
            var outDir = Program.StartupArgs[tourIdx + 1];
            await _mainWindow.RunScreenshotTourAsync(outDir);
            ExitApplication(desktop);
        }
    }

    private void SetupSystemTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _statusMenuItem = new NativeMenuItem("Status: Connecting to daemon...")
        {
            IsEnabled = false
        };

        var openItem = new NativeMenuItem("Open NVMe Sentinel Dashboard");
        openItem.Click += (_, _) => RestoreMainWindow();

        var themeItem = new NativeMenuItem("Toggle Theme (Dark / Light)");
        themeItem.Click += (_, _) => _viewModel?.ToggleThemeCommand.Execute(null);

        var flushItem = new NativeMenuItem("Flush Telemetry Buffer Now");
        flushItem.Click += (_, _) => _viewModel?.FlushNowCommand.Execute(null);

        var quitGuiItem = new NativeMenuItem("Quit GUI (Keep Daemon Running)");
        quitGuiItem.Click += (_, _) => ExitApplication(desktop);

        var stopAllItem = new NativeMenuItem("Stop Daemon & Quit All");
        stopAllItem.Click += (_, _) => _viewModel?.StopDaemonAndQuitCommand.Execute(null);

        var menu = new NativeMenu
        {
            Items =
            {
                openItem,
                _statusMenuItem,
                new NativeMenuItemSeparator(),
                themeItem,
                flushItem,
                new NativeMenuItemSeparator(),
                quitGuiItem,
                stopAllItem
            }
        };

        _trayIcon = new TrayIcon
        {
            Icon = TrayIconGenerator.CreateIcon(TrayIconGenerator.TrayState.Healthy),
            ToolTipText = "NVMe Sentinel — Monitoring NVMe Health & TBW",
            Menu = menu,
            IsVisible = true
        };

        _trayIcon.Clicked += (_, _) => RestoreMainWindow();
        TrayIcon.SetIcons(this, [_trayIcon]);
    }

    private void OnTrayStatusUpdated(string tooltip, TrayIconGenerator.TrayState state)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = tooltip;
            if (state != _lastTrayState)
            {
                _lastTrayState = state;
                _trayIcon.Icon = TrayIconGenerator.CreateIcon(state);
            }
        }

        if (_statusMenuItem is not null)
        {
            _statusMenuItem.Header = tooltip.Replace("NVMe Sentinel • ", "");
        }
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowRealClose || e.IsProgrammatic)
        {
            return;
        }

        if (_viewModel is { CloseToTrayEnabled: true })
        {
            e.Cancel = true;
            _mainWindow?.Hide();
            return;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ExitApplication(desktop);
        }
    }

    private void RestoreMainWindow()
    {
        if (_mainWindow is null) return;
        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private int _isShuttingDown;

    private void CleanupAppResources()
    {
        try
        {
            if (_trayIcon is not null)
            {
                _trayIcon.IsVisible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
        catch
        {
            // Ignore disposal exceptions during shutdown
        }

        try
        {
            _viewModel?.Dispose();
            _viewModel = null;
        }
        catch
        {
            // Ignore viewmodel disposal errors during shutdown
        }
    }

    private void ExitApplication(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _allowRealClose = true;
        if (Interlocked.Exchange(ref _isShuttingDown, 1) == 1) return;

        CleanupAppResources();

        try
        {
            desktop.Shutdown();
        }
        catch
        {
            // Ignore if already shutting down
        }
    }
}
