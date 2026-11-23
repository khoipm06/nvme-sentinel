using Avalonia.Controls;
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
}
