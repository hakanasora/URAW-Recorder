using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace PortableRecorder.Desktop;

internal sealed class FadingText : Grid
{
    private readonly TextBlock _text;
    private readonly CanvasControl _canvas = new() { IsHitTestVisible = false };
    private readonly CanvasTextFormat _format;
    private readonly RectangleGeometry _clip = new();
    private readonly Color _color;
    private bool _released;

    public FadingText(float fontSize, Color color)
    {
        _color = color;
        _text = Design.Text("", fontSize, color);
        _text.TextWrapping = TextWrapping.NoWrap;
        _text.Opacity = 0;
        _format = new()
        {
            FontFamily = "Segoe UI Variable",
            FontSize = fontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap
        };

        var naturalWidth = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        naturalWidth.Children.Add(_text);
        Children.Add(naturalWidth);
        Children.Add(_canvas);
        Clip = _clip;
        _canvas.Draw += DrawLabel;
        SizeChanged += (_, _) =>
        {
            if (_released) return;
            _clip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
            _canvas.Invalidate();
        };
        _text.SizeChanged += (_, _) =>
        {
            if (_released) return;
            NaturalWidthChanged?.Invoke();
            _canvas.Invalidate();
        };
    }

    public double NaturalWidth => _text.ActualWidth;
    public event Action? NaturalWidthChanged;

    public string Text
    {
        get => _text.Text;
        set
        {
            if (_released || _text.Text == value) return;
            _text.Text = value;
            _canvas.Invalidate();
        }
    }

    private void DrawLabel(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_released || Text.Length == 0) return;
        float width = (float)sender.ActualWidth, height = (float)sender.ActualHeight;
        if (width < .5f || height < 1) return;
        var ds = args.DrawingSession;
        ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        // Mask the whole line so the fade stays continuous across font-fallback runs.
        using var line = new CanvasCommandList(sender);
        using (var ink = line.CreateDrawingSession())
            ink.DrawText(Text, new Rect(0, 0, Math.Max(NaturalWidth, width) + 4, height), _color, _format);
        if (NaturalWidth <= width + .5)
        {
            ds.DrawImage(line);
            return;
        }
        using var mask = new CanvasCommandList(sender);
        using (var ink = mask.CreateDrawingSession())
        using (var gradient = new CanvasLinearGradientBrush(sender, [
            new CanvasGradientStop { Position = 0, Color = Design.White },
            new CanvasGradientStop { Position = Math.Max(0, (width - 24) / width), Color = Design.White },
            new CanvasGradientStop { Position = 1, Color = Color.FromArgb(0, 255, 255, 255) }])
            { StartPoint = new(0, 0), EndPoint = new(width, 0) })
            ink.FillRectangle(0, 0, width, height, gradient);
        using var faded = new AlphaMaskEffect { Source = line, AlphaMask = mask };
        ds.DrawImage(faded);
    }

    public void ReleaseResources()
    {
        if (_released) return;
        _released = true;
        _canvas.RemoveFromVisualTree();
        _format.Dispose();
    }
}
