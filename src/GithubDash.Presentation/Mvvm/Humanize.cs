using System.Globalization;

namespace GithubDash.Presentation.Mvvm;

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

    public static string Stamp(DateTimeOffset at) =>
        at.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.CurrentCulture);

    public static string Time(DateTimeOffset at) =>
        at.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    /// <summary>Day heading for grouped lists: Today, Yesterday, or the full date.</summary>
    public static string Day(DateTimeOffset at, DateTimeOffset now)
    {
        var day = at.ToLocalTime().Date;
        var today = now.ToLocalTime().Date;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.ToString(day.Year == today.Year ? "dddd, d MMMM" : "dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
    }

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
