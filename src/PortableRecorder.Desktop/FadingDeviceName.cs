using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace PortableRecorder.Desktop;

/// <summary>Compact microphone label, with a conditional fade only on overflowing device names.</summary>
public sealed class FadingDeviceName : Grid
{
    private readonly TextBlock _text = Design.Text("", 13);
    private readonly Grid _viewport = new();
    private readonly Border _fade;
    private string _value = "";
    private const double MaxTextWidth = 122;

    public FadingDeviceName()
    {
        Width = 146; Height = 44;
        ColumnDefinitions.Add(new() { Width = new GridLength(24) });
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var microphone = Design.Icon("\uE720",16,Design.White);
        microphone.HorizontalAlignment = HorizontalAlignment.Left;
        microphone.IsHitTestVisible = false;
        Children.Add(microphone);
        Grid.SetColumn(_viewport,1); Children.Add(_viewport);
        var naturalWidth = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _text.TextWrapping = TextWrapping.NoWrap;
        _text.TextTrimming = TextTrimming.None;
        naturalWidth.Children.Add(_text); _viewport.Children.Add(naturalWidth);
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        gradient.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0, Design.Transport.R, Design.Transport.G, Design.Transport.B) });
        gradient.GradientStops.Add(new GradientStop { Offset = 1, Color = Design.Transport });
        _fade = new Border { Width = 24, Background = gradient, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _viewport.Children.Add(_fade);
        _viewport.SizeChanged += (_, _) => UpdateOverflow();
        _text.SizeChanged += (_, _) => UpdateOverflow();
    }

    public void SetName(string value)
    {
        if (_value == value) return;
        _value = value; _text.Text = value;
        ToolTipService.SetToolTip(this, value);
        AutomationProperties.SetName(this, "输入设备：" + value);
    }

    private void UpdateOverflow()
    {
        // Short names shrink the group, keeping the following knob 16 DIP away.
        Width = 24 + Math.Min(MaxTextWidth, _text.ActualWidth);
        _viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _viewport.ActualWidth, _viewport.ActualHeight) };
        _fade.Visibility = _text.ActualWidth > _viewport.ActualWidth + .5 ? Visibility.Visible : Visibility.Collapsed;
    }
}
