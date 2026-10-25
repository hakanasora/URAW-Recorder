using System.Diagnostics;
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
    private CanvasRenderTarget? _spectrumGrid;
    private (float W,float H,float Dpi) _gridKey;
    private PortableRecorder.Audio.SpectrumFrame? _projectedFrame;
    private Vector2[] _spectrumPoints=[];
    private float _projectionWidth;
    private readonly Dictionary<float,CanvasTextFormat> _formats = new();
    private double _low = 36, _targetLow = 36, _frameDelta,_lastDrawTime,_timeline;
    private readonly Stopwatch _visualClock=Stopwatch.StartNew();
    private readonly MeterMotion _meterMotion=new();
    private readonly CentsMotion _centsMotion=new();
    private CanvasRenderTarget? _needleGlow;
    private Vector2[] _fromSpectrum=[],_drawSpectrum=[];
    private double _blendStarted;
    private long _visualEpoch=-1;
    private int _visualSegment=-1;
    private bool _disposed;
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
        _face?.Dispose();_needleGlow?.Dispose();_needleGlow=null; _faceDpi=sender.Dpi;
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
        if(_needleGlow==null)
        {
            _needleGlow=new CanvasRenderTarget(sender,115,20,sender.Dpi);
            using var ink=_needleGlow.CreateDrawingSession();ink.Clear(Color.FromArgb(0,0,0,0));
            using var line=new CanvasCommandList(sender);
            using(var light=line.CreateDrawingSession())light.DrawLine(8,10,107,10,A(Amber,.9),2.5f);
            using var blur=new GaussianBlurEffect{Source=line,BlurAmount=3,BorderMode=EffectBorderMode.Soft};ink.DrawImage(blur);
        }
        var transform=ds.Transform;
        ds.Transform=Matrix3x2.CreateTranslation(-8,-10)*Matrix3x2.CreateRotation((float)a)*Matrix3x2.CreateTranslation(start)*transform;
        ds.DrawImage(_needleGlow);ds.Transform=transform;
        ds.DrawLine(start,end,Design.Gold,1.6f); ds.DrawLine(start,end,A(Ivory,.8),.65f);
        ds.FillCircle(start,6.7f,Design.Hex("0C0D0D"));
        using(var hub=new CanvasRadialGradientBrush(sender,[Stop(0,Ivory),Stop(.32f,Design.Gold),Stop(.8f,Amber),Stop(1,Design.Hex("AB6400"))])
            {Center=start-new Vector2(1.8f,2.2f),RadiusX=8,RadiusY=8}) ds.FillCircle(start,5.3f,hub);
        Text(ds,caption,x+130,y+175,12,Design.Muted);
    }
    public void Draw(CanvasControl sender,CanvasDrawEventArgs args,ShellViewModel state)
    {
        if(_disposed)return;
        double now=_visualClock.Elapsed.TotalSeconds;
        _frameDelta=Math.Clamp(now-_lastDrawTime,0,.050);_lastDrawTime=now;
        var sample=state.Audio.Latest;
        if(!sample.Valid || sample.Epoch!=_visualEpoch || sample.Segment!=_visualSegment)
        {
            _visualEpoch=sample.Epoch;_visualSegment=sample.Segment;_centsMotion.Reset();_projectedFrame=null;_spectrumPoints=[];_fromSpectrum=[];_drawSpectrum=[];
        }
        _meterMotion.Push(sample,now);var vu=_meterMotion.Read(now,ReducedMotion);
        _timeline=_meterMotion.Timeline(now);
        var ds=args.DrawingSession;
        var width=(float)sender.ActualWidth; var height=(float)sender.ActualHeight;
        ds.TextAntialiasing=CanvasTextAntialiasing.Grayscale;
        if(state.Mode==MonitorMode.Level)
        {
            var x=Stacked?(width-536)/2:256+(width-284-536)/2;
            var y=Stacked?StackedVuTop:(height-188)/2;
            Vu(sender,ds,x,y,vu.Left,"L"); Vu(sender,ds,x+276,y,vu.Right,"R");
        }
        else if(state.Mode==MonitorMode.Spectrum) Spectrum(sender,ds,state,width,height);
        else Pitch(sender,ds,state,width,height);

    }
    private void Spectrum(CanvasControl sender,CanvasDrawingSession ds,ShellViewModel state,float w,float h)
    {
        float left=288,right=w-28,top=24,bottom=h-40;
        float Y(double db)=>top+(float)SpectrumScale.LevelPosition(Math.Clamp(db,-96,0))*(bottom-top);
        float X(double f)=>left+(float)SpectrumScale.FrequencyPosition(f)*(right-left);
        var freqs=SpectrumScale.Frequencies;var dbs=SpectrumScale.Levels;
        var key=(w,h,sender.Dpi);
        if(_spectrumGrid==null || _gridKey!=key)
        {
            _spectrumGrid?.Dispose();_gridKey=key;
            _spectrumGrid=new CanvasRenderTarget(sender,w,h,sender.Dpi);
            using var ink=_spectrumGrid.CreateDrawingSession();ink.Clear(Color.FromArgb(0,0,0,0));
            for(int i=0;i<dbs.Length-1;i++)ink.DrawLine(left,Y((dbs[i]+dbs[i+1])/2),right,Y((dbs[i]+dbs[i+1])/2),A(Design.Grid,.48),.65f);
            for(int decade=10;decade<=10000;decade*=10)
                for(int part=1;part<=9;part++)
                {
                    int f=decade*part;
                    if(f>=20 && f<=20000 && !freqs.Contains(f))ink.DrawLine(X(f),top,X(f),bottom,A(Design.Grid,.48),.65f);
                }
            foreach(var db in dbs){ink.DrawLine(left,Y(db),right,Y(db),A(Design.Line,.78),.8f);Text(ink,$"{db}",left-22,Y(db),12,Design.Muted);}
            for(int i=0;i<freqs.Length;i++)
            {
                var f=freqs[i];ink.DrawLine(X(f),top,X(f),bottom,A(Design.Line,.78),.8f);
                Text(ink,f>=1000?$"{f/1000}k":$"{f}",X(f)+(i==0?5:i==freqs.Length-1?-6:0),bottom+21,12,Design.Muted);
            }
            Text(ink,"dBFS · 非线性",left+44,top-14,11,Design.Muted);
        }
        ds.DrawImage(_spectrumGrid);
        var frame=state.Snapshot.Spectrum;
        if(!state.Snapshot.Valid || frame==null)return;
        if(_projectedFrame!=frame || _projectionWidth!=w)
        {
            _projectedFrame=frame;_projectionWidth=w;
            var points=new List<Vector2>();var hz=frame.FrequenciesHz;var db=frame.Dbfs;
            int last=hz.Length-1;
            for(int pixel=0;pixel<=right-left;pixel++)
            {
                double f=20*Math.Pow(1000,pixel/(double)(right-left));
                if(f>hz[last])break;
                double start=20*Math.Pow(1000,Math.Max(0,pixel-.5)/(right-left));
                double end=20*Math.Pow(1000,Math.Min(right-left,pixel+.5)/(right-left));
                int first=Array.BinarySearch(hz,start);if(first<0)first=~first;
                int stop=Array.BinarySearch(hz,end);if(stop<0)stop=~stop;
                double level=double.NegativeInfinity;
                if(stop>first)for(int k=first;k<Math.Min(stop,hz.Length);k++)level=Math.Max(level,db[k]);
                else
                {
                    int index=Array.BinarySearch(hz,f);if(index<0)index=~index;
                    index=Math.Clamp(index,1,last);
                    // Power interpolation between real FFT points: no spline overshoot.
                    double mix=(f-hz[index-1])/(hz[index]-hz[index-1]);
                    double power=(1-mix)*Math.Pow(10,db[index-1]/10d)+mix*Math.Pow(10,db[index]/10d);
                    level=power>0?10*Math.Log10(power):double.NegativeInfinity;
                }
                points.Add(new(left+pixel,Y(level)));
            }
            _fromSpectrum=_drawSpectrum.Length==points.Count?_drawSpectrum.ToArray():points.ToArray();
            _spectrumPoints=points.ToArray();_drawSpectrum=new Vector2[points.Count];_blendStarted=_visualClock.Elapsed.TotalSeconds;
        }
        if(_spectrumPoints.Length<2)return;
        float blend=ReducedMotion?1:(float)Math.Clamp((_visualClock.Elapsed.TotalSeconds-_blendStarted)/.020,0,1);
        for(int i=0;i<_drawSpectrum.Length;i++)_drawSpectrum[i]=Vector2.Lerp(_fromSpectrum[i],_spectrumPoints[i],blend);
        using var chart=new CanvasCommandList(sender);
        using(var ink=chart.CreateDrawingSession())
        {
            using var brush=new CanvasLinearGradientBrush(sender,[Stop(0,A(Design.Gold,.84)),Stop(.14f,A(Design.Gold,.58)),Stop(.42f,A(Amber,.30)),Stop(.76f,A(Amber,.075)),Stop(1,A(Amber,0))]);
            foreach(var point in _drawSpectrum)
            {
                float depth=Math.Max(1,Math.Min((bottom-top)*.46f,bottom-point.Y));
                brush.StartPoint=point;brush.EndPoint=point+new Vector2(0,depth);
                brush.Opacity=(float)(.84+.16*Math.Cos((point.X-left)*Math.PI));
                ink.FillRectangle(point.X,point.Y,1.1f,bottom-point.Y,brush);
            }
            using var curve=Path(sender,_drawSpectrum);
            double level=Math.Clamp((state.Snapshot.RmsDbfs+60)/54,0,1);float bloom=(float)(level*level);
            using var glow=new CanvasCommandList(sender);
            using(var g=glow.CreateDrawingSession())g.DrawGeometry(curve,A(Design.Gold,.035+.48*bloom),2.5f);
            using(var blur=new GaussianBlurEffect{Source=glow,BlurAmount=2+8*bloom,BorderMode=EffectBorderMode.Soft})ink.DrawImage(blur);
            ink.DrawGeometry(curve,Design.Gold,1.8f);
        }
        using var mask=new CanvasCommandList(sender);
        using(var m=mask.CreateDrawingSession())
        using(var fade=new CanvasLinearGradientBrush(sender,[Stop(0,A(Design.White,0)),Stop(.05f,Design.White),Stop(.95f,Design.White),Stop(1,A(Design.White,0))]){StartPoint=new(left,0),EndPoint=new(right,0)})m.FillRectangle(left,top,right-left,bottom-top,fade);
        using(var faded=new AlphaMaskEffect{Source=chart,AlphaMask=mask})ds.DrawImage(faded);
    }
    private void Pitch(CanvasControl sender,CanvasDrawingSession ds,ShellViewModel state,float w,float h)
    {
        var midi=state.Snapshot.MidiNote;
        if(midi is double note)
        {
            if(note<_targetLow+3) _targetLow=note-7;
            if(note>_targetLow+33) _targetLow=note-29;
        }
        var dt=_frameDelta;
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
                var u=(p.Time-(_timeline-4))/4;
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

        var cents=_centsMotion.Advance(_visualEpoch,state.Snapshot.MidiNote,state.Snapshot.Cents,_frameDelta,ReducedMotion);
        Cents(ds,cents,28,CentsTop,200*TextScale);
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
    public void Dispose() { _disposed=true; _needleGlow?.Dispose(); _face?.Dispose(); _spectrumGrid?.Dispose(); foreach(var f in _formats.Values) f.Dispose(); }
}
