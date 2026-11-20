using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NvmeSentinel.App.Controls;
using NvmeSentinel.App.ViewModels;

namespace NvmeSentinel.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        if (this.FindControl<TimeMachineChartControl>("TimeMachineChart") is { } chart)
        {
            chart.RangeBrushed += async (start, end) =>
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    await vm.InspectBrushedSubWindowAsync(start, end);
                }
            };

            chart.BrushCleared += async () =>
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    await vm.ClearBrushAsync();
                }
            };
        }
    }

    public async Task RunScreenshotTourAsync(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        if (DataContext is not MainWindowViewModel vm) return;

        // Allow live stream & layout to settle
        await Task.Delay(1800);

        string[] names = ["01-overview", "02-time-machine", "03-live-processes", "04-smart-diagnostics", "05-alerts-settings"];
        for (int i = 0; i < names.Length; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm.NavigatePageCommand.Execute(i.ToString());
            });

            await Task.Delay(550);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                int w = Math.Max(1320, (int)Bounds.Width);
                int h = Math.Max(840, (int)Bounds.Height);
                Measure(new Size(w, h));
                Arrange(new Rect(0, 0, w, h));
                UpdateLayout();

                using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
                rtb.Render(this);
                rtb.Save(Path.Combine(outputDir, $"{names[i]}.png"));
            });
        }

        // Also capture Light Grade Overview to verify both grades
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.NavigatePageCommand.Execute("0");
            vm.ToggleThemeCommand.Execute(null);
        });
        await Task.Delay(550);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            int w = Math.Max(1320, (int)Bounds.Width);
            int h = Math.Max(840, (int)Bounds.Height);
            Measure(new Size(w, h));
            Arrange(new Rect(0, 0, w, h));
            UpdateLayout();

            using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            rtb.Render(this);
            rtb.Save(Path.Combine(outputDir, "06-overview-light.png"));
        });
    }
}
