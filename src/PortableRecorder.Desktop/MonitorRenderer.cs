using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace PortableRecorder.Desktop;

public sealed class MonitorRenderer : IDisposable
{
    private CanvasRenderTarget? _face;
    private readonly Dictionary<float,CanvasTextFormat> _formats = new();
    private double _low = 36, _targetLow = 36, _lastTime;
    private float _faceDpi;
    private static readonly double[] Marks=[-20,-10,-7,-5,-3,0,3];
    private static readonly double[] Angles=[-153,-132,-111,-90,-69,-48,-27];
    private static readonly Color Amber=Design.Hex("FF9600"), Ivory=Design.Hex("FFE6AF"), Positive=Design.Hex("F38B58");
    public bool ReducedMotion { get; set; }
    public float TextScale { get; set; } = 1;
    public bool Stacked { get; set; }
    public float StackedVuTop { get; set; }
    public float CentsTop { get; set; } = 154;
    public string NumericFont { get; set; } = "Segoe UI Variable";
    private static Color A(Color c,double a) => Color.FromArgb((byte)Math.Clamp(Math.Round(a*255),0,255),c.R,c.G,c.B);
    private CanvasTextFormat Format(float size)
    {
        size*=TextScale;
        if(!_formats.TryGetValue(size,out var f))
            _formats[size]=f=new CanvasTextFormat { FontFamily="Segoe UI Variable",FontSize=size,HorizontalAlignment=CanvasHorizontalAlignment.Center,VerticalAlignment=CanvasVerticalAlignment.Center };
        return f;
    }
    private void Text(CanvasDrawingSession ds,string value,float x,float y,float size,Color color)
    {
        ds.DrawText(value,new Rect(x-60,y-12*TextScale,120,24*TextScale),color,Format(size));
    }
    private static CanvasGeometry Path(ICanvasResourceCreator device,IReadOnlyList<Vector2> points,bool close=false)
    {
        using var builder=new CanvasPathBuilder(device);
        builder.BeginFigure(points[0]);
        for(var i=1;i<points.Count;i++) builder.AddLine(points[i]);
        builder.EndFigure(close?CanvasFigureLoop.Closed:CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(builder);
    }
    private static CanvasGeometry Arc(ICanvasResourceCreator device,float radius,double start,double end)
    {
        var points=new List<Vector2>();
        for(var i=0;i<=100;i++) { var a=(start+(end-start)*i/100)*Math.PI/180; points.Add(new(130+radius*(float)Math.Cos(a),150+radius*(float)Math.Sin(a))); }
        return Path(device,points);
    }
    private static double Angle(double value)
    {
        var i=0; while(i<Marks.Length-2 && value>Marks[i+1]) i++;
        return (Angles[i]+(value-Marks[i])/(Marks[i+1]-Marks[i])*(Angles[i+1]-Angles[i]))*Math.PI/180;
    }
    private static CanvasGradientStop Stop(float position,Color color) => new(){Position=position,Color=color};
    private static void Glow(CanvasDrawingSession ds,Vector2 center,float rx,float ry,CanvasGradientStop[] stops)
    {
        using var brush=new CanvasRadialGradientBrush(ds,stops){Center=center,RadiusX=rx,RadiusY=ry};
        ds.FillEllipse(center,rx,ry,brush);
    }
    private CanvasRenderTarget Face(CanvasControl sender)
    {
        if(_face!=null && _faceDpi==sender.Dpi) return _face;
        _face?.Dispose(); _faceDpi=sender.Dpi;
        _face=new CanvasRenderTarget(sender,260,159,sender.Dpi);
        using var ds=_face.CreateDrawingSession(); ds.Clear(Color.FromArgb(0,0,0,0));
        const float cx=130,cy=150,r=104;
        Glow(ds,new(cx,cy-r*.28f),r*1.02f,r*.71f,
            [Stop(0,A(Amber,.14)),Stop(.45f,A(Amber,.095)),Stop(1,A(Amber,0))]);
        foreach(var side in new[]{-1,1})
        {
            var x=cx+side*r*.38f;
            using var beam=new CanvasCommandList(sender);
            using(var light=beam.CreateDrawingSession())
            {
                using var b=new CanvasPathBuilder(sender);
                b.BeginFigure(x-r*.12f,cy+3);
                b.AddCubicBezier(new(x-r*.24f,cy-r*.12f),new(x-r*.70f,cy-r*.42f),new(x-r*.54f,cy-r*.72f));
                b.AddCubicBezier(new(x-r*.30f,cy-r*.91f),new(x+r*.30f,cy-r*.91f),new(x+r*.54f,cy-r*.72f));
                b.AddCubicBezier(new(x+r*.70f,cy-r*.42f),new(x+r*.24f,cy-r*.12f),new(x+r*.12f,cy+3));
                b.EndFigure(CanvasFigureLoop.Closed);
                using var shape=CanvasGeometry.CreatePath(b);
                using var gradient=new CanvasLinearGradientBrush(sender,
                    [Stop(0,A(Design.Gold,.96)),Stop(.12f,A(Amber,.86)),Stop(.38f,A(Amber,.53)),Stop(.72f,A(Amber,.19)),Stop(1,A(Amber,0))])
                    {StartPoint=new(0,cy+2),EndPoint=new(0,cy-r*.84f)};
                light.FillGeometry(shape,gradient);
            }
            using var blur=new GaussianBlurEffect{Source=beam,BlurAmount=r*.065f,BorderMode=EffectBorderMode.Soft};
            ds.DrawImage(blur);
            Glow(ds,new(x,cy-3),r*.19f,r*.13f,
                [Stop(0,Ivory),Stop(.14f,A(Design.Gold,.98)),Stop(.42f,A(Amber,.76)),Stop(.72f,A(Amber,.23)),Stop(1,A(Amber,0))]);
        }
        using(var fade=new CanvasLinearGradientBrush(sender,[Stop(0,Color.FromArgb(255,0,0,0)),Stop(.44f,Color.FromArgb(255,0,0,0)),Stop(1,Color.FromArgb(0,0,0,0))])
            {StartPoint=new(0,cy-8),EndPoint=new(0,cy+7)})
        {
            using var mask=new CanvasCommandList(sender);
            using(var m=mask.CreateDrawingSession()) m.FillRectangle(0,0,260,159,fade);
            ds.DrawImage(mask,Vector2.Zero,new Rect(0,0,260,159),1,CanvasImageInterpolation.Linear,CanvasComposite.DestinationIn);
        }
        using(var reflection=new CanvasLinearGradientBrush(sender,[Stop(0,A(Ivory,.36)),Stop(.55f,A(Amber,.12)),Stop(1,A(Amber,0))])
            {StartPoint=new(0,cy-r-17),EndPoint=new(0,cy)})
        using(var arc=Arc(sender,r+16,-153,-27)) ds.DrawGeometry(arc,reflection,.8f);
        using var dial=new CanvasCommandList(sender);
        using(var ink=dial.CreateDrawingSession())
        {
            using(var arc=Arc(sender,r,-153,-48)) ink.DrawGeometry(arc,A(Ivory,.65),1);
            using(var arc=Arc(sender,r,-48,-27)) ink.DrawGeometry(arc,Positive,1);
            for(var i=0;i<=36;i++)
            {
                var a=(-153+i*126.0/36)*Math.PI/180;
                ink.DrawLine(new(cx+(r-4)*(float)Math.Cos(a),cy+(r-4)*(float)Math.Sin(a)),new(cx+r*(float)Math.Cos(a),cy+r*(float)Math.Sin(a)),a>Angle(0)?Positive:A(Ivory,.86),1);
            }
            for(var i=0;i<Marks.Length;i++)
            {
                var a=Angles[i]*Math.PI/180; var color=Marks[i]>0?Positive:Ivory;
                ink.DrawLine(new(cx+(r-8)*(float)Math.Cos(a),cy+(r-8)*(float)Math.Sin(a)),new(cx+r*(float)Math.Cos(a),cy+r*(float)Math.Sin(a)),color,1);
                // Existing dial text keeps its original optical size, independent of UI text enlargement.
                using var format=new CanvasTextFormat{FontFamily="Segoe UI Variable",FontSize=11,HorizontalAlignment=CanvasHorizontalAlignment.Center,VerticalAlignment=CanvasVerticalAlignment.Center};
                ink.DrawText(Marks[i]>0?$"+{Marks[i]}":$"{Marks[i]}",new Rect(cx+(r+14)*Math.Cos(a)-24,cy+(r+14)*Math.Sin(a)-9,48,18),color,format);
            }
            using var vuFormat=new CanvasTextFormat{FontFamily="Segoe UI Variable",FontSize=16,HorizontalAlignment=CanvasHorizontalAlignment.Center};
            ink.DrawText("VU",new Rect(cx+6,cy-41,36,24),Ivory,vuFormat);
        }
        using(var blur=new GaussianBlurEffect{Source=dial,BlurAmount=2,BorderMode=EffectBorderMode.Soft}) ds.DrawImage(blur,Vector2.Zero,new Rect(0,0,260,159),.65f);
        ds.DrawImage(dial);
        return _face;
    }
    private void Vu(CanvasControl sender,CanvasDrawingSession ds,float x,float y,double value,string caption)
    {
        ds.DrawImage(Face(sender),x,y);
        var a=Angle(Math.Clamp(value,-20,3));
        var start=new Vector2(x+130,y+150); var end=start+new Vector2(99*(float)Math.Cos(a),99*(float)Math.Sin(a));
        using(var cmd=new CanvasCommandList(sender))
        {
            using(var glow=cmd.CreateDrawingSession()) glow.DrawLine(start,end,A(Amber,.9),2.5f);
            using var blur=new GaussianBlurEffect{Source=cmd,BlurAmount=3,BorderMode=EffectBorderMode.Soft}; ds.DrawImage(blur);
        }
        ds.DrawLine(start,end,Design.Gold,1.6f); ds.DrawLine(start,end,A(Ivory,.8),.65f);
        ds.FillCircle(start,6.7f,Design.Hex("0C0D0D"));
        using(var hub=new CanvasRadialGradientBrush(sender,[Stop(0,Ivory),Stop(.32f,Design.Gold),Stop(.8f,Amber),Stop(1,Design.Hex("AB6400"))])
            {Center=start-new Vector2(1.8f,2.2f),RadiusX=8,RadiusY=8}) ds.FillCircle(start,5.3f,hub);
        Text(ds,caption,x+130,y+175,12,Design.Muted);
    }
    public void Draw(CanvasControl sender,CanvasDrawEventArgs args,ShellViewModel state)
    {
        var ds=args.DrawingSession;
        var width=(float)sender.ActualWidth; var height=(float)sender.ActualHeight;
        ds.TextAntialiasing=CanvasTextAntialiasing.Grayscale;
        if(state.Mode==MonitorMode.Level)
        {
            var x=Stacked?(width-536)/2:256+(width-284-536)/2;
            var y=Stacked?StackedVuTop:(height-188)/2;
            Vu(sender,ds,x,y,state.Snapshot.VuLeft,"L"); Vu(sender,ds,x+276,y,state.Snapshot.VuRight,"R");
        }
        else if(state.Mode==MonitorMode.Spectrum) Spectrum(sender,ds,state,width,height);
        else Pitch(sender,ds,state,width,height);
        _lastTime=state.MonitorTime;
    }
    private void Spectrum(CanvasControl sender,CanvasDrawingSession ds,ShellViewModel state,float w,float h)
    {
        // Shares the instrument region with VU; the left RMS/PEAK block stays visible.
        float left=288,right=w-28,top=16,bottom=h-40;
        float Y(double db)=>top+(float)((6-db)/96)*(bottom-top);
        float X(double f)=>left+(float)(Math.Log10(f/20)/3)*(right-left);
        double[] freqs=[20,100,500,2000,10000,20000]; double[] dbs=[-12,-30,-48,-66,-84];
        using var chart=new CanvasCommandList(sender);
        using(var ink=chart.CreateDrawingSession())
        {
            for(var db=-88.5;db<=6;db+=4.5) ink.DrawLine(left,Y(db),right,Y(db),A(Design.Grid,.48),.65f);
            for(var decade=10;decade<=10000;decade*=10)
                for(var part=2;part<20;part++) { var f=decade*part/2.0; if(f>=20 && f<=20000) ink.DrawLine(X(f),top,X(f),bottom,A(Design.Grid,.48),.65f); }
            foreach(var db in dbs) ink.DrawLine(left,Y(db),right,Y(db),A(Design.Line,.78),.8f);
            foreach(var f in freqs) ink.DrawLine(X(f),top,X(f),bottom,A(Design.Line,.78),.8f);
            var points=state.Snapshot.SpectrumBins.Select((db,i)=>new Vector2(left+i*(right-left)/(state.Snapshot.SpectrumBins.Length-1),Math.Clamp(Y(db),top,bottom))).ToArray();
            using var brush=new CanvasLinearGradientBrush(sender,[Stop(0,A(Design.Gold,.84)),Stop(.14f,A(Design.Gold,.58)),Stop(.42f,A(Amber,.30)),Stop(.76f,A(Amber,.075)),Stop(1,A(Amber,0))]);
            for(var x=(int)left;x<right;x++)
            {
                var pos=(x-left)/(right-left)*(points.Length-1); var i=Math.Min(points.Length-2,(int)pos); var mix=pos-i;
                var y=points[i].Y*(1-mix)+points[i+1].Y*mix; var depth=Math.Max(1,Math.Min((bottom-top)*.46f,bottom-y));
                brush.StartPoint=new(x,y); brush.EndPoint=new(x,y+depth); brush.Opacity=(float)(.84+.16*Math.Cos((x-left)*Math.PI));
                ink.FillRectangle(x,y,1.1f,bottom-y,brush);
            }
            using var curve=Path(sender,points);
            var b=Math.Clamp((state.Snapshot.RmsDbfs+34)/4,0,1); var bloom=(float)(b*b);
            using var glow=new CanvasCommandList(sender);
            using(var g=glow.CreateDrawingSession()) g.DrawGeometry(curve,A(Design.Gold,.035+.48*bloom),2.5f);
            using(var blur=new GaussianBlurEffect{Source=glow,BlurAmount=2+8*bloom,BorderMode=EffectBorderMode.Soft}) ink.DrawImage(blur);
            ink.DrawGeometry(curve,Design.Gold,1.8f);
        }
        using var mask=new CanvasCommandList(sender);
        using(var m=mask.CreateDrawingSession())
        using(var fade=new CanvasLinearGradientBrush(sender,[Stop(0,A(Design.White,0)),Stop(.05f,Design.White),Stop(.95f,Design.White),Stop(1,A(Design.White,0))]){StartPoint=new(left,0),EndPoint=new(right,0)}) m.FillRectangle(left,top,right-left,bottom-top,fade);
        using(var faded=new AlphaMaskEffect{Source=chart,AlphaMask=mask}) ds.DrawImage(faded);
        foreach(var db in dbs) Text(ds,$"{db}",left-22,Y(db),12,Design.Muted);
        for(var i=0;i<freqs.Length;i++) Text(ds,freqs[i]>=1000?$"{freqs[i]/1000}k":$"{freqs[i]}",X(freqs[i])+(i==0?5:i==freqs.Length-1?-6:0),bottom+21,12,Design.Muted);
        Text(ds,"dB",left-22,top-4,12,Design.Muted); Text(ds,"Hz",right-7,h-5,12,Design.Muted);
    }
    private void Pitch(CanvasControl sender,CanvasDrawingSession ds,ShellViewModel state,float w,float h)
    {
        var midi=state.Snapshot.MidiNote;
        if(midi is double note)
        {
            if(note<_targetLow+3) _targetLow=note-7;
            if(note>_targetLow+33) _targetLow=note-29;
        }
        var dt=Math.Clamp(state.MonitorTime-_lastTime,0,.2);
        _low=ReducedMotion?_targetLow:_low+(_targetLow-_low)*(1-Math.Exp(-dt/.055));
        float left=288,right=w-8,top=16,bottom=h-28;
        if(Stacked) { left=60; top=StackedVuTop; }
        float Y(double n)=>top+(float)((_low+36-n)/36)*(bottom-top);
        using var chart=new CanvasCommandList(sender);
        using(var ink=chart.CreateDrawingSession())
        {
            using var clip=ink.CreateLayer(1,new Rect(left,top,right-left,bottom-top));
            for(var n=Math.Ceiling(_low/3)*3;n<=_low+36;n+=3)
                ink.DrawLine(left,Y(n),right,Y(n),A(n%12==0?Design.Line:Design.Grid,n%12==0?.85:.45),n%12==0?1:.65f);
            Vector2? last=null;
            foreach(var p in state.PitchHistory)
            {
                var u=(p.Time-(state.MonitorTime-4))/4;
                if(u<0 || u>1 || p.Midi is not double n) { last=null; continue; }
                var point=new Vector2(left+(float)u*(right-left)*.94f,Y(n));
                if(last is Vector2 prev) ink.DrawLine(prev,point,Design.Gold,1.45f);
                last=point;
            }
        }
        using var mask=new CanvasCommandList(sender);
        using(var m=mask.CreateDrawingSession())
        using(var fade=new CanvasLinearGradientBrush(sender,[Stop(0,Design.White),Stop(.76f,Design.White),Stop(1,A(Design.White,0))]){StartPoint=new(left,0),EndPoint=new(right,0)}) m.FillRectangle(left,top,right-left,bottom-top,fade);
        using(var faded=new AlphaMaskEffect{Source=chart,AlphaMask=mask}) ds.DrawImage(faded);
        ds.DrawLine(left,top,left,bottom,Design.Muted,1);
        for(var n=(int)Math.Ceiling(_low);n<=_low+36;n++)
            if(n%12==0 || (bottom-top>=230 && n%12==7)) Text(ds,$"{(n%12==0?"C":"G")}{n/12-1}",left-22,Y(n),12,Design.Muted);
        Text(ds,"−4 s",left+12,h-9,12,Design.Muted); Text(ds,"现在",right-12,h-9,12,Design.Muted);
        Cents(ds,state.Snapshot.Cents,28,CentsTop,200*TextScale);
    }
    private void Cents(CanvasDrawingSession ds,double? cents,float x,float y,float width)
    {
        if(cents is not double value) return;
        for(var n=-50;n<=50;n+=5)
        {
            var xx=x+7+(float)((n+50-value)/100)*(width-14);
            var u=(xx-x)/width; if(u<0||u>1) continue;
            var fade=Math.Clamp(Math.Min(u/.27,(1-u)/.27),0,1); var major=n%25==0;
            ds.DrawLine(xx,y+(major?6:12),xx,y+24,A(Design.Muted,fade*(major?.85:.55)),1);
            if(major) Text(ds,n==0?"0":n>0?$"+{n}":$"−{-n}",xx,y+43,12,A(Design.Muted,fade));
        }
        ds.DrawLine(x+width/2,y+3,x+width/2,y+27,Design.Gold,2);
    }
    public void Dispose() { _face?.Dispose(); foreach(var f in _formats.Values) f.Dispose(); }
}
