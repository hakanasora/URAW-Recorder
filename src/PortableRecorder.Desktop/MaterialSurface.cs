using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace PortableRecorder.Desktop;

/// <summary>Neutral content layer above the system backdrop; no layout or input ownership.</summary>
internal static class MaterialSurface
{
    public const int DisplayVeilAlpha=160;
    public const int TransportVeilAlpha=144;

    public static Color Tint(Color color,int alpha)=>Color.FromArgb((byte)alpha,color.R,color.G,color.B);

    public static Grid DisplayPanel(Color fill,bool materialEnabled)
    {
        var panel=new Grid{Background=Design.Brush(fill),IsHitTestVisible=false};
        if(!materialEnabled)return panel;
        // A restrained top light and contour lift the single instrument surface, not each instrument.
        var light=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(0,1)};
        light.GradientStops.Add(new GradientStop{Offset=0,Color=Tint(Colors.White,8)});
        light.GradientStops.Add(new GradientStop{Offset=.8,Color=Tint(Colors.White,0)});
        panel.Children.Add(new Border{Background=light});
        panel.Children.Add(new Border{Height=1,Background=Design.Brush(Tint(Colors.White,14)),VerticalAlignment=VerticalAlignment.Top});
        return panel;
    }
}
