namespace GitDash.Domain.PullRequests;

/// <summary>GitHub logins are case-insensitive; keep that rule in one place.</summary>
public static class Login
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && Comparer.Equals(a, b);

    public static bool ContainsLogin(this IEnumerable<string> logins, string? login) =>
        login is not null && logins.Any(l => Comparer.Equals(l, login));
}
