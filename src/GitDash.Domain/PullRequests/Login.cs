namespace GitDash.Domain.PullRequests;

/// <summary>GitHub logins are case-insensitive; keep that rule in one place.</summary>
public static class Login
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && Comparer.Equals(a, b);

    public static bool ContainsLogin(this IEnumerable<string> logins, string? login) =>
        login is not null && logins.Any(l => Comparer.Equals(l, login));

    /// <summary>
    /// A friendly first name from a GitHub login: "gowtham-krishnan" → "Gowtham", "JaneDoe" → "Jane", "john_smith42" → "John".
    /// A login with no separators and no capitals stays whole ("octocat" → "Octocat").
    /// </summary>
    public static string FirstName(string login)
    {
        if (string.IsNullOrWhiteSpace(login)) return "?";
        var word = login.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? login;
        var end = 0;
        while (end < word.Length && char.IsLetter(word[end])) end++;
        if (end == 0) return login; // all digits or symbols: keep it as it is
        word = word[..end];
        // camelCase: stop at the second capital letter ("JaneDoe" → "Jane")
        for (var i = 1; i < word.Length; i++)
        {
            if (char.IsUpper(word[i]) && char.IsLower(word[i - 1]))
            {
                word = word[..i];
                break;
            }
        }

        return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
    }
}
