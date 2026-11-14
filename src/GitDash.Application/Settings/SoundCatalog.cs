namespace GitDash.Application.Settings;

public sealed record SoundOption(string Id, string Name);

/// <summary>The built-in notification sounds. The sounds themselves are synthesised by the infrastructure layer.</summary>
public static class SoundCatalog
{
    public const string DefaultId = "chime";
    public const string CustomId = "custom";

    public static IReadOnlyList<SoundOption> BuiltIn { get; } =
    [
        new("chime", "Chime"),
        new("bell", "Bell"),
        new("beep", "Beep"),
        new("soft", "Soft pop"),
        new("digital", "Digital"),
        new("alarm", "Alarm clock"),
    ];

    public static bool IsBuiltIn(string? id) => BuiltIn.Any(s => s.Id == id);

    /// <summary>A valid id: the stored one when it still exists, otherwise the default.</summary>
    public static string Normalise(string? id) => id == CustomId || IsBuiltIn(id) ? id! : DefaultId;
}
