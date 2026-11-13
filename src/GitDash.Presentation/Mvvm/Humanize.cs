using System.Globalization;

namespace GitDash.Presentation.Mvvm;

/// <summary>Short, human-friendly text for durations and timestamps.</summary>
public static class Humanize
{
    public static string Age(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return "just now";
    }

    public static string Ago(TimeSpan span) => Age(span) is "just now" ? "just now" : $"{Age(span)} ago";

    public static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>The chosen time style; set from the settings so every page formats the same way.</summary>
    public static GitDash.Application.Settings.TimeFormat Format { get; set; }

    /// <summary>The moment in the chosen zone: local time, or UTC.</summary>
    public static DateTime Zoned(DateTimeOffset at) =>
        Format == GitDash.Application.Settings.TimeFormat.Utc ? at.UtcDateTime : at.ToLocalTime().DateTime;

    private static string TimeOfDay(DateTime t, bool seconds) => Format switch
    {
        GitDash.Application.Settings.TimeFormat.Hour24 => t.ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.CurrentCulture),
        GitDash.Application.Settings.TimeFormat.Hour12 => t.ToString(seconds ? "h:mm:ss tt" : "h:mm tt", CultureInfo.CurrentCulture),
        GitDash.Application.Settings.TimeFormat.Utc => t.ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture) + " UTC",
        _ => t.ToString(seconds ? "T" : "t", CultureInfo.CurrentCulture), // the operating system's own short / long time pattern
    };

    public static string Stamp(DateTimeOffset at)
    {
        var t = Zoned(at);
        return $"{t.ToString("d MMM", CultureInfo.CurrentCulture)}, {TimeOfDay(t, false)}";
    }

    public static string Time(DateTimeOffset at, bool seconds = false) => TimeOfDay(Zoned(at), seconds);

    /// <summary>The calendar day in the chosen zone, used to group entries by day.</summary>
    public static DateTime DayOf(DateTimeOffset at) => Zoned(at).Date;

    /// <summary>Day heading for grouped lists: Today, Yesterday, or the full date.</summary>
    public static string Day(DateTimeOffset at, DateTimeOffset now)
    {
        var day = DayOf(at);
        var today = DayOf(now);
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.ToString(day.Year == today.Year ? "dddd, d MMMM" : "dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>"OPEN" or "changes requested" → "Open" / "Changes Requested".</summary>
    public static string Pascal(string text) =>
        string.Join(' ', text.Split([' ', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

    public static string Initials(string login)
    {
        var parts = login.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0].Length >= 2 ? parts[0][..2].ToUpperInvariant() : parts[0].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}",
        };
    }

    private static readonly string[] Palette =
    [
        "#7C5CFF", "#22D3EE", "#F472B6", "#34D399", "#FBBF24", "#60A5FA", "#FB7185", "#A78BFA", "#2DD4BF", "#F97316",
    ];

    /// <summary>A stable colour per login, so a person looks the same on every floor and every launch.</summary>
    public static string Accent(string login)
    {
        var hash = 17;
        foreach (var c in login.ToLowerInvariant()) hash = unchecked(hash * 31 + c);
        return Palette[(int)((uint)hash % Palette.Length)];
    }
}
