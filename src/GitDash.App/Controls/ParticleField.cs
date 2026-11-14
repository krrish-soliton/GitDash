using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitDash.App.Controls;

/// <summary>
/// A quiet, slowly drifting field of glowing dots joined by faint lines when they come close, drawn behind a page.
/// It only runs while it is on screen and the window's motion is on ("Reduce motion" in the settings stops it).
/// </summary>
public sealed class ParticleField : Control
{
    private const int Count = 48;
    private const double LinkDistance = 0.16; // as a fraction of the diagonal-ish size
    private static readonly Color[] Palette =
    [
        Color.Parse("#6366F1"), Color.Parse("#22D3EE"), Color.Parse("#F472B6"), Color.Parse("#A78BFA"),
    ];

    private struct Particle
    {
        public double X, Y, Vx, Vy, Radius, Phase;
        public int Tint;
    }

    private readonly Particle[] _particles = new Particle[Count];
    private DispatcherTimer? _timer;
    private Window? _window;
    private bool _motion = true;
    private long _lastTick;
    private double _time;

    public ParticleField()
    {
        IsHitTestVisible = false;
        var rng = new Random(11);
        for (var i = 0; i < Count; i++)
        {
            _particles[i] = new Particle
            {
                X = rng.NextDouble(),
                Y = rng.NextDouble(),
                // Normalised units per second: a slow drift, mostly upwards.
                Vx = (rng.NextDouble() - 0.5) * 0.012,
                Vy = -(0.004 + rng.NextDouble() * 0.014),
                Radius = 1.4 + rng.NextDouble() * 2.6,
                Phase = rng.NextDouble() * Math.PI * 2,
                Tint = rng.Next(Palette.Length),
            };
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = this.GetVisualRoot() as Window;
        if (_window is not null)
        {
            _window.Classes.CollectionChanged += OnWindowClassesChanged;
            _motion = _window.Classes.Contains("motion");
        }

        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null) _window.Classes.CollectionChanged -= OnWindowClassesChanged;
        _window = null;
        StopTimer();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowClassesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _motion = _window?.Classes.Contains("motion") ?? false;
        UpdateTimer();
        InvalidateVisual();
    }

    private void UpdateTimer()
    {
        if (_motion && _window is not null)
        {
            if (_timer is not null) return;
            _lastTick = Environment.TickCount64;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => Step());
            _timer.Start();
        }
        else
        {
            StopTimer();
        }
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void Step()
    {
        var now = Environment.TickCount64;
        if (!IsEffectivelyVisible)
        {
            _lastTick = now; // hidden tab: idle without animating or redrawing
            return;
        }

        var dt = Math.Min(0.25, (now - _lastTick) / 1000d);
        _lastTick = now;
        _time += dt;
        for (var i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            // Leave one side, come back on the other.
            if (p.X < -0.02) p.X = 1.02; else if (p.X > 1.02) p.X = -0.02;
            if (p.Y < -0.02) p.Y = 1.02; else if (p.Y > 1.02) p.Y = -0.02;
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!_motion || Bounds.Width < 10 || Bounds.Height < 10) return;

        double w = Bounds.Width, h = Bounds.Height;
        var reach = LinkDistance * Math.Max(w, h);

        for (var i = 0; i < _particles.Length; i++)
        {
            for (var j = i + 1; j < _particles.Length; j++)
            {
                double dx = (_particles[i].X - _particles[j].X) * w, dy = (_particles[i].Y - _particles[j].Y) * h;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance >= reach) continue;
                var alpha = (byte)(46 * (1 - distance / reach));
                var color = Palette[_particles[i].Tint];
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), 1);
                context.DrawLine(pen, new Point(_particles[i].X * w, _particles[i].Y * h),
                    new Point(_particles[j].X * w, _particles[j].Y * h));
            }
        }

        foreach (var p in _particles)
        {
            var twinkle = 0.55 + 0.45 * Math.Sin(_time * 1.3 + p.Phase);
            var color = Palette[p.Tint];
            var centre = new Point(p.X * w, p.Y * h);
            context.DrawEllipse(
                new SolidColorBrush(Color.FromArgb((byte)(34 * twinkle), color.R, color.G, color.B)), null, centre, p.Radius * 3.2, p.Radius * 3.2);
            context.DrawEllipse(
                new SolidColorBrush(Color.FromArgb((byte)(120 * twinkle), color.R, color.G, color.B)), null, centre, p.Radius, p.Radius);
        }
    }
}
