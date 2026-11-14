using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitDash.Presentation.Mvvm;

namespace GitDash.App.Controls;

/// <summary>
/// A person shown as a small pill: round profile picture (initials until it loads) plus first name.
/// The full login only appears as the tooltip. Pass a login; the picture is looked up automatically.
/// </summary>
public sealed class UserChip : Border
{
    public static readonly StyledProperty<string?> LoginProperty =
        AvaloniaProperty.Register<UserChip, string?>(nameof(Login));

    public static readonly StyledProperty<string?> AvatarUrlProperty =
        AvaloniaProperty.Register<UserChip, string?>(nameof(AvatarUrl));

    private readonly TextBlock _initials = new()
    {
        FontSize = 9,
        FontWeight = FontWeight.Bold,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly AvatarImage _image = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _name = new()
    {
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 110
    };

    private readonly Border _circle = new()
    {
        Width = 18,
        Height = 18,
        CornerRadius = new CornerRadius(9),
        ClipToBounds = true
    };

    public UserChip()
    {
        Classes.Add("userchip");
        var grid = new Grid();
        grid.Children.Add(_initials);
        grid.Children.Add(_image);
        _circle.Child = grid;
        _name.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextBrush"));
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _circle, _name }
        };
    }

    public string? Login
    {
        get => GetValue(LoginProperty);
        set => SetValue(LoginProperty, value);
    }

    /// <summary>Optional explicit picture; otherwise the shared directory is consulted.</summary>
    public string? AvatarUrl
    {
        get => GetValue(AvatarUrlProperty);
        set => SetValue(AvatarUrlProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Border);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LoginProperty || change.Property == AvatarUrlProperty) Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AvatarDirectory.Changed += Refresh;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        AvatarDirectory.Changed -= Refresh;
        base.OnDetachedFromVisualTree(e);
    }

    private void Refresh()
    {
        var login = Login;
        IsVisible = !string.IsNullOrWhiteSpace(login);
        _name.Text = Humanize.FirstName(login ?? "");
        _initials.Text = Humanize.Initials(login ?? "");
        _circle.Background = new SolidColorBrush(Color.Parse(Humanize.Accent(login ?? "")));
        _image.Url = AvatarUrl ?? AvatarDirectory.UrlFor(login);
        ToolTip.SetTip(this, login);
    }
}
