using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NvmeSentinel.Core.Storage;

namespace NvmeSentinel.App.Controls;

public sealed class LiveThroughputChartControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ReadHistoryProperty =
        AvaloniaProperty.Register<LiveThroughputChartControl, IReadOnlyList<double>?>(nameof(ReadHistory));

    public static readonly StyledProperty<IReadOnlyList<double>?> WriteHistoryProperty =
        AvaloniaProperty.Register<LiveThroughputChartControl, IReadOnlyList<double>?>(nameof(WriteHistory));

    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<LiveThroughputChartControl, int>(nameof(Revision));

    public static readonly StyledProperty<bool> IsDarkThemeProperty =
        AvaloniaProperty.Register<LiveThroughputChartControl, bool>(nameof(IsDarkTheme), true);

    static LiveThroughputChartControl()
    {
        AffectsRender<LiveThroughputChartControl>(ReadHistoryProperty, WriteHistoryProperty, RevisionProperty, IsDarkThemeProperty);
    }

    public IReadOnlyList<double>? ReadHistory
    {
        get => GetValue(ReadHistoryProperty);
        set => SetValue(ReadHistoryProperty, value);
    }

    public IReadOnlyList<double>? WriteHistory
    {
        get => GetValue(WriteHistoryProperty);
        set => SetValue(WriteHistoryProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public bool IsDarkTheme
    {
        get => GetValue(IsDarkThemeProperty);
        set => SetValue(IsDarkThemeProperty, value);
    }

    private bool? _cachedIsDark;
    private Pen? _cachedDashedPen;
    private Pen? _cachedSolidBasePen;
    private IBrush? _cachedLabelBrush;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 60 || h < 40) return;

        var plot = new Rect(0, 14, w, Math.Max(10, h - 30));

        if (_cachedIsDark != IsDarkTheme || _cachedDashedPen is null || _cachedLabelBrush is null || _cachedSolidBasePen is null)
        {
            _cachedIsDark = IsDarkTheme;
            var lineColor = IsDarkTheme ? Color.FromArgb(26, 239, 240, 241) : Color.FromArgb(30, 20, 20, 19);
            _cachedLabelBrush = IsDarkTheme ? new SolidColorBrush(Color.Parse("#8E9196")) : new SolidColorBrush(Color.Parse("#65645E"));
            _cachedDashedPen = new Pen(new SolidColorBrush(lineColor), 1, new DashStyle([2, 4], 0));
            _cachedSolidBasePen = new Pen(new SolidColorBrush(lineColor), 1);
        }

        var labelBrush = _cachedLabelBrush;
        var dashedPen = _cachedDashedPen;
        var solidBasePen = _cachedSolidBasePen;

        var reads = ReadHistory ?? [];
        var writes = WriteHistory ?? [];
        double peak = 1_000_000.0; // 1 MB/s minimum floor
        if (reads.Count > 0)
        {
            var validReads = reads.Where(double.IsFinite);
            if (validReads.Any()) peak = Math.Max(peak, validReads.Max() * 1.18);
        }
        if (writes.Count > 0)
        {
            var validWrites = writes.Where(double.IsFinite);
            if (validWrites.Any()) peak = Math.Max(peak, validWrites.Max() * 1.18);
        }

        // fx-chart style dashed rules with quiet inline labels on the left
        for (int i = 0; i <= 2; i++)
        {
            double frac = i / 2.0;
            double y = plot.Bottom - plot.Height * frac;
            context.DrawLine(i == 0 ? solidBasePen : dashedPen, new Point(plot.Left, y), new Point(plot.Right, y));

            if (i > 0)
            {
                var ft = new FormattedText(
                    ByteFormatter.FormatRate(peak * frac),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Monospace"),
                    11.0,
                    labelBrush);
                context.DrawText(ft, new Point(plot.Left + 2, y - ft.Height - 3));
            }
        }

        // Read trace: quiet neutral ink line (no fill, keeps visual calm)
        var readStrokeColor = IsDarkTheme ? Color.FromArgb(145, 239, 240, 241) : Color.FromArgb(140, 20, 20, 19);
        DrawSeries(context, plot, reads, peak, readStrokeColor, fillArea: false, strokeWidth: 1.5);

        // Write trace: the single precision accent (#E8571F) with soft fade
        DrawSeries(context, plot, writes, peak, Color.Parse("#E8571F"), fillArea: true, strokeWidth: 2.0);

        // Multi-point time axis markers across the 60-second live buffer
        string[] timeLabels = ["60s ago", "45s", "30s", "15s", "now"];
        for (int i = 0; i < timeLabels.Length; i++)
        {
            double frac = (double)i / (timeLabels.Length - 1);
            double x = plot.Left + plot.Width * frac;
            var ft = new FormattedText(timeLabels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Monospace"), 11.0, labelBrush);
            double drawX = i == 0
                ? plot.Left
                : i == timeLabels.Length - 1
                    ? plot.Right - ft.Width
                    : Math.Clamp(x - ft.Width / 2.0, plot.Left, plot.Right - ft.Width);
            context.DrawText(ft, new Point(drawX, plot.Bottom + 5));
        }
    }

    private static void DrawSeries(DrawingContext ctx, Rect plot, IReadOnlyList<double> values, double maxVal, Color color, bool fillArea, double strokeWidth)
    {
        if (values.Count < 2) return;

        int n = values.Count;
        var pts = new List<Point>(n);
        for (int i = 0; i < n; i++)
        {
            double x = plot.Left + plot.Width * i / (n - 1.0);
            double val = double.IsFinite(values[i]) ? values[i] : 0.0;
            double norm = maxVal > 0 ? Math.Clamp(val / maxVal, 0.0, 1.0) : 0.0;
            double y = plot.Bottom - plot.Height * norm;
            pts.Add(new Point(x, y));
        }

        if (fillArea)
        {
            var fillGeom = new StreamGeometry();
            using (var g = fillGeom.Open())
            {
                g.BeginFigure(new Point(pts[0].X, plot.Bottom), isFilled: true);
                foreach (var p in pts) g.LineTo(p);
                g.LineTo(new Point(pts[^1].X, plot.Bottom));
                g.EndFigure(isClosed: true);
            }

            var fillBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(52, color.R, color.G, color.B), 0.0),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0)
                }
            };
            ctx.DrawGeometry(fillBrush, null, fillGeom);
        }

        var lineGeom = new StreamGeometry();
        using (var g = lineGeom.Open())
        {
            g.BeginFigure(pts[0], isFilled: false);
            for (int i = 1; i < pts.Count; i++) g.LineTo(pts[i]);
            g.EndFigure(isClosed: false);
        }

        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(color), strokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), lineGeom);
    }
}
