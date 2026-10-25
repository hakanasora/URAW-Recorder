using PortableRecorder.Audio;
namespace PortableRecorder.Desktop;

// Presentation-only interpolation. Measurements, peak hold, recordings and VU detector stay untouched.
internal sealed class MeterMotion
{
    private AudioSnapshot? _previous,_current;
    private double _received;
    public void Push(AudioSnapshot sample,double now)
    {
        if(!sample.Valid){_previous=null;_current=null;return;}
        if(_current?.Epoch!=sample.Epoch || _current!=null && sample.Time-_current.Time>.200){_previous=null;_current=null;}
        if(_current?.FramePosition==sample.FramePosition)return;
        _previous=_current;_current=sample;_received=now;
    }
    public double Timeline(double now)=>_current==null?0:_current.Time+Math.Clamp(now-_received,0,.080);
    public (double Left,double Right) Read(double now,bool reducedMotion)
    {
        if(_current==null)return(double.NegativeInfinity,double.NegativeInfinity);
        if(_previous==null || reducedMotion)return(_current.VuLeft,_current.VuRight);
        double span=_current.Time-_previous.Time;
        if(span<=0)return(_current.VuLeft,_current.VuRight);
        double mix=Math.Clamp((now-_received)/Math.Clamp(span,.008,.080),0,1);
        static double Interpolate(double a,double b,double t)
        {
            double value=(1-t)*Math.Pow(10,a/20)+t*Math.Pow(10,b/20);
            return value>0?20*Math.Log10(value):double.NegativeInfinity;
        }
        return(Interpolate(_previous.VuLeft,_current.VuLeft,mix),Interpolate(_previous.VuRight,_current.VuRight,mix));
    }
}
