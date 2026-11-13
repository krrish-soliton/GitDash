using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using GitDash.Presentation.Markdown;
using GitDash.Application.Abstractions;

namespace GitDash.App.Controls;

/// <summary>
/// Renders Markdown blocks (see <see cref="MarkdownParser"/>) as native Avalonia text, no web view involved.
/// Links open through <see cref="LinkOpener"/>, which only allows github.com URLs.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<IReadOnlyList<MarkdownBlock>?> BlocksProperty =
        AvaloniaProperty.Register<MarkdownView, IReadOnlyList<MarkdownBlock>?>(nameof(Blocks));

    /// <summary>Set once at startup to the app's URL launcher.</summary>
    public static IUrlLauncher? LinkOpener { get; set; }

    static MarkdownView() => BlocksProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());

    public MarkdownView()
    {
        Spacing = 6;
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    public IReadOnlyList<MarkdownBlock>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var r) && r is IBrush b ? b : fallback;

    private void Rebuild()
    {
        Children.Clear();
        if (Blocks is null) return;

        var text = Brush("TextBrush", Brushes.White);
        var muted = Brush("MutedBrush", Brushes.Gray);
        var surface = Brush("Surface2Brush", Brushes.DimGray);
        var accent = Brush("AccentBrush", Brushes.MediumPurple);
        var numberCounter = 0;

        foreach (var block in Blocks)
        {
            numberCounter = block.Kind == BlockKind.Numbered ? numberCounter + 1 : 0;
            var indent = new Thickness(block.Level * 18, 0, 0, 0);

            switch (block.Kind)
            {
                case BlockKind.Heading:
                    Children.Add(Text(block, text, accent, size: block.Level switch { 1 => 22, 2 => 18, 3 => 16, _ => 14 },
                        FontWeight.Bold, new Thickness(0, 6, 0, 0)));
                    break;
                case BlockKind.Bullet:
                    Children.Add(Row("•", block, text, muted, indent, accent));
                    break;
                case BlockKind.Numbered:
                    Children.Add(Row(block.Marker ?? $"{numberCounter}.", block, text, muted, indent, accent));
                    break;
                case BlockKind.Task:
                    Children.Add(Row(block.Checked ? "☑" : "☐", block, text, muted, indent, accent,
                        markerBrush: block.Checked ? Brush("GreenBrush", Brushes.LightGreen) : muted));
                    break;
                case BlockKind.Quote:
                    Children.Add(new Border
                    {
                        BorderBrush = accent, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2),
                        Child = Text(block, muted, accent, 13, FontWeight.Normal, default),
                    });
                    break;
                case BlockKind.Code:
                    Children.Add(new Border
                    {
                        Background = surface, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9),
                        Child = new SelectableTextBlock
                        {
                            Text = block.PlainText, FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
                            FontSize = 12, Foreground = text, TextWrapping = TextWrapping.NoWrap,
                        },
                    });
                    break;
                case BlockKind.Rule:
                    Children.Add(new Border { Height = 1, Background = Brush("LineBrush", Brushes.Gray), Margin = new Thickness(0, 6) });
                    break;
                default:
                    Children.Add(Text(block, text, accent, 13.5, FontWeight.Normal, default));
                    break;
            }
        }
    }

    private Control Row(
        string marker, MarkdownBlock block, IBrush text, IBrush muted, Thickness indent, IBrush accent, IBrush? markerBrush = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = indent };
        grid.Children.Add(new TextBlock
        {
            Text = marker, Foreground = markerBrush ?? muted, Margin = new Thickness(0, 0, 8, 0), FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Top,
        });
        var body = Text(block, text, accent, 13.5, FontWeight.Normal, default);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private SelectableTextBlock Text(
        MarkdownBlock block, IBrush foreground, IBrush linkBrush, double size, FontWeight weight, Thickness margin)
    {
        var tb = new SelectableTextBlock
        {
            FontSize = size, FontWeight = weight, Foreground = foreground, TextWrapping = TextWrapping.Wrap, Margin = margin,
        };
        foreach (var part in block.Inlines)
        {
            switch (part.Kind)
            {
                case InlineKind.Bold:
                    tb.Inlines!.Add(new Run(part.Text) { FontWeight = FontWeight.Bold });
                    break;
                case InlineKind.Italic:
                    tb.Inlines!.Add(new Run(part.Text) { FontStyle = FontStyle.Italic });
                    break;
                case InlineKind.Code:
                    tb.Inlines!.Add(new Run(part.Text)
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
                        Background = Brush("Surface2Brush", Brushes.DimGray),
                    });
                    break;
                case InlineKind.Link:
                    tb.Inlines!.Add(Link(part, linkBrush));
                    break;
                default:
                    tb.Inlines!.Add(new Run(part.Text));
                    break;
            }
        }

        return tb;
    }

    private static InlineUIContainer Link(MarkdownInline part, IBrush brush)
    {
        var url = part.Url;
        var link = new TextBlock
        {
            Text = part.Text, Foreground = brush, TextDecorations = TextDecorations.Underline,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        link.PointerPressed += (_, _) =>
        {
            if (url is not null) LinkOpener?.Open(url);
        };
        return new InlineUIContainer(link);
    }
}
