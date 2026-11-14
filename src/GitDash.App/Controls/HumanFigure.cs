using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GitDash.App.Controls;

/// <summary>How a <see cref="HumanFigure"/> stands: sitting at a desk (arms reaching forward) or standing (arms by the sides).</summary>
public enum FigurePose { Seated, Standing }

/// <summary>
/// The body of a person below the head: neck, shoulders, torso, arms and hands — and, standing, legs and shoes.
/// The head (the GitHub profile picture) is laid over the top by the view. Drawn in the person's colour.
/// </summary>
public sealed class HumanFigure : Control
{
    public static readonly StyledProperty<IBrush?> ShirtProperty =
        AvaloniaProperty.Register<HumanFigure, IBrush?>(nameof(Shirt));

    public static readonly StyledProperty<FigurePose> PoseProperty =
        AvaloniaProperty.Register<HumanFigure, FigurePose>(nameof(Pose));

    private static readonly IBrush Skin = new SolidColorBrush(Color.Parse("#E9BC9A"));
    private static readonly IBrush Trousers = new SolidColorBrush(Color.Parse("#2A3042"));
    private static readonly IBrush Shoes = new SolidColorBrush(Color.Parse("#12151F"));
    private static readonly IPen Seam = new Pen(new SolidColorBrush(Color.Parse("#33000000")), 1);

    static HumanFigure()
    {
        AffectsRender<HumanFigure>(ShirtProperty, PoseProperty);
    }

    public IBrush? Shirt { get => GetValue(ShirtProperty); set => SetValue(ShirtProperty, value); }
    public FigurePose Pose { get => GetValue(PoseProperty); set => SetValue(PoseProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var shirt = Shirt ?? Brushes.SlateGray;
        var cx = w / 2;

        if (Pose == FigurePose.Seated)
        {
            // Neck, then wide shoulders and torso; the arms bend forward to the keyboard behind the desk.
            Rounded(context, Skin, null, cx - 6, 0, 12, 18, 5);
            Rounded(context, shirt, Seam, cx - 29, 12, 58, h - 12, 18);
            Rounded(context, shirt, Seam, cx - 38, 22, 13, h - 24, 7);
            Rounded(context, shirt, Seam, cx + 25, 22, 13, h - 24, 7);
            context.DrawEllipse(Skin, null, new Point(cx - 31, h - 3), 6, 5);
            context.DrawEllipse(Skin, null, new Point(cx + 31, h - 3), 6, 5);
            // Collar
            Rounded(context, Skin, null, cx - 8, 11, 16, 7, 3);
            return;
        }

        // Standing: legs and shoes, torso, arms hanging, hands, neck.
        var legTop = h * 0.56;
        var legH = h - legTop - 5;
        Rounded(context, Trousers, null, cx - 15, legTop, 14, legH, 5);
        Rounded(context, Trousers, null, cx + 1, legTop, 14, legH, 5);
        Rounded(context, Shoes, null, cx - 18, h - 8, 18, 8, 4);
        Rounded(context, Shoes, null, cx, h - 8, 18, 8, 4);
        Rounded(context, Skin, null, cx - 5, 0, 10, 14, 4);
        Rounded(context, shirt, Seam, cx - 23, 9, 46, legTop - 5, 15);
        Rounded(context, shirt, Seam, cx - 31, 14, 10, legTop - 16, 5);
        Rounded(context, shirt, Seam, cx + 21, 14, 10, legTop - 16, 5);
        context.DrawEllipse(Skin, null, new Point(cx - 26, legTop - 0), 5, 5);
        context.DrawEllipse(Skin, null, new Point(cx + 26, legTop - 0), 5, 5);
    }

    private static void Rounded(DrawingContext context, IBrush brush, IPen? pen, double x, double y, double width, double height, double radius) =>
        context.DrawRectangle(brush, pen, new Rect(x, y, Math.Max(0, width), Math.Max(0, height)), radius, radius);
}
