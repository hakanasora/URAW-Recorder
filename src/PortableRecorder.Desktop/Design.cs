using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PortableRecorder.Desktop;

public static class Design
{
    public static readonly Color Display = Hex("1B1B1B"), Transport = Hex("0E0E0E"), Inspector = Hex("252527"),
        Field = Hex("49494D"), White = Hex("F5F5F7"), Muted = Hex("86868B"), Label = Hex("A1A1A6"),
        Grey = Hex("6E6E73"), Zero = Hex("9D9D9F"), Blue = Hex("0387FF"), Alert = Hex("FF3037"),
        Record = Hex("D3222B"), Action = Hex("1D1D1F"), Selected = Hex("343437"), Gold = Hex("FFB700"),
        Grid = Hex("29292C"), Line = Hex("323234"), Link = Hex("DFBC79");
    public static Color Hex(string value) => Color.FromArgb(255,Convert.ToByte(value[..2],16),Convert.ToByte(value[2..4],16),Convert.ToByte(value[4..6],16));
    public static SolidColorBrush Brush(Color c) => new(c);
    public static TextBlock Text(string value, double size = 13, Color? color = null, bool semibold = false)
    {
        var t = new TextBlock { Text=value, FontSize=size, Foreground=Brush(color??White),
            FontFamily=new FontFamily(value.Any(c=>c>0x2E80) ? "Microsoft YaHei UI" : "Segoe UI Variable"),
            FontWeight=semibold?FontWeights.SemiBold:FontWeights.Normal, VerticalAlignment=VerticalAlignment.Center };
        Typography.SetNumeralAlignment(t,FontNumeralAlignment.Tabular);
        Typography.SetNumeralStyle(t,FontNumeralStyle.Lining);
        return t;
    }
    public static Button Button(object content, string name, Color? fill=null)
    {
        var b=new Button { Content=content, Height=42, Padding=new Thickness(12,0,12,0),
            Background=Brush(fill??Action), Foreground=Brush(White), BorderThickness=new Thickness(0),
            CornerRadius=new CornerRadius(5), FontSize=14, FontWeight=FontWeights.SemiBold,
            HorizontalContentAlignment=HorizontalAlignment.Center, VerticalContentAlignment=VerticalAlignment.Center };
        b.Template=(ControlTemplate)XamlReader.Load("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Border Background="{TemplateBinding Background}" CornerRadius="{TemplateBinding CornerRadius}" Padding="{TemplateBinding Padding}">
                <ContentPresenter Content="{TemplateBinding Content}" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" Foreground="{TemplateBinding Foreground}"/>
              </Border>
            </ControlTemplate>
            """);
        AutomationProperties.SetName(b,name); ToolTipService.SetToolTip(b,name);
        b.PointerEntered+=(_,_)=> { if(b.IsEnabled) b.Background=Brush(fill==Record?Hex("D9242D"):Hex("29292C")); };
        b.PointerExited+=(_,_)=> b.Background=Brush(fill??Action);
        b.PointerPressed+=(_,_)=> { if(b.IsEnabled) b.Background=Brush(fill==Record?Hex("B91C25"):Hex("151517")); };
        b.PointerReleased+=(_,_)=> b.Background=Brush(fill??Action);
        return b;
    }
    public static FontIcon Icon(string glyph, double size=16, Color? color=null) =>
        new() { Glyph=glyph, FontFamily=new FontFamily("Segoe Fluent Icons"), FontSize=size, Foreground=Brush(color??Muted), IsHitTestVisible=false };
    public static StackPanel ActionContent(string kind, string? label=null, Color? color=null)
    {
        var ink=color??White;
        var panel=new StackPanel { Orientation=Orientation.Horizontal, Spacing=8, VerticalAlignment=VerticalAlignment.Center };
        var box=new Grid { Width=18, Height=18, VerticalAlignment=VerticalAlignment.Center };
        if(kind=="pause")
        {
            var bars=new StackPanel { Orientation=Orientation.Horizontal, Spacing=4, HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center };
            for(var i=0;i<2;i++) bars.Children.Add(new Border { Width=4,Height=14,Background=Brush(ink) });
            box.Children.Add(bars);
        }
        else if(kind=="play")
        {
            var path=(Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load("""
              <Path xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Data="M0,0 L12,7 L0,14 Z" Width="12" Height="14" HorizontalAlignment="Center" VerticalAlignment="Center"/>
              """);
            path.Fill=Brush(ink); box.Children.Add(path);
        }
        else box.Children.Add(new Border { Width=10,Height=10,CornerRadius=new CornerRadius(kind=="record"?5:1),Background=Brush(ink),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center });
        panel.Children.Add(box);
        if(label!=null) panel.Children.Add(Text(label,14,ink,true));
        return panel;
    }
    public static SelectorBar Selector(string[] labels, double itemWidth, double gap, Action<int> changed)
    {
        var bar=new SelectorBar { Padding=new Thickness(0), HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center };
        for(var i=0;i<labels.Length;i++)
        {
            var item=new SelectorBarItem { Text=labels[i], Width=itemWidth, Height=28, MinWidth=0, MinHeight=0,
                Padding=new Thickness(0), Margin=new Thickness(0,0,i==labels.Length-1?0:gap,0), Foreground=Brush(Muted) };
            item.Template=(ControlTemplate)XamlReader.Load("""
              <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="SelectorBarItem">
                <Border Background="{TemplateBinding Background}" CornerRadius="3">
                  <TextBlock Text="{TemplateBinding Text}" Foreground="{TemplateBinding Foreground}" FontFamily="Microsoft YaHei UI" FontSize="13" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
              </ControlTemplate>
              """);
            bar.Items.Add(item);
        }
        bar.SelectionChanged+=(_,_)=>
        {
            for(var i=0;i<bar.Items.Count;i++)
            {
                var item=bar.Items[i];
                item.Background=Brush(item==bar.SelectedItem?Selected:Colors.Transparent);
                item.Foreground=Brush(item==bar.SelectedItem?White:Muted);
            }
            if(bar.SelectedItem is not null) changed(bar.Items.IndexOf(bar.SelectedItem));
        };
        bar.SelectedItem=bar.Items[0];
        return bar;
    }
}
