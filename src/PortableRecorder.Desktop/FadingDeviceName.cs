using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
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
    private readonly FontIcon _microphone=Design.Icon("\uE720",16,Design.White);
    private readonly Image _applicationIcon=new(){Width=16,Height=16,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed,IsHitTestVisible=false};
    private bool _listening;
    private int _channels=-1;
    private readonly TextBlock _text = Design.Text("", 13);
    private readonly Grid _viewport = new();
    private readonly CanvasControl _labelCanvas=new(){IsHitTestVisible=false};
    private readonly CanvasTextFormat _labelFormat=new(){FontFamily="Segoe UI Variable",FontSize=13,
        HorizontalAlignment=CanvasHorizontalAlignment.Left,VerticalAlignment=CanvasVerticalAlignment.Center,
        WordWrapping=CanvasWordWrapping.NoWrap};
    private string _value = "";
    private bool _recording,_released;
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
        var microphone = _microphone;
        microphone.HorizontalAlignment = HorizontalAlignment.Left;
        microphone.IsHitTestVisible = false;
        Children.Add(microphone);
        Children.Add(_applicationIcon);
        Grid.SetColumn(_viewport,1); Children.Add(_viewport);
        var naturalWidth = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _text.TextWrapping = TextWrapping.NoWrap;
        _text.TextTrimming = TextTrimming.None;
        naturalWidth.Children.Add(_text); _viewport.Children.Add(naturalWidth);
        // Keep native measurement/accessibility, render the entire mixed-font line through one alpha mask.
        // A XAML Foreground gradient restarts across font-fallback runs (Chinese / Latin), causing holes.
        _text.Opacity=0;_viewport.Children.Add(_labelCanvas);
        _labelCanvas.Draw+=DrawLabel;
        _viewport.SizeChanged += (_, _) => UpdateOverflow();
        _text.SizeChanged += (_, _) => UpdateOverflow();
        Unloaded += (_, _) => { _reveal?.Stop(); _reveal=null; };
    }

    public void SetName(string value)
    {
        if (_value == value) return;
        _value = value; _text.Text = value;_channels=-1;_labelCanvas.Invalidate();
        ToolTipService.SetToolTip(this, value);
        AutomationProperties.SetName(this, "输入设备：" + value);
    }

    public void SetChannels(int channels)
    {
        if(_listening)return;
        if(_channels==channels)return;_channels=channels;
        // Two adjacent microphone glyphs identify stereo/multichannel without falsifying mono files.
        _microphone.Glyph=channels>1?"\uE720\uE720":"\uE720";
        _microphone.FontSize=channels>1?11:16;
        var detail=_value+(channels>0?$" · 实际 {channels} 声道" : " · 尚未取得输入");
        ToolTipService.SetToolTip(this,detail);AutomationProperties.SetName(this,"输入设备："+detail);
    }
    public void UseMicrophone()
    {
        _listening=false;_channels=-1;_applicationIcon.Source=null;_applicationIcon.Visibility=Visibility.Collapsed;
        _microphone.Visibility=Visibility.Visible;_microphone.Glyph="\uE720";_microphone.FontSize=16;
    }
    public void SetListeningSource(string name,bool application)
    {
        _listening=true;SetName(name);
        _applicationIcon.Source=null;_applicationIcon.Visibility=Visibility.Collapsed;_microphone.Visibility=Visibility.Visible;
        _microphone.Glyph=application?"\uE7C4":"\uE977";_microphone.FontSize=16;
        AutomationProperties.SetName(this,"侦听来源："+name);
    }
    public void SetApplicationIcon(ImageSource icon)
    {
        if(!_listening)return;
        _applicationIcon.Source=icon;_applicationIcon.Visibility=Visibility.Visible;_microphone.Visibility=Visibility.Collapsed;
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
        _labelCanvas.Invalidate();
    }
    private void DrawLabel(CanvasControl sender,CanvasDrawEventArgs args)
    {
        if(_released)return;
        float width=(float)sender.ActualWidth,height=(float)sender.ActualHeight;
        if(width<.5f || height<1)return;
        var ds=args.DrawingSession;ds.TextAntialiasing=CanvasTextAntialiasing.Grayscale;
        using var line=new CanvasCommandList(sender);
        using(var ink=line.CreateDrawingSession())ink.DrawText(_value,new Rect(0,0,Math.Max(_text.ActualWidth,width)+4,height),Design.White,_labelFormat);
        if(_text.ActualWidth<=width+.5){ds.DrawImage(line);return;}
        using var mask=new CanvasCommandList(sender);
        using(var ink=mask.CreateDrawingSession())
        using(var gradient=new CanvasLinearGradientBrush(sender,[
            new CanvasGradientStop{Position=0,Color=Design.White},
            new CanvasGradientStop{Position=Math.Max(0,(width-24)/width),Color=Design.White},
            new CanvasGradientStop{Position=1,Color=Color.FromArgb(0,255,255,255)}])
            {StartPoint=new(0,0),EndPoint=new(width,0)})ink.FillRectangle(0,0,width,height,gradient);
        using var faded=new AlphaMaskEffect{Source=line,AlphaMask=mask};ds.DrawImage(faded);
    }
    public void ReleaseResources(){_released=true;_reveal?.Stop();_labelCanvas.RemoveFromVisualTree();_labelFormat.Dispose();}
}
