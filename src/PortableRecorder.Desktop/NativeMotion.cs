using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace PortableRecorder.Desktop;

/// <summary>Interruptible opacity animations run by the native XAML animation engine.</summary>
internal sealed class NativeFade(FrameworkElement target, Func<bool> animationsEnabled)
{
    private Storyboard? _storyboard;
    private int _generation;

    public void To(double opacity, int milliseconds, Action? completed = null)
    {
        var from = target.Opacity;
        Stop();
        var generation = _generation;
        target.Opacity = opacity;
        if (!animationsEnabled() || milliseconds == 0)
        {
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimation
        {
            From = from, To = opacity, Duration = TimeSpan.FromMilliseconds(milliseconds),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (generation != _generation) return;
            _storyboard = null;
            completed?.Invoke();
        };
        _storyboard = storyboard;
        storyboard.Begin();
    }

    public void In(int milliseconds = 140)
    {
        Stop();
        target.Opacity = 0;
        To(1, milliseconds);
    }

    public void Breathe()
    {
        Stop();
        target.Opacity = 1;
        if (!animationsEnabled()) return;
        var animation = new DoubleAnimation
        {
            From = 1, To = .45, Duration = TimeSpan.FromMilliseconds(1000),
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        _storyboard = storyboard;
        storyboard.Begin();
    }

    public void Stop()
    {
        _generation++;
        _storyboard?.Stop();
        _storyboard = null;
    }
}

internal sealed class NativeTween(
    Func<bool> animationsEnabled,
    Action<double> apply,
    int milliseconds = 420,
    double x1 = .4,
    double y1 = 0,
    double x2 = .2,
    double y2 = 1,
    Action? completed = null)
{
    private readonly Stopwatch _clock = new();
    private double _from, _to, _current;
    private bool _running;

    public double Current => _current;

    public void Jump(double value)
    {
        Stop();
        _from = _to = _current = value;
        apply(value);
    }

    public void To(double target)
    {
        if (!animationsEnabled() || milliseconds <= 0 || Math.Abs(_current - target) < .05)
        {
            Jump(target);
            completed?.Invoke();
            return;
        }
        if (_running && Math.Abs(_to - target) < .05) return;
        _from = _current;
        _to = target;
        _clock.Restart();
        if (!_running)
        {
            _running = true;
            CompositionTarget.Rendering += Tick;
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _clock.Stop();
        CompositionTarget.Rendering -= Tick;
    }

    private void Tick(object? sender, object e)
    {
        double raw = Math.Clamp(_clock.Elapsed.TotalMilliseconds / milliseconds, 0, 1);
        double progress = SampleBezier(raw);
        _current = _from + (_to - _from) * progress;
        apply(_current);
        if (raw < 1) return;
        _current = _to;
        apply(_to);
        Stop();
        completed?.Invoke();
    }

    private double SampleBezier(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double t = x;
        for (int i = 0; i < 6; i++)
        {
            double u = 1 - t;
            double bx = 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t;
            double dx = 3 * u * u * x1 + 6 * u * t * (x2 - x1) + 3 * t * t * (1 - x2);
            if (Math.Abs(dx) < 1e-6) break;
            t = Math.Clamp(t - (bx - x) / dx, 0, 1);
        }
        double r = 1 - t;
        return 3 * r * r * t * y1 + 3 * r * t * t * y2 + t * t * t;
    }
}
