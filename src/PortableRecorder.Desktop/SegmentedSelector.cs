using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace PortableRecorder.Desktop;

/// <summary>A native SelectorBar above one persistent, independently animated selection surface.</summary>
public sealed class SegmentedSelector : UserControl
{
    private readonly SelectorBar _selector = new() { Padding = new Thickness(0) };
    private readonly Border _indicator;
    private readonly SolidColorBrush[] _inks;
    private readonly double _step;
    private readonly UISettings _settings = new();
    private Vector3KeyFrameAnimation? _slide;
    private readonly Dictionary<SolidColorBrush, Storyboard> _colorAnimations = new();

    public new int TabIndex { get => _selector.TabIndex; set => _selector.TabIndex = value; }
    public int SelectedIndex => _selector.Items.IndexOf(_selector.SelectedItem);
    public void FocusSelected() => _selector.Focus(FocusState.Programmatic);

    public SegmentedSelector(string[] labels, double itemWidth, double gap, Action<int> changed)
    {
        IsTabStop = false;
        Height = 28;
        Width = labels.Length * itemWidth + (labels.Length - 1) * gap;
        _step = itemWidth + gap;
        var root = new Grid();
        _indicator = new Border
        {
            Width = itemWidth, Height = 28, HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(3), Background = Design.Brush(Design.Selected),
            IsHitTestVisible = false
        };
        root.Children.Add(_indicator);
        root.Children.Add(_selector);
        Content = root;
        _inks = labels.Select(_ => Design.Brush(Design.Muted)).ToArray();
        for (var i = 0; i < labels.Length; i++)
        {
            var item = new SelectorBarItem
            {
                Text = labels[i], Width = itemWidth, Height = 28, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(0), Margin = new Thickness(0, 0, i == labels.Length - 1 ? 0 : gap, 0),
                Background = Design.Brush(Colors.Transparent), Foreground = _inks[i]
            };
            item.Template = (ControlTemplate)XamlReader.Load("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="SelectorBarItem">
                  <Grid Background="Transparent">
                    <TextBlock Text="{TemplateBinding Text}" Foreground="{TemplateBinding Foreground}"
                        FontFamily="Microsoft YaHei UI" FontSize="13" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                  </Grid>
                </ControlTemplate>
                """);
            _selector.Items.Add(item);
        }
        _selector.SelectionChanged += (_, _) =>
        {
            var index = SelectedIndex;
            if (index < 0) return;
            UpdateSelection(IsLoaded && _settings.AnimationsEnabled);
            changed(index);
        };
        Loaded += (_, _) => UpdateSelection(false);
        Unloaded += (_, _) =>
        {
            if (_slide != null) _indicator.StopAnimation(_slide);
            foreach (var animation in _colorAnimations.Values) animation.Stop();
            _colorAnimations.Clear();
        };
        _selector.SelectedItem = _selector.Items[0];
    }

    private void UpdateSelection(bool animate)
    {
        var target = new Vector3((float)(Math.Max(0, SelectedIndex) * _step), 0, 0);
        if (animate)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(_indicator).Compositor;
            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Target = "Translation";
            slide.Duration = TimeSpan.FromMilliseconds(180);
            slide.InsertExpressionKeyFrame(0, "this.StartingValue");
            slide.InsertKeyFrame(1, target, compositor.CreateCubicBezierEasingFunction(new(.22f, 1), new(.36f, 1)));
            _slide = slide;
            _indicator.StartAnimation(slide);
        }
        else
        {
            if (_slide != null) _indicator.StopAnimation(_slide);
            _indicator.Translation = target;
        }
        for (var i = 0; i < _inks.Length; i++)
        {
            var brush = _inks[i];
            var from = brush.Color;
            if (_colorAnimations.Remove(brush, out var previous)) previous.Stop();
            var to = i == SelectedIndex ? Design.White : Design.Muted;
            brush.Color = to;
            if (!animate || from == to) continue;
            var color = new ColorAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(80), FillBehavior = FillBehavior.Stop };
            var storyboard = new Storyboard();
            Storyboard.SetTarget(color, brush); Storyboard.SetTargetProperty(color, "Color");
            storyboard.Children.Add(color);
            _colorAnimations[brush] = storyboard;
            storyboard.Begin();
        }
    }
}
