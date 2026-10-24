using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;

namespace PortableRecorder.Desktop;

/// <summary>Compact microphone label, with a conditional fade only on overflowing device names.</summary>
public sealed class FadingDeviceName : Grid
{
    private readonly TextBlock _text = Design.Text("", 13);
    private readonly Grid _viewport = new();
    private readonly LinearGradientBrush _fadeInk;
    private readonly SolidColorBrush _solidInk=Design.Brush(Design.White);
    private string _value = "";
    private bool _recording;
    private double _expandedWidth = 146;
    private Storyboard? _reveal;
    private const double MaxTextWidth = 122;
    // Idle name-to-knob gap stays 16; the fully wiped state leaves 12 DIP after the 16-DIP icon.
    public double GainStartOffset => ActualWidth + 16 - 12 * Math.Clamp(
        (_expandedWidth-ActualWidth)/Math.Max(1,_expandedWidth-24),0,1);

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
        // Fade the glyph fill itself: never paint a background-colored patch over Mica.
        _fadeInk=new LinearGradientBrush{StartPoint=new Point(0,.5),EndPoint=new Point(1,.5)};
        var transparent=Color.FromArgb(0,Design.White.R,Design.White.G,Design.White.B);
        _fadeInk.GradientStops.Add(new GradientStop{Offset=0,Color=Design.White});
        _fadeInk.GradientStops.Add(new GradientStop{Offset=1,Color=Design.White});
        _fadeInk.GradientStops.Add(new GradientStop{Offset=1,Color=transparent});
        _fadeInk.GradientStops.Add(new GradientStop{Offset=1,Color=transparent});
        _viewport.SizeChanged += (_, _) => UpdateOverflow();
        _text.SizeChanged += (_, _) => UpdateOverflow();
        Unloaded += (_, _) => { _reveal?.Stop(); _reveal=null; };
    }

    public void SetName(string value)
    {
        if (_value == value) return;
        _value = value; _text.Text = value;
        ToolTipService.SetToolTip(this, value);
        AutomationProperties.SetName(this, "输入设备：" + value);
    }

    public void SetRecording(bool recording, bool animate)
    {
        if(recording==_recording && animate)return;
        _recording=recording;
        var from=Width;
        _reveal?.Stop();_reveal=null;
        var target=recording?24:_expandedWidth;
        Width=target;
        if(!animate || Math.Abs(from-target)<.1){UpdateOverflow();return;}
        // The width boundary wipes the text while the adjacent knob follows it left.
        var motion=new DoubleAnimationUsingKeyFrames{EnableDependentAnimation=true,FillBehavior=FillBehavior.Stop};
        motion.KeyFrames.Add(new DiscreteDoubleKeyFrame{KeyTime=TimeSpan.Zero,Value=from});
        motion.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime=TimeSpan.FromMilliseconds(420),Value=target,
            KeySpline=new KeySpline{ControlPoint1=new Point(.4,0),ControlPoint2=new Point(.2,1)}
        });
        var storyboard=new Storyboard();
        Storyboard.SetTarget(motion,this);Storyboard.SetTargetProperty(motion,"Width");
        storyboard.Children.Add(motion);_reveal=storyboard;
        storyboard.Completed+=(_,_)=>{if(_reveal==storyboard){_reveal=null;UpdateOverflow();}};
        storyboard.Begin();
    }

    private void UpdateOverflow()
    {
        // Natural name width is retained even while its viewport is wiped closed.
        _expandedWidth = 24 + Math.Min(MaxTextWidth, _text.ActualWidth);
        if(!_recording && _reveal==null && Math.Abs(Width-_expandedWidth)>.1)Width=_expandedWidth;
        _viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _viewport.ActualWidth, _viewport.ActualHeight) };
        if(_text.ActualWidth>_viewport.ActualWidth+.5 && _text.ActualWidth>0)
        {
            var edge=Math.Clamp(_viewport.ActualWidth/_text.ActualWidth,0,1);
            var start=Math.Clamp((_viewport.ActualWidth-24)/_text.ActualWidth,0,edge);
            _fadeInk.GradientStops[1].Offset=start;
            _fadeInk.GradientStops[2].Offset=edge;
            _text.Foreground=_fadeInk;
        }
        else _text.Foreground=_solidInk;
    }
}
