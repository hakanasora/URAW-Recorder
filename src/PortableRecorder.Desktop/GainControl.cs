using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI.Core;

namespace PortableRecorder.Desktop;

public sealed class GainControl : UserControl
{
    private readonly ShellViewModel _state;
    private readonly TextBlock _number=Design.Text("0 dB",13,Design.Zero);
    private readonly TextBox _editor=new(){Visibility=Visibility.Collapsed,Width=100,Height=36,FontSize=13};
    private readonly Line _marker;
    private bool _dragging;
    private double _startY,_startGain;
    public GainControl(ShellViewModel state)
    {
        _state=state; Width=112; Height=44; IsTabStop=true; TabIndex=4;
        AutomationProperties.SetName(this,"软件增益");
        ToolTipService.SetToolTip(this,"上拖增加，下拖衰减；Shift 精调；双击归零；Enter 输入数值");
        var root=new Grid{Background=Design.Brush(Microsoft.UI.Colors.Transparent)};
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,VerticalAlignment=VerticalAlignment.Center};
        var knob=new Grid{Width=17,Height=17};
        knob.Children.Add(new Ellipse{Stroke=Design.Brush(Design.Grey),StrokeThickness=1.4});
        _marker=new Line{X1=8.5,Y1=8.5,X2=8.5,Y2=1,Stroke=Design.Brush(Design.Grey),StrokeThickness=1.5};
        knob.Children.Add(_marker); row.Children.Add(knob);row.Children.Add(_number);
        root.Children.Add(row);root.Children.Add(_editor);Content=root;
        PointerPressed+=Pressed;PointerMoved+=Moved;
        PointerReleased+=(s,e)=>{_dragging=false;ReleasePointerCapture(e.Pointer);};
        PointerCanceled+=(s,e)=>CancelDrag();PointerCaptureLost+=(s,e)=>_dragging=false;
        DoubleTapped+=(s,e)=>{_dragging=false;_state.SetGain(0);e.Handled=true;};KeyDown+=Key;
        _editor.KeyDown+=(s,e)=>
        {
            if(e.Key==VirtualKey.Escape){_editor.Visibility=Visibility.Collapsed;Focus(FocusState.Programmatic);e.Handled=true;}
            if(e.Key==VirtualKey.Enter)
            {
                var value=_editor.Text.Replace("dB","",StringComparison.OrdinalIgnoreCase).Replace('−','-').Trim();
                if(double.TryParse(value,NumberStyles.Float,CultureInfo.CurrentCulture,out var db)||double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out db))
                    if(double.IsFinite(db)){_state.SetGain(db);_editor.Visibility=Visibility.Collapsed;Focus(FocusState.Programmatic);}
                e.Handled=true;
            }
        };
    }
    private static bool Shift=>(InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)&CoreVirtualKeyStates.Down)!=0;
    private void Pressed(object sender,PointerRoutedEventArgs e)
    {
        if(_editor.Visibility==Visibility.Visible||!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)return;
        Focus(FocusState.Pointer);_startY=e.GetCurrentPoint(this).Position.Y;_startGain=_state.GainDb;
        _dragging=CapturePointer(e.Pointer);e.Handled=true;
    }
    private void Moved(object sender,PointerRoutedEventArgs e)
    {if(_dragging){_state.SetGain(_startGain+(_startY-e.GetCurrentPoint(this).Position.Y)*(Shift?.025:.25));e.Handled=true;}}
    private void CancelDrag(){if(_dragging){_dragging=false;_state.SetGain(_startGain);ReleasePointerCaptures();}}
    private void Key(object sender,KeyRoutedEventArgs e)
    {
        if(_editor.Visibility==Visibility.Visible)return;
        switch(e.Key)
        {
            case VirtualKey.Up:case VirtualKey.Right:_state.SetGain(_state.GainDb+(Shift?.1:1));break;
            case VirtualKey.Down:case VirtualKey.Left:_state.SetGain(_state.GainDb-(Shift?.1:1));break;
            case VirtualKey.Home:case VirtualKey.Number0:_state.SetGain(0);break;
            case VirtualKey.Escape:CancelDrag();break;
            case VirtualKey.F2:case VirtualKey.Enter:_editor.Text=_state.GainDb.ToString("0.0");_editor.Visibility=Visibility.Visible;_editor.Focus(FocusState.Programmatic);_editor.SelectAll();break;
            default:return;
        }
        e.Handled=true;
    }
    public void Refresh()
    {
        var value=_state.GainDb;_number.Text=value==0?"0 dB":$"{(value>0?"+":"−")}{Math.Abs(value):0.0} dB";
        _number.Foreground=Design.Brush(value==0?Design.Zero:Design.Blue);
        _marker.RenderTransform=new RotateTransform{CenterX=8.5,CenterY=8.5,Angle=value/(value>=0?24:60)*130};
    }
    protected override AutomationPeer OnCreateAutomationPeer()=>new GainPeer(this,_state);
    private sealed class GainPeer(GainControl owner,ShellViewModel state):FrameworkElementAutomationPeer(owner),IRangeValueProvider
    {
        protected override string GetClassNameCore()=>"GainControl";
        protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.Slider;
        protected override object GetPatternCore(PatternInterface pattern)=>pattern==PatternInterface.RangeValue?this:base.GetPatternCore(pattern);
        public bool IsReadOnly=>false;public double LargeChange=>1;public double SmallChange=>.1;
        public double Maximum=>24;public double Minimum=>-60;public double Value=>state.GainDb;
        public void SetValue(double value)=>state.SetGain(value);
    }
}
