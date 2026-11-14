using System.Globalization;

namespace GitDash.Domain.Updates;

/// <summary>A semantic version (1.2.3 or 1.2.3-beta.1). Build metadata after "+" is ignored.</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string PreRelease = "") : IComparable<AppVersion>
{
    public bool IsPreRelease => PreRelease.Length > 0;

    /// <summary>Reads "1.2.3", "v1.2.3", "1.2.3-rc.1" or "1.2.3+abc123"; the missing parts of "1.2" count as zero.</summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var pre = string.Empty;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
        }

        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4) return false;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length && i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }

        version = new AppVersion(numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    public static AppVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a version number.");

    public bool IsNewerThan(AppVersion other) => CompareTo(other) > 0;

    public int CompareTo(AppVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A release is newer than any of its own pre-releases.
        if (!IsPreRelease || !other.IsPreRelease) return other.IsPreRelease.CompareTo(IsPreRelease);
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xn = int.TryParse(x[i], NumberStyles.None, CultureInfo.InvariantCulture, out var xi);
            var yn = int.TryParse(y[i], NumberStyles.None, CultureInfo.InvariantCulture, out var yi);
            int c;
            if (xn && yn) c = xi.CompareTo(yi);
            else if (xn != yn) c = xn ? -1 : 1; // numbers sort before words
            else c = string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }

        return x.Length.CompareTo(y.Length);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(IsPreRelease ? "-" + PreRelease : string.Empty)}";

    public static bool operator <(AppVersion l, AppVersion r) => l.CompareTo(r) < 0;
    public static bool operator >(AppVersion l, AppVersion r) => l.CompareTo(r) > 0;
    public static bool operator <=(AppVersion l, AppVersion r) => l.CompareTo(r) <= 0;
    public static bool operator >=(AppVersion l, AppVersion r) => l.CompareTo(r) >= 0;
}
