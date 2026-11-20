using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NvmeSentinel.App.Controls;

public sealed class RadialHealthGauge : Control
{
    public static readonly StyledProperty<double> ScoreProperty =
        AvaloniaProperty.Register<RadialHealthGauge, double>(nameof(Score), 100.0);

    public static readonly StyledProperty<string> GradeProperty =
        AvaloniaProperty.Register<RadialHealthGauge, string>(nameof(Grade), "Good");

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<RadialHealthGauge, string>(nameof(Subtitle), "0% wear");

    public static readonly StyledProperty<bool> IsDarkThemeProperty =
        AvaloniaProperty.Register<RadialHealthGauge, bool>(nameof(IsDarkTheme), true);

    static RadialHealthGauge()
    {
        AffectsRender<RadialHealthGauge>(ScoreProperty, GradeProperty, SubtitleProperty, IsDarkThemeProperty);
    }

    public double Score
    {
        get => GetValue(ScoreProperty);
        set => SetValue(ScoreProperty, value);
    }

    public string Grade
    {
        get => GetValue(GradeProperty);
        set => SetValue(GradeProperty, value);
    }

    public string Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public bool IsDarkTheme
    {
        get => GetValue(IsDarkThemeProperty);
        set => SetValue(IsDarkThemeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 40 || h < 40) return;

        double size = Math.Min(w, h);
        var center = new Point(w / 2.0, h / 2.0 + 4);
        double radius = size * 0.40;
        double stroke = Math.Max(6.0, size * 0.055);

        const double startDeg = 135.0;
        const double totalSweepDeg = 270.0;
        double pct = Math.Clamp(Score / 100.0, 0.0, 1.0);

        var trackColor = IsDarkTheme ? Color.FromArgb(28, 239, 240, 241) : Color.FromArgb(28, 20, 20, 19);
        var trackPen = new Pen(new SolidColorBrush(trackColor), stroke, lineCap: PenLineCap.Round);
        DrawArc(context, center, radius, startDeg, totalSweepDeg, trackPen);

        // Ink by default; accent only when attention is warranted
        var arcColor = Score < 75
            ? Color.Parse("#E8571F")
            : (IsDarkTheme ? Color.Parse("#EFF0F1") : Color.Parse("#18181B"));

        if (pct > 0.005)
        {
            var valuePen = new Pen(new SolidColorBrush(arcColor), stroke, lineCap: PenLineCap.Round);
            DrawArc(context, center, radius, startDeg, totalSweepDeg * pct, valuePen);

            // Precision endpoint pip in signal orange
            double tipRad = (startDeg + totalSweepDeg * pct) * Math.PI / 180.0;
            var tipPt = new Point(center.X + Math.Cos(tipRad) * radius, center.Y + Math.Sin(tipRad) * radius);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#E8571F")), null, tipPt, stroke * 0.52, stroke * 0.52);
        }

        IBrush fgMain = IsDarkTheme ? new SolidColorBrush(Color.Parse("#EFF0F1")) : new SolidColorBrush(Color.Parse("#141413"));
        IBrush fgMuted = IsDarkTheme ? new SolidColorBrush(Color.Parse("#8E9196")) : new SolidColorBrush(Color.Parse("#65645E"));

        var scoreText = Score >= 99.95 ? "100%" : $"{Score:F1}%";
        var scoreFt = new FormattedText(
            scoreText,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            size * (Score >= 99.95 ? 0.20 : 0.175),
            fgMain);

        context.DrawText(scoreFt, new Point(center.X - scoreFt.Width / 2.0, center.Y - scoreFt.Height * 0.62));

        var subLine = string.IsNullOrWhiteSpace(Subtitle) ? Grade : $"{Grade} · {Subtitle}";
        var subFt = new FormattedText(
            subLine,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal),
            12.5,
            fgMuted);
        context.DrawText(subFt, new Point(center.X - subFt.Width / 2.0, center.Y + scoreFt.Height * 0.34));
    }

    private static void DrawArc(DrawingContext ctx, Point center, double radius, double startDeg, double sweepDeg, Pen pen)
    {
        if (sweepDeg <= 0.1) return;

        int segments = Math.Max(16, (int)Math.Ceiling(sweepDeg / 3.5));
        var geom = new StreamGeometry();
        using (var g = geom.Open())
        {
            for (int i = 0; i <= segments; i++)
            {
                double deg = startDeg + sweepDeg * i / segments;
                double rad = deg * Math.PI / 180.0;
                var pt = new Point(center.X + Math.Cos(rad) * radius, center.Y + Math.Sin(rad) * radius);
                if (i == 0) g.BeginFigure(pt, isFilled: false);
                else g.LineTo(pt);
            }

            g.EndFigure(isClosed: false);
        }

        ctx.DrawGeometry(null, pen, geom);
    }
}
