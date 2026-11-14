using GitDash.Domain.PullRequests;

namespace GitDash.Presentation.Mvvm;

/// <summary>
/// Login → profile-picture lookup shared by every view that shows a person chip. Refreshed from each
/// monitor snapshot so chips anywhere in the app can show a picture without carrying a URL around.
/// </summary>
public static class AvatarDirectory
{
    private static IReadOnlyDictionary<string, string> _urls =
        new Dictionary<string, string>(Login.Comparer);

    public static event Action? Changed;

    public static void Update(IReadOnlyDictionary<string, string> avatars)
    {
        _urls = new Dictionary<string, string>(avatars, Login.Comparer);
        Changed?.Invoke();
    }

    public static string? UrlFor(string? login) =>
        !string.IsNullOrEmpty(login) && _urls.TryGetValue(login, out var url) ? url : null;
}
