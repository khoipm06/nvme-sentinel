using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NvmeSentinel.Core.Models;
using NvmeSentinel.Core.Storage;

namespace NvmeSentinel.App.Controls;

public enum ChartMetricMode
{
    CumulativeTbw = 0,
    WriteDelta = 1,
    Temperature = 2,
    WearPercent = 3
}

public sealed class TimeMachineChartControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<TimeSeriesPoint>?> PointsProperty =
        AvaloniaProperty.Register<TimeMachineChartControl, IReadOnlyList<TimeSeriesPoint>?>(nameof(Points));

    public static readonly StyledProperty<ChartMetricMode> MetricModeProperty =
        AvaloniaProperty.Register<TimeMachineChartControl, ChartMetricMode>(nameof(MetricMode), ChartMetricMode.CumulativeTbw);

    public static readonly StyledProperty<bool> IsDarkThemeProperty =
        AvaloniaProperty.Register<TimeMachineChartControl, bool>(nameof(IsDarkTheme), true);

    public static readonly StyledProperty<double> WarningTempProperty =
        AvaloniaProperty.Register<TimeMachineChartControl, double>(nameof(WarningTemp), 75.0);

    public event Action<DateTimeOffset, DateTimeOffset>? RangeBrushed;
    public event Action? BrushCleared;

    private Point? _hoverPos;
    private bool _isDragging;
    private double _dragStartX;
    private double _dragCurrentX;
    private (double MinX, double MaxX)? _activeBrushPixels;

    static TimeMachineChartControl()
    {
        AffectsRender<TimeMachineChartControl>(PointsProperty, MetricModeProperty, IsDarkThemeProperty, WarningTempProperty);
    }

    public IReadOnlyList<TimeSeriesPoint>? Points
    {
        get => GetValue(PointsProperty);
        set
        {
            _activeBrushPixels = null;
            SetValue(PointsProperty, value);
        }
    }

    public ChartMetricMode MetricMode
    {
        get => GetValue(MetricModeProperty);
        set => SetValue(MetricModeProperty, value);
    }

    public bool IsDarkTheme
    {
        get => GetValue(IsDarkThemeProperty);
        set => SetValue(IsDarkThemeProperty, value);
    }

    public double WarningTemp
    {
        get => GetValue(WarningTempProperty);
        set => SetValue(WarningTempProperty, value);
    }

    public TimeMachineChartControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public void ClearSelection()
    {
        _activeBrushPixels = null;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        _hoverPos = pos;
        if (_isDragging)
        {
            _dragCurrentX = pos.X;
        }

        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_isDragging)
        {
            _hoverPos = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            _activeBrushPixels = null;
            _isDragging = false;
            BrushCleared?.Invoke();
            InvalidateVisual();
            return;
        }

        if (props.IsLeftButtonPressed)
        {
            if (e.ClickCount >= 2)
            {
                _activeBrushPixels = null;
                _isDragging = false;
                BrushCleared?.Invoke();
                InvalidateVisual();
                return;
            }

            var pos = e.GetPosition(this);
            _isDragging = true;
            _dragStartX = pos.X;
            _dragCurrentX = pos.X;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isDragging) return;

        _isDragging = false;
        e.Pointer.Capture(null);

        var plot = GetPlotRect();
        double x1 = Math.Clamp(Math.Min(_dragStartX, _dragCurrentX), plot.Left, plot.Right);
        double x2 = Math.Clamp(Math.Max(_dragStartX, _dragCurrentX), plot.Left, plot.Right);

        var pts = Points;
        if (pts is { Count: >= 2 } && (x2 - x1) >= 12.0)
        {
            _activeBrushPixels = (x1, x2);
            var tMin = pts[0].TimestampUtc;
            var tMax = pts[^1].EndTimestampUtc > tMin ? pts[^1].EndTimestampUtc : tMin.AddMinutes(5);
            double spanSec = Math.Max(1.0, (tMax - tMin).TotalSeconds);

            var selStart = tMin.AddSeconds(spanSec * ((x1 - plot.Left) / plot.Width));
            var selEnd = tMin.AddSeconds(spanSec * ((x2 - plot.Left) / plot.Width));
            RangeBrushed?.Invoke(selStart, selEnd);
        }
        else
        {
            _activeBrushPixels = null;
        }

        InvalidateVisual();
    }

    private Rect GetPlotRect()
    {
        const double top = 18;
        const double bottom = 26;
        return new Rect(
            0,
            top,
            Math.Max(20, Bounds.Width),
            Math.Max(20, Bounds.Height - top - bottom));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, Bounds.Width, Bounds.Height));

        var plot = GetPlotRect();
        if (plot.Width < 40 || plot.Height < 40) return;

        var accent = Color.Parse("#E8571F");
        var ruleColor = IsDarkTheme ? Color.FromArgb(26, 239, 240, 241) : Color.FromArgb(30, 20, 20, 19);
        var axisBrush = IsDarkTheme ? new SolidColorBrush(Color.Parse("#8E9196")) : new SolidColorBrush(Color.Parse("#65645E"));
        var dashedPen = new Pen(new SolidColorBrush(ruleColor), 1, new DashStyle([2, 4], 0));
        var solidPen = new Pen(new SolidColorBrush(ruleColor), 1);

        var pts = Points ?? [];
        if (pts.Count == 0)
        {
            var emptyFt = new FormattedText("Collecting initial drive sample…", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 13.0, axisBrush);
            context.DrawText(emptyFt, new Point((plot.Width - emptyFt.Width) / 2.0, plot.Top + (plot.Height - emptyFt.Height) / 2.0));
            return;
        }

        var values = pts.Select(ExtractValue).ToList();
        double minV = values.Min();
        double maxV = values.Max();

        switch (MetricMode)
        {
            case ChartMetricMode.CumulativeTbw:
            {
                double spread = Math.Max(maxV - minV, 100_000_000.0);
                minV = Math.Max(0, minV - spread * 0.14);
                maxV += spread * 0.14;
                break;
            }
            case ChartMetricMode.WriteDelta:
                minV = 0;
                maxV = Math.Max(maxV * 1.18, 10_000_000.0);
                break;
            case ChartMetricMode.Temperature:
                minV = Math.Min(25.0, Math.Floor((minV - 5) / 5.0) * 5.0);
                maxV = Math.Max(WarningTemp + 8.0, Math.Ceiling((maxV + 5) / 5.0) * 5.0);
                break;
            case ChartMetricMode.WearPercent:
                minV = 0;
                maxV = Math.Max(100.0, maxV + 5.0);
                break;
        }

        if (Math.Abs(maxV - minV) < 1e-6) maxV = minV + 1.0;

        // fx-chart style dashed rules with left-aligned monospace values sitting just above each line
        for (int i = 0; i <= 3; i++)
        {
            double frac = i / 3.0;
            double y = plot.Bottom - plot.Height * frac;
            context.DrawLine(i == 0 ? solidPen : dashedPen, new Point(plot.Left, y), new Point(plot.Right, y));

            if (i > 0)
            {
                double val = minV + (maxV - minV) * frac;
                var ft = new FormattedText(
                    FormatMetricAxisValue(val),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Monospace"),
                    11.0,
                    axisBrush);
                context.DrawText(ft, new Point(plot.Left + 2, y - ft.Height - 3));
            }
        }

        var tStart = pts[0].TimestampUtc;
        var tEnd = pts.Count > 1 && pts[^1].TimestampUtc > tStart
            ? pts[^1].TimestampUtc
            : tStart.AddMinutes(5);
        double totalSec = Math.Max(1.0, (tEnd - tStart).TotalSeconds);

        var screenPts = new List<Point>(pts.Count);
        if (pts.Count == 1)
        {
            double y = plot.Bottom - plot.Height * ((values[0] - minV) / (maxV - minV));
            screenPts.Add(new Point(plot.Left + plot.Width * 0.5, y));
        }
        else
        {
            for (int i = 0; i < pts.Count; i++)
            {
                double xFrac = Math.Clamp((pts[i].TimestampUtc - tStart).TotalSeconds / totalSec, 0.0, 1.0);
                double yFrac = Math.Clamp((values[i] - minV) / (maxV - minV), 0.0, 1.0);
                screenPts.Add(new Point(plot.Left + plot.Width * xFrac, plot.Bottom - plot.Height * yFrac));
            }
        }

        DrawXAxisTicks(context, plot, tStart, tEnd, pts.Count == 1, axisBrush);

        var cardSurface = IsDarkTheme ? Color.Parse("#16171A") : Color.Parse("#FFFFFF");

        if (pts.Count == 1)
        {
            var refPen = new Pen(new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B)), 1.5, new DashStyle([3, 4], 0));
            context.DrawLine(refPen, new Point(plot.Left, screenPts[0].Y), new Point(plot.Right, screenPts[0].Y));
            context.DrawEllipse(new SolidColorBrush(accent), new Pen(new SolidColorBrush(cardSurface), 2), screenPts[0], 5, 5);
        }
        else if (MetricMode == ChartMetricMode.WriteDelta && pts.Count <= 96)
        {
            // taste-skill stat bar rule: neutral fill-3 bars by default; accent only on spikes & latest bar
            var neutralBarColor = IsDarkTheme ? Color.FromArgb(62, 239, 240, 241) : Color.FromArgb(52, 20, 20, 19);
            double barW = Math.Clamp(plot.Width / (pts.Count * 1.55), 3.0, 14.0);

            for (int i = 0; i < pts.Count; i++)
            {
                double barH = Math.Max(2.0, plot.Bottom - screenPts[i].Y);
                var barRect = new Rect(screenPts[i].X - barW / 2.0, plot.Bottom - barH, barW, barH);
                bool highlight = pts[i].IsAnomalySpike || i == pts.Count - 1;
                var fill = new SolidColorBrush(highlight ? accent : neutralBarColor);
                context.DrawRectangle(fill, null, new RoundedRect(barRect, 2, 2, 0, 0));
            }
        }
        else
        {
            var fillGeom = new StreamGeometry();
            using (var g = fillGeom.Open())
            {
                g.BeginFigure(new Point(screenPts[0].X, plot.Bottom), isFilled: true);
                foreach (var p in screenPts) g.LineTo(p);
                g.LineTo(new Point(screenPts[^1].X, plot.Bottom));
                g.EndFigure(isClosed: true);
            }

            var areaBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(56, accent.R, accent.G, accent.B), 0.0),
                    new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1.0)
                }
            };
            context.DrawGeometry(areaBrush, null, fillGeom);

            var lineGeom = new StreamGeometry();
            using (var g = lineGeom.Open())
            {
                g.BeginFigure(screenPts[0], isFilled: false);
                for (int i = 1; i < screenPts.Count; i++) g.LineTo(screenPts[i]);
                g.EndFigure(isClosed: false);
            }

            context.DrawGeometry(null, new Pen(new SolidColorBrush(accent), 2.0, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), lineGeom);

            // Subtle precision dots on anomaly spikes
            for (int i = 0; i < pts.Count; i++)
            {
                if (pts[i].IsAnomalySpike)
                {
                    context.DrawEllipse(new SolidColorBrush(accent), new Pen(new SolidColorBrush(cardSurface), 2.0), screenPts[i], 4.5, 4.5);
                }
            }
        }

        // Active brush window
        (double bx1, double bx2)? brush = _isDragging
            ? (Math.Clamp(Math.Min(_dragStartX, _dragCurrentX), plot.Left, plot.Right),
               Math.Clamp(Math.Max(_dragStartX, _dragCurrentX), plot.Left, plot.Right))
            : _activeBrushPixels;

        if (brush is { } b && (b.bx2 - b.bx1) > 4)
        {
            var bandRect = new Rect(b.bx1, plot.Top, b.bx2 - b.bx1, plot.Height);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(26, accent.R, accent.G, accent.B)), bandRect);
            var railPen = new Pen(new SolidColorBrush(accent), 1.2);
            context.DrawLine(railPen, new Point(b.bx1, plot.Top), new Point(b.bx1, plot.Bottom));
            context.DrawLine(railPen, new Point(b.bx2, plot.Top), new Point(b.bx2, plot.Bottom));
        }

        // Hover cursor & crisp fx-chart pill tooltip
        if (_hoverPos is { } hp && plot.Contains(hp) && !_isDragging)
        {
            int nearestIdx = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < screenPts.Count; i++)
            {
                double d = Math.Abs(screenPts[i].X - hp.X);
                if (d < bestDist)
                {
                    bestDist = d;
                    nearestIdx = i;
                }
            }

            var targetPt = screenPts[nearestIdx];
            var sample = pts[nearestIdx];

            var cursorColor = IsDarkTheme ? Color.FromArgb(65, 239, 240, 241) : Color.FromArgb(65, 20, 20, 19);
            context.DrawLine(new Pen(new SolidColorBrush(cursorColor), 1), new Point(targetPt.X, plot.Top), new Point(targetPt.X, plot.Bottom));
            context.DrawEllipse(new SolidColorBrush(accent), new Pen(new SolidColorBrush(cardSurface), 2.0), targetPt, 5.0, 5.0);

            DrawHoverTooltip(context, plot, targetPt, sample);
        }
    }

    private double ExtractValue(TimeSeriesPoint p) => MetricMode switch
    {
        ChartMetricMode.CumulativeTbw => p.TotalBytesWritten,
        ChartMetricMode.WriteDelta => p.DeltaBytesWritten,
        ChartMetricMode.Temperature => p.MaxTempCelsius > 0 ? p.MaxTempCelsius : p.AvgTempCelsius,
        ChartMetricMode.WearPercent => p.PercentUsed,
        _ => p.TotalBytesWritten
    };

    private string FormatMetricAxisValue(double v) => MetricMode switch
    {
        ChartMetricMode.CumulativeTbw => ByteFormatter.FormatBytes((long)Math.Max(0, v), 3),
        ChartMetricMode.WriteDelta => ByteFormatter.FormatBytes((long)Math.Max(0, v), 1),
        ChartMetricMode.Temperature => $"{v:F0} °C",
        ChartMetricMode.WearPercent => $"{v:F0}%",
        _ => v.ToString("F1", CultureInfo.InvariantCulture)
    };

    private static void DrawXAxisTicks(
        DrawingContext ctx,
        Rect plot,
        DateTimeOffset tStart,
        DateTimeOffset tEnd,
        bool singlePoint,
        IBrush axisBrush)
    {
        if (singlePoint)
        {
            var localTxt = tStart.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " · Initial baseline";
            var ft = new FormattedText(localTxt, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Monospace"), 11.0, axisBrush);
            ctx.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2.0, plot.Bottom + 7));
            return;
        }

        double spanHours = (tEnd - tStart).TotalHours;
        string fmt = spanHours <= 36
            ? "HH:mm"
            : spanHours <= 24 * 45
                ? "MMM dd HH:mm"
                : "yyyy-MM-dd";

        const int ticks = 5;
        for (int i = 0; i <= ticks; i++)
        {
            double frac = (double)i / ticks;
            double x = plot.Left + plot.Width * frac;
            var ts = tStart.AddSeconds((tEnd - tStart).TotalSeconds * frac).ToLocalTime();
            var ft = new FormattedText(ts.ToString(fmt, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Monospace"), 11.0, axisBrush);
            double drawX = i == 0
                ? plot.Left
                : i == ticks
                    ? plot.Right - ft.Width
                    : Math.Clamp(x - ft.Width / 2.0, plot.Left, plot.Right - ft.Width);
            ctx.DrawText(ft, new Point(drawX, plot.Bottom + 7));
        }
    }

    private void DrawHoverTooltip(DrawingContext ctx, Rect plot, Point anchor, TimeSeriesPoint sample)
    {
        var bg = IsDarkTheme ? Color.Parse("#0F1012") : Color.Parse("#FFFFFF");
        var border = IsDarkTheme ? Color.FromArgb(42, 239, 240, 241) : Color.FromArgb(38, 20, 20, 19);
        IBrush ink = IsDarkTheme ? new SolidColorBrush(Color.Parse("#EFF0F1")) : new SolidColorBrush(Color.Parse("#141413"));
        IBrush muted = IsDarkTheme ? new SolidColorBrush(Color.Parse("#8E9196")) : new SolidColorBrush(Color.Parse("#65645E"));

        var timeStr = sample.TimestampUtc.ToLocalTime().ToString("MMM dd, HH:mm", CultureInfo.InvariantCulture);
        var valStr = $"{ByteFormatter.FormatBytes(sample.TotalBytesWritten, 3)}  (+{ByteFormatter.FormatBytes(sample.DeltaBytesWritten, 2)} · {sample.MaxTempCelsius:F1} °C)";

        var tFt = new FormattedText(timeStr, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 12.0, muted);
        var vFt = new FormattedText(valStr, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Medium), 12.5, ink);

        double boxW = tFt.Width + vFt.Width + 28;
        const double boxH = 30;

        double bx = anchor.X + 14;
        if (bx + boxW > plot.Right - 4) bx = anchor.X - boxW - 14;
        bx = Math.Max(plot.Left + 4, bx);
        double by = Math.Clamp(anchor.Y - boxH / 2.0, plot.Top + 4, plot.Bottom - boxH - 4);

        var rect = new Rect(bx, by, boxW, boxH);
        ctx.DrawRectangle(new SolidColorBrush(bg), new Pen(new SolidColorBrush(border), 1), new RoundedRect(rect, 6));
        ctx.DrawText(tFt, new Point(bx + 10, by + (boxH - tFt.Height) / 2.0));
        ctx.DrawText(vFt, new Point(bx + 18 + tFt.Width, by + (boxH - vFt.Height) / 2.0));
    }
}
