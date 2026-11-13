using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace GitDash.App.Converters;

/// <summary>"#RRGGBB" → brush. Brushes are immutable and cached, so repeated use allocates nothing.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();
    private static readonly Dictionary<string, IBrush> Cache = [];

    public static IBrush Brush(string hex)
    {
        if (Cache.TryGetValue(hex, out var brush)) return brush;
        brush = Color.TryParse(hex, out var color) ? new ImmutableSolidColorBrush(color) : Brushes.Gray;
        Cache[hex] = brush;
        return brush;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Brush(value as string ?? "#8B93AD");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Notification tones and timeline kinds → their accent colour.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public static readonly ToneBrushConverter Instance = new();

    public static IBrush Brush(string? tone) => HexBrushConverter.Brush(tone?.ToLowerInvariant() switch
    {
        "assigned" or "reviewrequested" or "review" or "amber" => "#FBBF24",
        "approved" or "readyforreview" or "green" => "#34D399",
        "added" => "#60A5FA",
        "open" or "reopened" => "#2DD4BF",
        "changesrequested" => "#FB923C",
        "removed" or "closed" or "reviewrequestremoved" or "unassigned" or "reviewdismissed" or "red" => "#FB7185",
        "commit" or "forcepushed" or "task" => "#60A5FA",
        "merged" or "opened" or "violet" => "#A78BFA",
        "comment" or "reviewed" or "cyan" => "#22D3EE",
        "draft" or "converteddraft" => "#8B93AD",
        "timer" or "pomodoro" => "#F472B6",
        _ => "#8B93AD",
    });

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Brush(value?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Resource key → geometry, so view-models can name an icon without referencing the UI framework.</summary>
public sealed class GeometryKeyConverter : IValueConverter
{
    public static readonly GeometryKeyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && AvaloniaApplication.Current is { } app &&
        app.TryGetResource(key, app.ActualThemeVariant, out var resource)
            ? resource as Geometry
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the value's text equals the converter parameter.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public static readonly EqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(
            System.Convert.ToString(value, CultureInfo.InvariantCulture),
            System.Convert.ToString(parameter, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
