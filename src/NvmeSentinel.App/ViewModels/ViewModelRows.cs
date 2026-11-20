using Avalonia.Media;

namespace NvmeSentinel.App.ViewModels;

public sealed class SensorBadgeItem
{
    public string Label { get; init; } = string.Empty;
    public string ValueText { get; init; } = string.Empty;
    public bool IsHot { get; init; }
}

public sealed class ProcessCulpritRow
{
    public int Rank { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string WrittenFormatted { get; init; } = string.Empty;
    public string ReadFormatted { get; init; } = string.Empty;
    public double SharePercentage { get; init; }
    public string ShareText => $"{SharePercentage:F1}%";
    public double BarWidth => Math.Clamp(SharePercentage * 1.15, 3.0, 115.0);
    public bool IsTopCulprit => Rank == 1;
    public bool IsSecondaryCulprit => Rank > 1;
}

public sealed class LiveProcessRow
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string WriteRateText { get; init; } = string.Empty;
    public string ReadRateText { get; init; } = string.Empty;
    public string SessionWrittenText { get; init; } = string.Empty;
    public string SessionReadText { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public bool IsActivelyWriting { get; init; }
}

public sealed class SmartAttributeRow
{
    public string IdCode { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string FormattedValue { get; init; } = string.Empty;
    public string RawValue { get; init; } = string.Empty;
    public string Status { get; init; } = "Normal";
    public string Description { get; init; } = string.Empty;
    public bool IsAttention { get; init; }
    public bool IsQuiet => !IsAttention;
}

public sealed class AlertRow
{
    public long Id { get; init; }
    public string TimestampText { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool Acknowledged { get; init; }
    public IBrush SeverityBrush { get; init; } = new SolidColorBrush(Color.Parse("#E8571F"));
}
