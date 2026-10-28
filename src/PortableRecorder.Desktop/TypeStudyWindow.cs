using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;

namespace PortableRecorder.Desktop;

/// <summary>Opt-in, offline typography specimen. No audio service or saved preference changes.</summary>
internal sealed class TypeStudyWindow : Window
{
    private readonly Grid _sheet=new(){Background=Design.Brush(Design.Display),Padding=new Thickness(20)};
    private readonly List<(TextBlock Level,TextBlock Peak,TextBlock Gain,TextBlock Clock)> _samples=[];
    private static readonly (string Level,string Peak,string Gain,string Clock)[] Values=
    [ ("25.9","−6.3","0 dB","00:08.52"), ("11.1","−11.1","−3.8 dB","11:11.11"),
      ("88.8","+0.0","+12.0 dB","08:08.88"), ("∞","−∞","0 dB","00:00.00") ];
    public TypeStudyWindow()
    {
        Title="Portable Recorder · 数字字体研究";
        _sheet.RowDefinitions.Add(new(){Height=GridLength.Auto});
        _sheet.RowDefinitions.Add(new(){Height=GridLength.Auto});
        _sheet.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        _sheet.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var heading=Design.Text("给仪表一个更鲜明的声音",22,null,true);
        _sheet.Children.Add(heading);
        var subtitle=Design.Text("原生 WinUI 字体样张 · 相同字号，不同字形 · 固定示例，未启动音频",12,Design.Muted);
        subtitle.Margin=new Thickness(0,8,0,14);Grid.SetRow(subtitle,1);_sheet.Children.Add(subtitle);
        var columns=new Grid{ColumnSpacing=16};
        for(int i=0;i<4;i++)columns.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        string[] names=["Segoe UI Variable","Bahnschrift","IBM Plex Sans","Barlow"];
        string[] descriptions=["现有对照 · Bold","工业仪器 · SemiBold","理性技术 · SemiBold","轻巧设备 · SemiBold"];
        FontFamily[] families=[new("Segoe UI Variable"),new("Bahnschrift"),
            new("ms-appx:///Assets/TypeStudy/IBMPlexSans.ttf#IBM Plex Sans"),
            new("ms-appx:///Assets/TypeStudy/Barlow-SemiBold.ttf#Barlow SemiBold")];
        for(int i=0;i<4;i++)
        {
            var panel=new StackPanel{Spacing=0};
            panel.Children.Add(Design.Text(names[i],15,null,true));
            var description=Design.Text(descriptions[i],11,Design.Muted);description.Margin=new Thickness(0,6,0,14);panel.Children.Add(description);
            var family=families[i];var weight=i==0?FontWeights.Bold:FontWeights.SemiBold;
            TextBlock Figure(string text,double size)
            {
                var label=Design.Text(text,size);label.FontFamily=family;label.FontWeight=weight;
                Typography.SetNumeralAlignment(label,FontNumeralAlignment.Tabular);
                label.TextWrapping=TextWrapping.NoWrap;label.Height=size>=60?82:size>=30?50:size>=20?32:24;return label;
            }
            var reading=new Grid{Height=82};
            reading.ColumnDefinitions.Add(new(){Width=new GridLength(22)});reading.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            reading.Children.Add(new Border{Width=16,Height=5,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Left,Background=Design.Brush(Design.White)});
            var level=Figure("25.9",69);level.CharacterSpacing=-20;Grid.SetColumn(level,1);reading.Children.Add(level);panel.Children.Add(reading);
            panel.Children.Add(Design.Text("dBFS · RMS",11,Design.Muted));
            var peak=Figure("PEAK −6.3 dBFS",14);peak.Margin=new Thickness(0,14,0,0);panel.Children.Add(peak);
            var pitch=Figure("C♯4",32);pitch.Margin=new Thickness(0,14,0,0);panel.Children.Add(pitch);
            var hz=Figure("275.22 Hz",20);hz.Margin=new Thickness(0,2,0,0);panel.Children.Add(hz);
            var tray=new StackPanel{Spacing=8,Padding=new Thickness(10),Background=Design.Brush(Design.Transport),Margin=new Thickness(0,16,0,0)};
            var gain=Figure("0 dB",13);gain.Foreground=Design.Brush(Design.Zero);tray.Children.Add(gain);
            var clock=Figure("00:08.52",32);tray.Children.Add(clock);panel.Children.Add(tray);
            _samples.Add((level,peak,gain,clock));Grid.SetColumn(panel,i);columns.Children.Add(panel);
        }
        Grid.SetRow(columns,2);_sheet.Children.Add(columns);
        var controls=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Margin=new Thickness(0,16,0,0)};
        string[] labels=["常规读数","窄字形 1","宽字形 8","静音"];
        for(int i=0;i<labels.Length;i++){int index=i;var button=Design.Button(labels[i],labels[i]);button.Height=32;button.Click+=(_,_)=>Apply(index);controls.Children.Add(button);}
        Grid.SetRow(controls,3);_sheet.Children.Add(controls);
        Content=_sheet;AppWindow.Resize(new SizeInt32(1200,790));
        _sheet.Loaded+=async(_,_)=>
        {
            try
            {
                var scale=_sheet.XamlRoot.RasterizationScale;
                AppWindow.Resize(new SizeInt32((int)(940*scale),(int)(570*scale)));
                await Task.Delay(200);
                foreach(var sample in _samples)
                {
                    double Width(string value)
                    {
                        var probe=new TextBlock{Text=value,FontFamily=sample.Level.FontFamily,FontSize=69,FontWeight=sample.Level.FontWeight};
                        Typography.SetNumeralAlignment(probe,FontNumeralAlignment.Tabular);
                        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity,double.PositiveInfinity));return probe.DesiredSize.Width;
                    }
                    StartupLog.Write($"Type study digit widths: {sample.Level.FontFamily.Source}; 111.1={Width("111.1"):F3}; 888.8={Width("888.8"):F3}");
                }
                string? output=Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith("--type-study-export="))?.Split('=',2)[1];
                if(output==null)return;
                await Task.Delay(600);
                Directory.CreateDirectory(output);
                for(int i=0;i<3;i++){Apply(i);await Task.Delay(200);await Export(Path.Combine(output,$"type-study-{i}.png"));}
                Apply(0);StartupLog.Write("Type study exports complete: "+output);
            }
            catch(Exception error){StartupLog.Write("Type study export failed: "+error);}
        };
    }
    private void Apply(int index)
    {
        var value=Values[index];
        foreach(var sample in _samples)
        {
            sample.Level.Text=value.Level;sample.Peak.Text="PEAK "+value.Peak+" dBFS";
            sample.Peak.Foreground=Design.Brush(index==2?Design.Alert:Design.White);
            sample.Gain.Text=value.Gain;sample.Gain.Foreground=Design.Brush(index is 1 or 2?Design.Blue:Design.Zero);sample.Clock.Text=value.Clock;
        }
    }
    private async Task Export(string path)
    {
        var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(_sheet);
        byte[] pixels=(await bitmap.GetPixelsAsync()).ToArray();
        using var file=File.Create(path);using var stream=file.AsRandomAccessStream();
        var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels);
        await encoder.FlushAsync();
    }
}
