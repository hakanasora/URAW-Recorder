namespace PortableRecorder.Desktop;

// Only the visual ruler moves. The displayed Hz/note/cents measurement and saved audio are untouched.
internal sealed class CentsMotion
{
    private long _epoch=-1;
    private int? _note;
    private double? _position;
    public void Reset(){_epoch=-1;_note=null;_position=null;}
    public double? Advance(long epoch,double? midi,double? cents,double elapsed,bool reducedMotion)
    {
        if(midi is not double pitch || cents is not double target || !double.IsFinite(target))
        {_epoch=epoch;_note=null;_position=null;return null;}
        int note=(int)Math.Round(pitch);
        // A different note resets its own tuning ruler; do not animate across the +/-50-cent wrap.
        if(epoch!=_epoch || note!=_note || _position==null || reducedMotion)_position=target;
        else _position+= (target-_position.Value)*(1-Math.Exp(-Math.Max(0,elapsed)/.035));
        _epoch=epoch;_note=note;return _position;
    }
}
