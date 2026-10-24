using Microsoft.UI.Xaml;
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
            From = 1, To = .45, Duration = TimeSpan.FromMilliseconds(1500),
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
