using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GitDash.App.Controls;

/// <summary>
/// Lays its children out as a cover flow: the item at <see cref="Position"/> faces the viewer,
/// neighbours are tilted, scaled down and fanned out to the sides.
/// <see cref="Position"/> is a double so a transition can glide between cards; work is only done
/// while that transition runs.
/// </summary>
public sealed class CoverFlowPanel : Panel
{
    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<CoverFlowPanel, double>(nameof(Position));

    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<CoverFlowPanel, double>(nameof(ItemWidth), 380);

    private const double FirstOffset = 0.74;   // distance of the first neighbour, in item widths
    private const double NextOffset = 0.30;    // distance between further neighbours
    private const double MaxAngle = 42;
    private const double PerspectiveDepth = 900;
    private const double VisibleRange = 4;

    static CoverFlowPanel()
    {
        AffectsMeasure<CoverFlowPanel>(ItemWidthProperty);
        AffectsArrange<CoverFlowPanel>(PositionProperty);
    }

    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0d;
        foreach (var child in Children)
        {
            child.Measure(new Size(ItemWidth, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var width = double.IsInfinity(availableSize.Width) ? ItemWidth * 3 : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var position = Position;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var height = Math.Min(child.DesiredSize.Height, finalSize.Height);
            child.Arrange(new Rect(
                (finalSize.Width - ItemWidth) / 2, (finalSize.Height - height) / 2, ItemWidth, height));

            var delta = i - position;
            var distance = Math.Abs(delta);
            var side = Math.Sign(delta);
            var near = Math.Min(distance, 1);          // 0 at the centre, 1 from the first neighbour on
            var far = Math.Max(distance - 1, 0);

            // Cards far off to the side are fully transparent, which the renderer skips entirely.
            var visible = distance < VisibleRange;
            child.IsHitTestVisible = visible;
            if (!visible)
            {
                child.Opacity = 0;
                continue;
            }

            var x = side * (near * FirstOffset + far * NextOffset) * ItemWidth;
            var scale = Math.Max(0.6, 1 - 0.2 * near - 0.05 * far);
            var matrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, 0);
            if (near > 0.001)
            {
                var tilt = new Rotate3DTransform { AngleY = -side * MaxAngle * near, Depth = PerspectiveDepth };
                matrix = tilt.Value * matrix;
            }

            child.RenderTransform = new MatrixTransform(matrix);
            child.ZIndex = 1000 - (int)Math.Round(distance * 10);
            child.Opacity = Math.Clamp(1 - 0.22 * far - 0.25 * near, 0.15, 1);
        }

        return finalSize;
    }
}
