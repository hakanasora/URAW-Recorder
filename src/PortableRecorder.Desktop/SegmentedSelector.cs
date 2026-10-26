using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace PortableRecorder.Desktop;

/// <summary>Fixed native radio-button segments: label, hit target and focus share one element.</summary>
public sealed class SegmentedSelector : UserControl
{
    private readonly RadioButton[] _buttons;
    private readonly string[] _labels;
    private readonly Action<int> _changed;
    private readonly Border _indicator;
    private readonly SolidColorBrush[] _inks;
    private readonly double _step;
    private readonly UISettings _settings = new();
    private Vector3KeyFrameAnimation? _slide;
    private readonly Dictionary<SolidColorBrush, Storyboard> _colorAnimations = new();
    private int _selectedIndex = -1, _tabIndex;

    public new int TabIndex
    {
        get => _tabIndex;
        set { _tabIndex = value; UpdateTabStops(); }
    }
    public int SelectedIndex => _selectedIndex;
    public void FocusSelected()
    {
        if (_selectedIndex >= 0) _buttons[_selectedIndex].Focus(FocusState.Programmatic);
        LogLayout();
    }

    public SegmentedSelector(string[] labels, double itemWidth, double gap, Action<int> changed)
    {
        if (labels.Length == 0) throw new ArgumentException("At least one segment is required.",nameof(labels));
        _labels = labels; _changed = changed; _buttons = new RadioButton[labels.Length];
        IsTabStop = false;
        Height = 28;
        Width = labels.Length * itemWidth + (labels.Length - 1) * gap;
        _step = itemWidth + gap;
        var root = new Grid();
        _indicator = new Border
        {
            Width = itemWidth, Height = 28, HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(3), Background = Design.Brush(MaterialSurface.Tint(Colors.White,36)),
            IsHitTestVisible = false
        };
        root.Children.Add(_indicator);
        var targets = new Grid(); root.Children.Add(targets); Content = root;
        _inks = labels.Select(_ => Design.Brush(Design.Muted)).ToArray();
        var group = "RecorderSegments_" + Guid.NewGuid().ToString("N");
        var template = (ControlTemplate)XamlReader.Load("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="RadioButton">
              <Grid Background="{TemplateBinding Background}">
                <ContentPresenter Content="{TemplateBinding Content}"
                    HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Grid>
            </ControlTemplate>
            """);
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            targets.ColumnDefinitions.Add(new() { Width = new GridLength(itemWidth + (i == labels.Length - 1 ? 0 : gap)) });
            var label = new TextBlock { Text = labels[i], Foreground = _inks[i],
                FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 13,
                IsHitTestVisible = false };
            // No item presenter, repeater or duplicate label overlay: this exact rectangle
            // renders the label AND receives native mouse/touchpad/touch activation.
            var button = new RadioButton
            {
                Content = label, GroupName = group, Template = template,
                Width = itemWidth, Height = 28, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(0), Margin = new Thickness(0), BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Background = Design.Brush(Colors.Transparent), UseSystemFocusVisuals = true,
                IsTabStop = false
            };
            AutomationProperties.SetName(button,labels[i]);
            button.Checked += (_, _) => Select(index);
            button.PreviewKeyDown += (_, args) => Navigate(index,args);
            button.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_, args) => {
                var point = args.GetCurrentPoint(button);
                StartupLog.Write($"Segment press: {_labels[index]}; local={point.Position.X:F1},{point.Position.Y:F1}; size={button.ActualWidth:F1}x{button.ActualHeight:F1}; pointer={args.Pointer.PointerDeviceType}");
            }),true);
            button.Click += (_, _) => StartupLog.Write($"Segment click: {_labels[index]}; selected={SelectedIndex}");
            _buttons[i] = button; Grid.SetColumn(button,i); targets.Children.Add(button);
        }
        Loaded += (_, _) => { UpdateSelection(false); LogLayout(); };
        Unloaded += (_, _) =>
        {
            if (_slide != null) _indicator.StopAnimation(_slide);
            foreach (var animation in _colorAnimations.Values) animation.Stop();
            _colorAnimations.Clear();
        };
        _buttons[0].IsChecked = true;
    }

    private void Select(int index)
    {
        if (_selectedIndex == index) return;
        _selectedIndex = index; UpdateTabStops();
        UpdateSelection(IsLoaded && _settings.AnimationsEnabled);
        StartupLog.Write($"Segment selected: {_labels[index]}");
        _changed(index);
    }

    private void UpdateTabStops()
    {
        for (var i = 0; i < _buttons.Length; i++)
        {
            var button = _buttons[i];
            if (button == null) continue;
            button.IsTabStop = i == _selectedIndex; button.TabIndex = _tabIndex;
        }
    }

    private void Navigate(int index, KeyRoutedEventArgs args)
    {
        var next = args.Key switch
        {
            VirtualKey.Left or VirtualKey.Up => (index + _buttons.Length - 1) % _buttons.Length,
            VirtualKey.Right or VirtualKey.Down => (index + 1) % _buttons.Length,
            VirtualKey.Home => 0,
            VirtualKey.End => _buttons.Length - 1,
            VirtualKey.Enter => index,
            _ => -1
        };
        if (next < 0) return;
        args.Handled = true;
        _buttons[next].IsChecked = true; _buttons[next].Focus(FocusState.Keyboard);
    }

    private void LogLayout()
    {
        if (!IsLoaded || ActualWidth < 1) return;
        for (var i = 0; i < _buttons.Length; i++)
        {
            var button = _buttons[i];
            var origin = button.TransformToVisual(this).TransformPoint(new Point(0,0));
            StartupLog.Write($"Segment bounds: {_labels[i]}; local={origin.X:F1},{origin.Y:F1}; size={button.ActualWidth:F1}x{button.ActualHeight:F1}");
        }
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
