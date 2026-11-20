using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NvmeSentinel.App.Controls;
using NvmeSentinel.App.Services;
using NvmeSentinel.Core.Ipc;
using NvmeSentinel.Core.Models;
using NvmeSentinel.Core.Storage;
using SukiUI;

namespace NvmeSentinel.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly SentinelIpcClient _ipc = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<double> _readRing = Enumerable.Repeat(0.0, 60).ToList();
    private readonly List<double> _writeRing = Enumerable.Repeat(0.0, 60).ToList();

    private string _activePreset = "24h";
    private DateTimeOffset _presetStartUtc = DateTimeOffset.UtcNow.AddHours(-24);
    private DateTimeOffset _presetEndUtc = DateTimeOffset.UtcNow;

    public event Action<string, TrayIconGenerator.TrayState>? TrayStatusUpdated;
    public event Action? RequestQuitGuiKeepDaemon;
    public event Action? RequestStopDaemonAndQuitAll;

    public ObservableCollection<NvmeDeviceInfo> Devices { get; } = [];
    public ObservableCollection<SensorBadgeItem> SensorBadges { get; } = [];
    public ObservableCollection<ProcessCulpritRow> WindowTopProcesses { get; } = [];
    public ObservableCollection<LiveProcessRow> LiveProcesses { get; } = [];
    public ObservableCollection<SmartAttributeRow> SmartAttributes { get; } = [];
    public ObservableCollection<AlertRow> AlertItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverviewPage))]
    [NotifyPropertyChangedFor(nameof(IsTimeMachinePage))]
    [NotifyPropertyChangedFor(nameof(IsLiveProcessesPage))]
    [NotifyPropertyChangedFor(nameof(IsSmartPage))]
    [NotifyPropertyChangedFor(nameof(IsSettingsPage))]
    [NotifyPropertyChangedFor(nameof(CurrentPageTitle))]
    private int _selectedPageIndex;

    public bool IsOverviewPage => SelectedPageIndex == 0;
    public bool IsTimeMachinePage => SelectedPageIndex == 1;
    public bool IsLiveProcessesPage => SelectedPageIndex == 2;
    public bool IsSmartPage => SelectedPageIndex == 3;
    public bool IsSettingsPage => SelectedPageIndex == 4;

    public string CurrentPageTitle => SelectedPageIndex switch
    {
        0 => "Overview",
        1 => "Write history",
        2 => "Active processes",
        3 => "SMART registers",
        4 => "Settings & alerts",
        _ => "Overview"
    };

    [ObservableProperty] private bool _hasWindowCulprits;
    [ObservableProperty] private bool _hasNoWindowCulprits = true;
    [ObservableProperty] private bool _hasAlerts;
    [ObservableProperty] private bool _hasNoAlerts = true;
    [ObservableProperty] private bool _hasUnreadAlerts;

    [ObservableProperty] private NvmeDeviceInfo? _selectedDevice;
    [ObservableProperty] private bool _isDaemonConnected;
    [ObservableProperty] private string _connectionBadgeText = "Connecting";
    [ObservableProperty] private string _actionBannerMessage = string.Empty;
    [ObservableProperty] private bool _hasActionBanner;

    // Overview KPIs & Hardware Sheet
    [ObservableProperty] private double _healthScore = 100.0;
    [ObservableProperty] private string _healthScoreFormatted = "100%";
    [ObservableProperty] private string _healthGrade = "Good";
    [ObservableProperty] private string _wearSubtitle = "0.0% wear";
    [ObservableProperty] private string _healthSummaryLine = "0% wear · 100% spare";

    [ObservableProperty] private string _driveModel = "Detecting NVMe SSD…";
    [ObservableProperty] private string _driveHeaderMeta = "Scanning storage controllers";
    [ObservableProperty] private string _controllerSpecText = "—";
    [ObservableProperty] private string _serialAndFirmwareText = "—";
    [ObservableProperty] private string _capacityFormatted = "—";
    [ObservableProperty] private string _pcieBadgeText = "PCIe";
    [ObservableProperty] private string _powerStateText = "Active";

    [ObservableProperty] private string _currentTbwFormatted = "0.000 TB";
    [ObservableProperty] private string _ratedTbwFormatted = "600 TB rated";
    [ObservableProperty] private double _tbwProgressPercent;
    [ObservableProperty] private string _todayWrittenFormatted = "+0 B today";
    [ObservableProperty] private string _todayReadFormatted = "0 B read today";
    [ObservableProperty] private string _tbwSublineText = "+0 B today · of 600 TB";
    [ObservableProperty] private string _tbwRawBytesTooltip = string.Empty;

    [ObservableProperty] private string _currentTempFormatted = "-- °C";
    [ObservableProperty] private string _thermalSensorsInlineText = "Composite sensor";
    [ObservableProperty] private bool _isTempElevated;

    [ObservableProperty] private string _liveReadRateFormatted = "0.0 MB/s";
    [ObservableProperty] private string _liveWriteRateFormatted = "0.0 MB/s";
    [ObservableProperty] private string _liveIopsFormatted = "0 R · 0 W IOPS";
    [ObservableProperty] private IReadOnlyList<double> _readRateHistory = [];
    [ObservableProperty] private IReadOnlyList<double> _writeRateHistory = [];
    [ObservableProperty] private int _liveChartRevision;

    [ObservableProperty] private string _burnRateFormatted = "-- / day";
    [ObservableProperty] private string _remainingLifeFormatted = "—";
    [ObservableProperty] private string _enduranceSublineText = "Calculating daily burn rate";
    [ObservableProperty] private string _exhaustionDateFormatted = "—";
    [ObservableProperty] private string _ratedTbwSourceText = "Model estimate";

    [ObservableProperty] private string _availableSpareFormatted = "100%";
    [ObservableProperty] private string _powerOnHoursFormatted = "0 h";
    [ObservableProperty] private string _powerCyclesFormatted = "0";
    [ObservableProperty] private string _unsafeShutdownsFormatted = "0";
    [ObservableProperty] private string _mediaErrorsFormatted = "0";
    [ObservableProperty] private string _controllerBusyFormatted = "0 min";
    [ObservableProperty] private string _totalReadFormatted = "0.00 TB";

    // Time-Machine Inspector
    [ObservableProperty] private IReadOnlyList<TimeSeriesPoint> _chartPoints = [];
    [ObservableProperty] private ChartMetricMode _selectedChartMetric = ChartMetricMode.CumulativeTbw;
    [ObservableProperty] private string _activeRangeLabel = "Last 24 hours";
    [ObservableProperty] private bool _isSubWindowBrushed;
    [ObservableProperty] private string _windowWrittenDeltaFormatted = "+0 B";
    [ObservableProperty] private string _windowReadDeltaFormatted = "0 B";
    [ObservableProperty] private string _windowPeakTempFormatted = "-- °C";
    [ObservableProperty] private string _windowAvgRateFormatted = "0.0 MB/s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreset1h))]
    [NotifyPropertyChangedFor(nameof(IsPreset24h))]
    [NotifyPropertyChangedFor(nameof(IsPreset7d))]
    [NotifyPropertyChangedFor(nameof(IsPreset30d))]
    [NotifyPropertyChangedFor(nameof(IsPreset1y))]
    [NotifyPropertyChangedFor(nameof(IsPresetAll))]
    private string _selectedPresetKey = "24h";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMetricTbw))]
    [NotifyPropertyChangedFor(nameof(IsMetricDelta))]
    [NotifyPropertyChangedFor(nameof(IsMetricTemp))]
    [NotifyPropertyChangedFor(nameof(IsMetricWear))]
    private string _selectedMetricKey = "tbw";

    public bool IsPreset1h => SelectedPresetKey == "1h";
    public bool IsPreset24h => SelectedPresetKey == "24h";
    public bool IsPreset7d => SelectedPresetKey == "7d";
    public bool IsPreset30d => SelectedPresetKey == "30d";
    public bool IsPreset1y => SelectedPresetKey == "1y";
    public bool IsPresetAll => SelectedPresetKey == "all";

    public bool IsMetricTbw => SelectedMetricKey == "tbw";
    public bool IsMetricDelta => SelectedMetricKey == "delta";
    public bool IsMetricTemp => SelectedMetricKey == "temp";
    public bool IsMetricWear => SelectedMetricKey == "wear";

    // SMART & Self-Test
    [ObservableProperty] private string _selfTestStatusText = "Self-test idle · Last passed";
    [ObservableProperty] private bool _isSelfTestRunning;
    [ObservableProperty] private double _selfTestProgress;

    // Alerts & Settings
    [ObservableProperty] private int _unreadAlertsCount;
    [ObservableProperty] private string _daemonPidText = "—";
    [ObservableProperty] private string _daemonUptimeText = "—";
    [ObservableProperty] private string _daemonBackendText = "—";
    [ObservableProperty] private string _daemonMemoryFormatted = "—";
    [ObservableProperty] private string _databaseSizeText = "—";
    [ObservableProperty] private string _databasePathText = "—";
    [ObservableProperty] private string _lastFlushSummary = "—";

    [ObservableProperty] private int _editFlushMinutes = 10;
    [ObservableProperty] private double _editSpikeThresholdGb = 5.0;
    [ObservableProperty] private double _editWarningTempC = 75.0;
    [ObservableProperty] private double _editCriticalTempC = 83.0;
    [ObservableProperty] private string _editCustomRatedTbwTb = string.Empty;
    [ObservableProperty] private bool _editEnableNotifications = true;
    [ObservableProperty] private bool _editCloseToTray = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeButtonLabel))]
    private bool _editDarkTheme = true;
    [ObservableProperty] private bool _editStartAtLogin;
    [ObservableProperty] private bool _editUseBinaryUnits;

    public string ThemeButtonLabel => EditDarkTheme ? "Light grade" : "Dark grade";
    public bool CloseToTrayEnabled => EditCloseToTray;

    public MainWindowViewModel()
    {
        _readRateHistory = _readRing.ToList();
        _writeRateHistory = _writeRing.ToList();
    }

    public async Task StartAsync()
    {
        bool online = await DaemonProcessLauncher.EnsureDaemonRunningAsync(_ipc, _cts.Token);
        IsDaemonConnected = online;
        ConnectionBadgeText = online ? "Daemon active" : "Daemon offline";

        if (online)
        {
            await RefreshDashboardAsync(loadSettingsIntoEditor: true);
            await RefreshTimeMachinePresetAsync("24h");
        }

        _ = Task.Run(() => RunLiveSubscriptionLoopAsync(_cts.Token));
        _ = Task.Run(() => RunPeriodicDashboardSyncLoopAsync(_cts.Token));
    }

    partial void OnSelectedDeviceChanged(NvmeDeviceInfo? value)
    {
        if (value is not null && IsDaemonConnected)
        {
            _ = RefreshDashboardAsync(loadSettingsIntoEditor: false);
            _ = RefreshTimeMachinePresetAsync(_activePreset);
        }
    }

    partial void OnEditDarkThemeChanged(bool value)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var variant = value ? ThemeVariant.Dark : ThemeVariant.Light;
            if (Avalonia.Application.Current is not null)
            {
                Avalonia.Application.Current.RequestedThemeVariant = variant;
            }

            SukiTheme.GetInstance().ChangeBaseTheme(variant);
        });
    }

    [RelayCommand]
    private void NavigatePage(string pageIdxStr)
    {
        if (int.TryParse(pageIdxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
        {
            SelectedPageIndex = idx;
            if (idx is 0 or 1)
            {
                _ = RefreshTimeMachinePresetAsync(_activePreset);
            }
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        EditDarkTheme = !EditDarkTheme;
    }

    [RelayCommand]
    private void DismissBanner()
    {
        HasActionBanner = false;
    }

    [RelayCommand]
    private async Task SelectRangePresetAsync(string preset)
    {
        await RefreshTimeMachinePresetAsync(preset);
    }

    [RelayCommand]
    private void SelectChartMetric(string metricKey)
    {
        SelectedMetricKey = metricKey;
        SelectedChartMetric = metricKey switch
        {
            "delta" => ChartMetricMode.WriteDelta,
            "temp" => ChartMetricMode.Temperature,
            "wear" => ChartMetricMode.WearPercent,
            _ => ChartMetricMode.CumulativeTbw
        };
    }

    [RelayCommand]
    public async Task ClearBrushAsync()
    {
        IsSubWindowBrushed = false;
        await QueryInspectionWindowAsync(_presetStartUtc, _presetEndUtc, updateChartSeries: true);
    }

    public async Task InspectBrushedSubWindowAsync(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        IsSubWindowBrushed = true;
        ActiveRangeLabel = $"{startUtc.ToLocalTime():MMM dd HH:mm} → {endUtc.ToLocalTime():MMM dd HH:mm}";
        await QueryInspectionWindowAsync(startUtc, endUtc, updateChartSeries: false);
    }

    [RelayCommand]
    private async Task FlushNowAsync()
    {
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Command = "FlushNow" }, ct: _cts.Token);
            if (resp.Dashboard is not null)
            {
                ApplyDashboardSnapshot(resp.Dashboard, loadSettingsIntoEditor: false);
            }

            await RefreshTimeMachinePresetAsync(_activePreset);
            ShowBanner(resp.Message ?? "Snapshot saved to SQLite.");
        }
        catch (Exception ex)
        {
            ShowBanner($"Flush failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RunSelfTestAsync(string testType)
    {
        if (SelectedDevice is null) return;
        try
        {
            ShowBanner($"Starting {testType} self-test…");
            var resp = await _ipc.SendAsync(new IpcRequest
            {
                Command = "StartSelfTest",
                DeviceId = SelectedDevice.DeviceId,
                SelfTestType = testType
            }, connectTimeoutMs: 5000, ct: _cts.Token);

            if (resp.Dashboard is not null)
            {
                ApplyDashboardSnapshot(resp.Dashboard, loadSettingsIntoEditor: false);
            }

            ShowBanner(resp.Ok ? (resp.Message ?? "Self-test started.") : (resp.Error ?? "Self-test did not start."));
        }
        catch (Exception ex)
        {
            ShowBanner($"Self-test request failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ExportReportAsync(string format)
    {
        if (SelectedDevice is null) return;
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest
            {
                Command = "ExportData",
                DeviceId = SelectedDevice.DeviceId,
                StartUtc = _presetStartUtc,
                EndUtc = DateTimeOffset.UtcNow,
                ExportFormat = format
            }, ct: _cts.Token);

            ShowBanner(resp.Ok
                ? $"Exported {format.ToUpperInvariant()} report to {resp.Message}"
                : $"Export failed: {resp.Error}");
        }
        catch (Exception ex)
        {
            ShowBanner($"Export error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task AcknowledgeAlertsAsync()
    {
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Command = "AcknowledgeAlerts" }, ct: _cts.Token);
            if (resp.Dashboard is not null)
            {
                ApplyDashboardSnapshot(resp.Dashboard, loadSettingsIntoEditor: false);
            }

            ShowBanner("All alerts marked as read.");
        }
        catch (Exception ex)
        {
            ShowBanner($"Failed to update alerts: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ClearAlertsAsync()
    {
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Command = "ClearAlerts" }, ct: _cts.Token);
            if (resp.Dashboard is not null)
            {
                ApplyDashboardSnapshot(resp.Dashboard, loadSettingsIntoEditor: false);
            }

            ShowBanner("All alerts cleared.");
        }
        catch (Exception ex)
        {
            ShowBanner($"Failed to clear alerts: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            ByteFormatter.CurrentStandard = EditUseBinaryUnits ? ByteUnitStandard.BinaryIec : ByteUnitStandard.DecimalSi;

            var customMap = new Dictionary<string, double>();
            if (SelectedDevice is not null &&
                double.TryParse(EditCustomRatedTbwTb.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double customTb) &&
                customTb > 0)
            {
                customMap[SelectedDevice.DeviceId] = customTb;
            }

            var newSettings = new DaemonSettings
            {
                FlushIntervalMinutes = Math.Clamp(EditFlushMinutes, 1, 120),
                SmartPollIntervalSeconds = 60,
                SpikeThresholdGbPer5Min = Math.Max(0.5, EditSpikeThresholdGb),
                WarningTempCelsius = Math.Clamp(EditWarningTempC, 45, 105),
                CriticalTempCelsius = Math.Clamp(EditCriticalTempC, 55, 115),
                EnableDesktopNotifications = EditEnableNotifications,
                CloseToTray = EditCloseToTray,
                DarkTheme = EditDarkTheme,
                StartDaemonAtLogin = EditStartAtLogin,
                UseBinaryUnits = EditUseBinaryUnits,
                CustomRatedTbwTb = customMap
            };

            var resp = await _ipc.SendAsync(new IpcRequest
            {
                Command = "UpdateSettings",
                Settings = newSettings
            }, ct: _cts.Token);

            if (resp.Dashboard is not null)
            {
                ApplyDashboardSnapshot(resp.Dashboard, loadSettingsIntoEditor: true);
            }

            ShowBanner(resp.Ok ? (resp.Message ?? "Preferences saved.") : $"Error: {resp.Error}");
        }
        catch (Exception ex)
        {
            ShowBanner($"Could not save preferences: {ex.Message}");
        }
    }

    [RelayCommand]
    private void QuitGuiOnly()
    {
        RequestQuitGuiKeepDaemon?.Invoke();
    }

    [RelayCommand]
    private async Task StopDaemonAndQuitAsync()
    {
        try
        {
            await _ipc.SendAsync(new IpcRequest { Command = "ShutdownDaemon" }, connectTimeoutMs: 1000);
        }
        catch
        {
            // Ignore if already offline
        }

        RequestStopDaemonAndQuitAll?.Invoke();
    }

    private async Task RefreshDashboardAsync(bool loadSettingsIntoEditor)
    {
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Command = "GetDashboard" }, ct: _cts.Token);
            if (resp is { Ok: true, Dashboard: not null })
            {
                Dispatcher.UIThread.Post(() => ApplyDashboardSnapshot(resp.Dashboard, loadSettingsIntoEditor));
            }
        }
        catch
        {
            // Handled by reconnect loop
        }
    }

    private async Task RefreshTimeMachinePresetAsync(string preset)
    {
        _activePreset = preset;
        SelectedPresetKey = preset;
        IsSubWindowBrushed = false;

        var now = DateTimeOffset.UtcNow;
        var (start, label) = preset switch
        {
            "1h" => (now.AddHours(-1), "Last 60 minutes · 5-min buckets"),
            "24h" => (now.AddHours(-24), "Last 24 hours · 5-min buckets"),
            "7d" => (now.AddDays(-7), "Last 7 days · 5-min buckets"),
            "30d" => (now.AddDays(-30), "Last 30 days · hourly rollup"),
            "1y" => (now.AddDays(-365), "Last 12 months · daily rollup"),
            "all" => (now.AddYears(-10), "Full drive history"),
            _ => (now.AddHours(-24), "Last 24 hours")
        };

        _presetStartUtc = start;
        _presetEndUtc = now;
        ActiveRangeLabel = label;

        await QueryInspectionWindowAsync(start, now.AddMinutes(1), updateChartSeries: true);
    }

    private async Task QueryInspectionWindowAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, bool updateChartSeries)
    {
        var devId = SelectedDevice?.DeviceId;
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest
            {
                Command = "InspectRange",
                DeviceId = devId,
                StartUtc = startUtc,
                EndUtc = endUtc
            }, ct: _cts.Token);

            if (resp is { Ok: true, Inspection: not null })
            {
                var insp = resp.Inspection;
                Dispatcher.UIThread.Post(() =>
                {
                    if (updateChartSeries)
                    {
                        ChartPoints = insp.Points;
                    }

                    WindowWrittenDeltaFormatted = $"+{ByteFormatter.FormatBytes(insp.TotalBytesWrittenDelta)}";
                    WindowReadDeltaFormatted = ByteFormatter.FormatBytes(insp.TotalBytesReadDelta);
                    WindowPeakTempFormatted = insp.PeakTempCelsius > 0 ? $"{insp.PeakTempCelsius:F1} °C" : "-- °C";
                    WindowAvgRateFormatted = ByteFormatter.FormatRate(insp.AvgWriteRateBytesPerSec);

                    WindowTopProcesses.Clear();
                    int rank = 1;
                    foreach (var p in insp.TopProcesses)
                    {
                        WindowTopProcesses.Add(new ProcessCulpritRow
                        {
                            Rank = rank++,
                            ProcessName = p.ProcessName,
                            ExecutablePath = string.IsNullOrEmpty(p.ExecutablePath) ? "—" : p.ExecutablePath,
                            WrittenFormatted = ByteFormatter.FormatBytes(p.BytesWritten),
                            ReadFormatted = ByteFormatter.FormatBytes(p.BytesRead),
                            SharePercentage = p.SharePercentage
                        });
                    }

                    HasWindowCulprits = WindowTopProcesses.Count > 0;
                    HasNoWindowCulprits = !HasWindowCulprits;
                });
            }
        }
        catch
        {
            // Ignore transient IPC errors
        }
    }

    private void ApplyDashboardSnapshot(DashboardSnapshot dash, bool loadSettingsIntoEditor)
    {
        IsDaemonConnected = true;
        ConnectionBadgeText = "Daemon active";

        if (Devices.Count != dash.Devices.Count ||
            !Devices.Select(d => d.DeviceId).SequenceEqual(dash.Devices.Select(d => d.DeviceId)))
        {
            var prevId = SelectedDevice?.DeviceId;
            Devices.Clear();
            foreach (var d in dash.Devices) Devices.Add(d);
            SelectedDevice = Devices.FirstOrDefault(d => d.DeviceId == prevId) ?? Devices.FirstOrDefault();
        }

        var dev = SelectedDevice ?? dash.Devices.FirstOrDefault();
        ByteFormatter.CurrentStandard = dash.Settings.UseBinaryUnits ? ByteUnitStandard.BinaryIec : ByteUnitStandard.DecimalSi;

        if (dev is not null)
        {
            DriveModel = dev.Model;
            CapacityFormatted = ByteFormatter.FormatBytes(dev.CapacityBytes, 2);
            DriveHeaderMeta = $"{dev.Model}  ·  {dev.PcieSummary}  ·  {CapacityFormatted}";
            ControllerSpecText = $"{dev.DevicePath} ({dev.PcieSummary}, {dev.PowerState})";
            SerialAndFirmwareText = $"{dev.SerialNumber} · FW {dev.FirmwareRevision}";
            PcieBadgeText = dev.PcieSummary;
            PowerStateText = dev.PowerState;

            dash.LatestSmart.TryGetValue(dev.DeviceId, out var smart);
            dash.Forecasts.TryGetValue(dev.DeviceId, out var forecast);
            dash.LatestLive.TryGetValue(dev.DeviceId, out var live);

            if (forecast is not null)
            {
                HealthScore = forecast.HealthScorePrecise;
                HealthScoreFormatted = forecast.HealthScorePrecise >= 99.95 ? "100%" : $"{forecast.HealthScorePrecise:F1}%";
                HealthGrade = forecast.HealthGrade;
                WearSubtitle = $"{forecast.HealthScorePrecise:F1}% health · {forecast.HardwarePercentUsed}% wear";
                HealthSummaryLine = $"{forecast.HardwarePercentUsed}% SMART wear · {smart?.AvailableSpare ?? 100}% spare";

                CurrentTbwFormatted = ByteFormatter.FormatBytes(forecast.CurrentTbwBytes, 3);
                RatedTbwFormatted = $"{ByteFormatter.FormatBytes(forecast.RatedTbwBytes, 1)} rated";
                TbwProgressPercent = Math.Clamp(forecast.TbwUsedPercentage, 0, 100);
                RatedTbwSourceText = forecast.RatedTbwSource;
                TbwRawBytesTooltip = $"Exact: {forecast.CurrentTbwBytes:N0} bytes ({forecast.CurrentTbwBytes / 512_000L:N0} NVMe data units)";

                BurnRateFormatted = forecast.EffectiveBurnRateBytesPerDay > 0
                    ? $"{ByteFormatter.FormatBytes((long)forecast.EffectiveBurnRateBytesPerDay, 1)}/day"
                    : "< 1 MB/day";

                if (forecast.EstimatedDaysRemaining is { } days)
                {
                    RemainingLifeFormatted = days >= 365
                        ? $"{(days / 365.25):F1} yrs"
                        : $"{Math.Ceiling(days):F0} days";
                    ExhaustionDateFormatted = forecast.EstimatedExhaustionDateUtc?.ToLocalTime().ToString("MMM yyyy", CultureInfo.InvariantCulture) ?? "—";
                    EnduranceSublineText = $"{BurnRateFormatted} · Est. {ExhaustionDateFormatted}";
                }
                else
                {
                    RemainingLifeFormatted = "> 15 yrs";
                    ExhaustionDateFormatted = "—";
                    EnduranceSublineText = $"{BurnRateFormatted} · Nominal wear";
                }

                long todayW = live?.TodayBytesWritten ?? 0;
                TbwSublineText = $"+{ByteFormatter.FormatBytes(todayW, 2)} today · of {ByteFormatter.FormatBytes(forecast.RatedTbwBytes, 0)} ({forecast.CurrentTbwBytes:N0} B)";
            }

            if (smart is not null)
            {
                CurrentTempFormatted = $"{smart.CompositeTempCelsius:F1} °C";
                IsTempElevated = smart.CompositeTempCelsius >= dash.Settings.WarningTempCelsius;
                AvailableSpareFormatted = $"{smart.AvailableSpare}% (min {smart.SpareThreshold}%)";
                PowerOnHoursFormatted = $"{smart.PowerOnHours:N0} h ({(smart.PowerOnHours / 24.0):F0} d)";
                PowerCyclesFormatted = $"{smart.PowerCycles:N0}";
                UnsafeShutdownsFormatted = $"{smart.UnsafeShutdowns:N0}";
                MediaErrorsFormatted = $"{smart.MediaErrors:N0}";
                ControllerBusyFormatted = $"{smart.ControllerBusyMinutes:N0} min";
                TotalReadFormatted = ByteFormatter.FormatBytes(smart.TotalBytesRead, 2);

                UpdateSensorBadges(smart.Sensors, dash.Settings);
                PopulateSmartTable(smart);

                IsSelfTestRunning = smart.SelfTestPercentRemaining >= 0 ||
                                    smart.SelfTestStatus.Contains("progress", StringComparison.OrdinalIgnoreCase);
                SelfTestProgress = IsSelfTestRunning ? Math.Clamp(100 - smart.SelfTestPercentRemaining, 0, 100) : 0;
                SelfTestStatusText = IsSelfTestRunning
                    ? $"Self-test running · {100 - smart.SelfTestPercentRemaining}% complete"
                    : $"Self-test idle · Last status: {smart.SelfTestStatus}";
            }

            if (live is not null)
            {
                ApplyLiveTelemetryOnUi(live, dash.Settings);
            }
        }

        var st = dash.Status;
        var upSpan = TimeSpan.FromSeconds(st.UptimeSeconds);
        DaemonPidText = $"PID {st.Pid}";
        DaemonUptimeText = $"{(int)upSpan.TotalHours}h {upSpan.Minutes}m";
        DaemonBackendText = st.HardwareBackend;
        DaemonMemoryFormatted = $"{ByteFormatter.FormatBytes(st.MemoryWorkingSetBytes, 1)}";
        DatabaseSizeText = $"{ByteFormatter.FormatBytes(st.DatabaseSizeBytes, 1)} ({st.TotalStoredSnapshots:N0} rows)";
        DatabasePathText = st.DatabasePath;
        LastFlushSummary = st.LastFlushUtc.HasValue
            ? $"{st.LastFlushUtc.Value.ToLocalTime():HH:mm:ss} ({st.BufferedSamplesCount} RAM bucket)"
            : "Pending first flush";

        AlertItems.Clear();
        int unread = 0;
        foreach (var a in dash.Alerts)
        {
            if (!a.Acknowledged) unread++;
            AlertItems.Add(new AlertRow
            {
                Id = a.Id,
                TimestampText = a.TimestampUtc.ToLocalTime().ToString("MMM dd, HH:mm", CultureInfo.InvariantCulture),
                Severity = a.Severity,
                Category = a.Category,
                Title = a.Title,
                Message = a.Message,
                Acknowledged = a.Acknowledged,
                SeverityBrush = a.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)
                    ? new SolidColorBrush(Color.Parse("#D92B21"))
                    : new SolidColorBrush(Color.Parse("#E8571F"))
            });
        }

        UnreadAlertsCount = unread;
        HasUnreadAlerts = unread > 0;
        HasAlerts = AlertItems.Count > 0;
        HasNoAlerts = !HasAlerts;

        if (loadSettingsIntoEditor)
        {
            EditFlushMinutes = dash.Settings.FlushIntervalMinutes;
            EditSpikeThresholdGb = dash.Settings.SpikeThresholdGbPer5Min;
            EditWarningTempC = dash.Settings.WarningTempCelsius;
            EditCriticalTempC = dash.Settings.CriticalTempCelsius;
            EditEnableNotifications = dash.Settings.EnableDesktopNotifications;
            EditCloseToTray = dash.Settings.CloseToTray;
            EditDarkTheme = dash.Settings.DarkTheme;
            EditStartAtLogin = dash.Status.AutostartInstalled;
            EditUseBinaryUnits = dash.Settings.UseBinaryUnits;

            if (dev is not null && dash.Settings.CustomRatedTbwTb.TryGetValue(dev.DeviceId, out double cTb))
            {
                EditCustomRatedTbwTb = cTb.ToString("F0", CultureInfo.InvariantCulture);
            }
            else
            {
                EditCustomRatedTbwTb = string.Empty;
            }
        }
    }

    private void ApplyLiveTelemetryOnUi(LiveTelemetry live, DaemonSettings? settings = null)
    {
        if (SelectedDevice is not null && live.DeviceId != SelectedDevice.DeviceId)
        {
            return;
        }

        LiveReadRateFormatted = ByteFormatter.FormatRate(live.ReadBytesPerSec);
        LiveWriteRateFormatted = ByteFormatter.FormatRate(live.WriteBytesPerSec);
        LiveIopsFormatted = $"{live.ReadIops:F0} R · {live.WriteIops:F0} W IOPS";
        CurrentTempFormatted = $"{live.CompositeTempCelsius:F1} °C";
        IsTempElevated = live.CompositeTempCelsius >= (settings?.WarningTempCelsius ?? EditWarningTempC);
        CurrentTbwFormatted = ByteFormatter.FormatBytes(live.EstimatedCurrentTbwBytes, 3);
        TodayWrittenFormatted = $"+{ByteFormatter.FormatBytes(live.TodayBytesWritten, 2)} today";
        TodayReadFormatted = $"{ByteFormatter.FormatBytes(live.TodayBytesRead, 2)} read today";
        TbwRawBytesTooltip = $"Exact: {live.EstimatedCurrentTbwBytes:N0} bytes ({live.EstimatedCurrentTbwBytes / 512_000L:N0} NVMe data units)";
        TbwSublineText = $"{TodayWrittenFormatted} · of {RatedTbwFormatted} ({live.EstimatedCurrentTbwBytes:N0} B)";
        PowerStateText = live.PowerState;

        if (live.Sensors.Count > 0)
        {
            UpdateSensorBadges(live.Sensors, settings);
        }

        _readRing.RemoveAt(0);
        _readRing.Add(live.ReadBytesPerSec);
        _writeRing.RemoveAt(0);
        _writeRing.Add(live.WriteBytesPerSec);
        ReadRateHistory = _readRing.ToList();
        WriteRateHistory = _writeRing.ToList();
        LiveChartRevision++;

        if (IsLiveProcessesPage)
        {
            LiveProcesses.Clear();
            bool markedTopWriter = false;
            foreach (var p in live.TopActiveProcesses)
            {
                bool isTop = !markedTopWriter && p.WriteBytesPerSec >= 10_240;
                if (isTop) markedTopWriter = true;

                LiveProcesses.Add(new LiveProcessRow
                {
                    Pid = p.Pid,
                    ProcessName = p.ProcessName,
                    WriteRateText = ByteFormatter.FormatRate(p.WriteBytesPerSec),
                    ReadRateText = ByteFormatter.FormatRate(p.ReadBytesPerSec),
                    SessionWrittenText = ByteFormatter.FormatBytes(p.SessionBytesWritten),
                    SessionReadText = ByteFormatter.FormatBytes(p.SessionBytesRead),
                    ExecutablePath = string.IsNullOrWhiteSpace(p.ExecutablePath) ? "—" : p.ExecutablePath,
                    IsActivelyWriting = isTop
                });
            }
        }

        var trayState = HealthScore < 40 || live.CompositeTempCelsius >= EditCriticalTempC
            ? TrayIconGenerator.TrayState.Critical
            : (HealthScore < 75 || live.CompositeTempCelsius >= EditWarningTempC
                ? TrayIconGenerator.TrayState.Warning
                : TrayIconGenerator.TrayState.Healthy);

        var tooltip = $"NVMe Sentinel · {live.CompositeTempCelsius:F1}°C · {HealthScore}% health · +{ByteFormatter.FormatBytes(live.TodayBytesWritten, 1)} today";
        TrayStatusUpdated?.Invoke(tooltip, trayState);
    }

    private void UpdateSensorBadges(List<SensorReading> sensors, DaemonSettings? settings)
    {
        double warn = settings?.WarningTempCelsius ?? EditWarningTempC;

        SensorBadges.Clear();
        foreach (var s in sensors)
        {
            SensorBadges.Add(new SensorBadgeItem
            {
                Label = s.Label,
                ValueText = $"{s.Celsius:F1} °C",
                IsHot = s.Celsius >= warn
            });
        }

        if (sensors.Count >= 2)
        {
            var extras = sensors.Skip(1).Take(2).Select(s => $"{s.Label}: {s.Celsius:F0} °C");
            ThermalSensorsInlineText = string.Join(" · ", extras);
        }
        else
        {
            ThermalSensorsInlineText = $"Warn at {warn:F0} °C";
        }
    }

    private void PopulateSmartTable(SmartSnapshot s)
    {
        SmartAttributes.Clear();

        void AddRow(string id, string name, string val, string raw, string status, bool attention, string desc)
        {
            SmartAttributes.Add(new SmartAttributeRow
            {
                IdCode = id,
                Name = name,
                FormattedValue = val,
                RawValue = raw,
                Status = status,
                IsAttention = attention,
                Description = desc
            });
        }

        AddRow("01", "Critical warning", $"0x{s.CriticalWarningFlags:X2}", s.CriticalWarningFlags.ToString(),
            s.CriticalWarningFlags == 0 ? "Normal" : "Critical", s.CriticalWarningFlags != 0,
            "Controller warning bitmask for spare blocks, temperature, and media state");

        bool tempHot = s.CompositeTempCelsius >= EditWarningTempC;
        AddRow("02", "Composite temperature", $"{s.CompositeTempCelsius:F1} °C", $"{Math.Round(s.CompositeTempCelsius + 273.15)} K",
            tempHot ? "Elevated" : "Normal", tempHot,
            "Combined controller and NAND flash thermal reading");

        AddRow("03", "Available spare", $"{s.AvailableSpare}%", $"{s.AvailableSpare}",
            s.AvailableSpare <= s.SpareThreshold ? "Low" : "Normal", s.AvailableSpare <= s.SpareThreshold,
            "Remaining spare NAND block capacity");

        AddRow("04", "Available spare threshold", $"{s.SpareThreshold}%", $"{s.SpareThreshold}",
            "Limit", false, "Manufacturer spare block alert threshold");

        AddRow("05", "Percentage used", $"{s.PercentUsed}%", $"{s.PercentUsed}",
            s.PercentUsed >= 100 ? "Rated limit" : "Normal", s.PercentUsed >= 100,
            "Vendor wear indicator (100% equals design endurance rating)");

        AddRow("06", "Data units read", ByteFormatter.FormatBytes(s.TotalBytesRead, 3), $"{s.TotalBytesRead / 512_000L:N0}",
            "Normal", false, "Total host data read from the namespace");

        AddRow("07", "Data units written (TBW)", ByteFormatter.FormatBytes(s.TotalBytesWritten, 3), $"{s.TotalBytesWritten / 512_000L:N0}",
            "Normal", false, "Total host data written across drive lifetime");

        AddRow("0A", "Controller busy time", $"{s.ControllerBusyMinutes:N0} min", $"{s.ControllerBusyMinutes}",
            "Normal", false, "Minutes the controller was actively servicing I/O");

        AddRow("0B", "Power cycles", $"{s.PowerCycles:N0}", $"{s.PowerCycles}",
            "Normal", false, "Full power-on cycles recorded");

        AddRow("0C", "Power-on hours", $"{s.PowerOnHours:N0} h", $"{s.PowerOnHours}",
            "Normal", false, "Total powered hours");

        AddRow("0D", "Unsafe shutdowns", $"{s.UnsafeShutdowns:N0}", $"{s.UnsafeShutdowns}",
            s.UnsafeShutdowns > 100 ? "Review" : "Logged", false,
            "Power losses without host shutdown notification");

        AddRow("0E", "Media and data integrity errors", $"{s.MediaErrors:N0}", $"{s.MediaErrors}",
            s.MediaErrors == 0 ? "Normal" : "Critical", s.MediaErrors != 0,
            "Unrecovered ECC or CRC data errors");

        AddRow("0F", "Error log entries", $"{s.ErrorLogEntries:N0}", $"{s.ErrorLogEntries}",
            "Normal", false, "Lifetime controller error log count");

        AddRow("10", "Warning temperature time", $"{s.WarningTempMinutes:N0} min", $"{s.WarningTempMinutes}",
            s.WarningTempMinutes > 15 ? "Elevated" : "Normal", s.WarningTempMinutes > 15,
            $"Minutes above {s.WarningTempCelsius:F0} °C warning threshold");

        AddRow("11", "Critical temperature time", $"{s.CriticalTempMinutes:N0} min", $"{s.CriticalTempMinutes}",
            s.CriticalTempMinutes > 0 ? "Critical" : "Normal", s.CriticalTempMinutes > 0,
            $"Minutes above {s.CriticalTempCelsius:F0} °C critical threshold");
    }

    private void ShowBanner(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ActionBannerMessage = message;
            HasActionBanner = !string.IsNullOrWhiteSpace(message);
        });
    }

    private async Task RunLiveSubscriptionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _ipc.SubscribeLiveAsync(live =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        IsDaemonConnected = true;
                        ConnectionBadgeText = "Daemon active";
                        ApplyLiveTelemetryOnUi(live);
                    });
                    return Task.CompletedTask;
                }, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                Dispatcher.UIThread.Post(() =>
                {
                    IsDaemonConnected = false;
                    ConnectionBadgeText = "Reconnecting…";
                });
                await DaemonProcessLauncher.EnsureDaemonRunningAsync(_ipc, ct);
                await Task.Delay(1500, ct);
            }
        }
    }

    private async Task RunPeriodicDashboardSyncLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (!IsDaemonConnected) continue;
            await RefreshDashboardAsync(loadSettingsIntoEditor: false);
            if (!IsSubWindowBrushed && SelectedPageIndex is 0 or 1)
            {
                await RefreshTimeMachinePresetAsync(_activePreset);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
