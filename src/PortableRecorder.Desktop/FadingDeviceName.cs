using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace PortableRecorder.Desktop;

/// <summary>Native text with a clipped viewport and a conditional fade into the flat transport surface.</summary>
public sealed class FadingDeviceName : Grid
{
    private readonly TextBlock _text = Design.Text("", 13);
    private readonly Border _fade;
    private string _value = "";

    public FadingDeviceName()
    {
        Width = 132; Height = 44;
        var naturalWidth = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _text.TextWrapping = TextWrapping.NoWrap;
        _text.TextTrimming = TextTrimming.None;
        naturalWidth.Children.Add(_text);
        Children.Add(naturalWidth);
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        gradient.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0, Design.Transport.R, Design.Transport.G, Design.Transport.B) });
        gradient.GradientStops.Add(new GradientStop { Offset = 1, Color = Design.Transport });
        _fade = new Border { Width = 24, Background = gradient, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Children.Add(_fade);
        SizeChanged += (_, _) => UpdateOverflow();
        _text.SizeChanged += (_, _) => UpdateOverflow();
    }

    public void SetName(string value)
    {
        if (_value == value) return;
        _value = value;
        _text.Text = value;
        ToolTipService.SetToolTip(this, value);
        AutomationProperties.SetName(this, "输入设备：" + value);
    }

    private void UpdateOverflow()
    {
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };
        _fade.Visibility = _text.ActualWidth > ActualWidth + .5 ? Visibility.Visible : Visibility.Collapsed;
    }
}
