using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace GitDash.App.Controls;

/// <summary>
/// A glass-like finish laid over a rounded surface: a soft gloss across the top, a glowing rim light and a slow
/// sheen that sweeps across. It never takes the mouse. The window decides whether it is shown (class "glass",
/// see <c>Theme.axaml</c>); the sweep only runs while the window is allowed to animate (class "motion").
/// Set <see cref="Border.CornerRadius"/> to the corner radius of the surface it covers.
/// </summary>
public sealed class GlassGlaze : Border
{
    public GlassGlaze()
    {
        Classes.Add("glaze");
        IsHitTestVisible = false;
        ClipToBounds = true;
        BorderThickness = new Thickness(1);
        BorderBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.Parse("#A0FFFFFF"), 0),
                new GradientStop(Color.Parse("#14FFFFFF"), 0.5),
                new GradientStop(Color.Parse("#66FFFFFF"), 1),
            ],
        };
        BoxShadow = BoxShadows.Parse("inset 0 0 14 0 #33FFFFFF");

        var gloss = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(Color.Parse("#40FFFFFF"), 0),
                    new GradientStop(Color.Parse("#0DFFFFFF"), 0.5),
                    new GradientStop(Color.Parse("#00FFFFFF"), 1),
                ],
            },
        };

        var sheen = new Border
        {
            Classes = { "glazeSheen" },
            Width = 44,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            RenderTransform = new TranslateTransform(-80, 0),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.3, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(Color.Parse("#00FFFFFF"), 0),
                    new GradientStop(Color.Parse("#38FFFFFF"), 0.5),
                    new GradientStop(Color.Parse("#00FFFFFF"), 1),
                ],
            },
        };

        Child = new Panel { Children = { gloss, sheen } };
    }
}
