using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace PortableRecorder.Desktop;

/// <summary>Uniform neutral veils over a single system Mica backdrop. No synthetic gray gradient.</summary>
internal static class MaterialSurface
{
    public const int DisplayVeilAlpha=102;
    public const int TransportVeilAlpha=13;

    public static Color Tint(Color color,int alpha)=>Color.FromArgb((byte)alpha,color.R,color.G,color.B);

    public static Grid DisplayPanel(Color fill)
        =>new(){Background=Design.Brush(fill),IsHitTestVisible=false};
}
