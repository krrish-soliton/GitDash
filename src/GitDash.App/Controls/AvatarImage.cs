using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using GitDash.Application.Abstractions;

namespace GitDash.App.Controls;

/// <summary>
/// Shows a GitHub profile picture. Stays empty until the picture arrives (the initials underneath remain
/// visible), decodes at thumbnail size and shares decoded bitmaps between all instances.
/// </summary>
public sealed class AvatarImage : Image
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<AvatarImage, string?>(nameof(Url));

    private const int DecodeWidth = 96;
    private const int MaxCached = 300;
    private static readonly Dictionary<string, Bitmap> Cache = [];
    private int _request;

    /// <summary>Set once at start-up by the composition root.</summary>
    public static IAvatarSource? Loader { get; set; }

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Image);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty) _ = LoadAsync(Url);
    }

    private async Task LoadAsync(string? url)
    {
        var request = ++_request;
        if (string.IsNullOrEmpty(url) || Loader is null)
        {
            Source = null;
            return;
        }

        if (Cache.TryGetValue(url, out var cached))
        {
            Source = cached;
            return;
        }

        Source = null;
        var bytes = await Loader.GetAsync(url); // resumes on the UI thread
        if (bytes is null || request != _request) return;

        try
        {
            if (!Cache.TryGetValue(url, out var bitmap))
            {
                using var stream = new MemoryStream(bytes);
                bitmap = Bitmap.DecodeToWidth(stream, DecodeWidth);
                if (Cache.Count >= MaxCached) Cache.Clear();
                Cache[url] = bitmap;
            }

            Source = bitmap;
        }
        catch (Exception)
        {
            // Not an image after all: keep showing the initials.
        }
    }
}
