using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitDash.App.Controls;

/// <summary>
/// Attached behaviour that makes a card lean towards the mouse in 3D, with a moving glare, like the achievement
/// cards on Steam. Turn it on with <c>c:Tilt.Enabled="{Binding IsFront}"</c>; a child named "Shine" (optional) is used as
/// the glare layer. The card eases towards its target a frame at a time and the timer only runs while something moves.
/// </summary>
public static class Tilt
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, bool>("Enabled");

    public static bool GetEnabled(Control control) => control.GetValue(EnabledProperty);

    public static void SetEnabled(Control control, bool value) => control.SetValue(EnabledProperty, value);

    private const double MaxAngle = 13;    // degrees at the very edge of the card
    private const double Easing = 0.2;     // share of the remaining distance covered per frame
    private const double MaxGlare = 0.5;
    private static readonly ConditionalWeakTable<Control, State> States = new();

    static Tilt()
    {
        EnabledProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true) Attach(control);
            else Detach(control);
        });
    }

    private sealed class State
    {
        public required Control Card;
        public readonly Rotate3DTransform Transform = new() { Depth = 900 };
        public readonly RadialGradientBrush Glare = new()
        {
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(0x70, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 1),
            ],
        };
        public Border? Shine;
        public DispatcherTimer? Timer;
        public double TargetX, TargetY, TargetGlare;
        public double X, Y, GlareOpacity;
    }

    private static void Attach(Control card)
    {
        if (States.TryGetValue(card, out _)) return;
        var state = new State { Card = card };
        States.Add(card, state);
        card.RenderTransformOrigin = RelativePoint.Center;
        card.RenderTransform = state.Transform;
        card.PointerMoved += OnMoved;
        card.PointerExited += OnExited;
        card.DetachedFromVisualTree += OnDetached;
    }

    private static void Detach(Control card)
    {
        if (!States.TryGetValue(card, out var state)) return;
        card.PointerMoved -= OnMoved;
        card.PointerExited -= OnExited;
        card.DetachedFromVisualTree -= OnDetached;
        state.Timer?.Stop();
        state.Timer = null;
        States.Remove(card);
        card.RenderTransform = null;
        if (state.Shine is not null) state.Shine.Opacity = 0;
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control card && States.TryGetValue(card, out var state)) state.Timer?.Stop();
    }

    private static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control card || !States.TryGetValue(card, out var state)) return;
        var size = card.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        var p = e.GetPosition(card);
        double nx = Math.Clamp(p.X / size.Width, 0, 1), ny = Math.Clamp(p.Y / size.Height, 0, 1);
        // The edge under the cursor dips away from the viewer (pressed in), the opposite edge rises.
        state.TargetY = (nx - 0.5) * 2 * MaxAngle;
        state.TargetX = -(ny - 0.5) * 2 * MaxAngle;
        state.TargetGlare = MaxGlare;

        state.Shine ??= card.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "Shine");
        if (state.Shine is not null)
        {
            state.Glare.Center = state.Glare.GradientOrigin = new RelativePoint(nx, ny, RelativeUnit.Relative);
            state.Shine.Background = state.Glare;
        }

        Run(state);
    }

    private static void OnExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Control card || !States.TryGetValue(card, out var state)) return;
        state.TargetX = state.TargetY = state.TargetGlare = 0;
        Run(state);
    }

    private static void Run(State state)
    {
        if (state.Timer is not null) return;
        state.Timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Step(state));
        state.Timer.Start();
    }

    private static void Step(State state)
    {
        state.X += (state.TargetX - state.X) * Easing;
        state.Y += (state.TargetY - state.Y) * Easing;
        state.GlareOpacity += (state.TargetGlare - state.GlareOpacity) * Easing;
        state.Transform.AngleX = state.X;
        state.Transform.AngleY = state.Y;
        if (state.Shine is not null) state.Shine.Opacity = state.GlareOpacity;

        var settled = Math.Abs(state.TargetX - state.X) < 0.02 && Math.Abs(state.TargetY - state.Y) < 0.02 &&
                      Math.Abs(state.TargetGlare - state.GlareOpacity) < 0.01;
        if (!settled) return;

        state.Transform.AngleX = state.TargetX;
        state.Transform.AngleY = state.TargetY;
        if (state.Shine is not null) state.Shine.Opacity = state.TargetGlare;
        state.X = state.TargetX;
        state.Y = state.TargetY;
        state.GlareOpacity = state.TargetGlare;
        if (state.TargetX != 0 || state.TargetY != 0) return; // resting on the cursor: wait for the next movement

        state.Timer?.Stop();
        state.Timer = null;
    }
}
