namespace PortableRecorder.Audio;

// Stream-relative audio frame positions, not wall-clock/UI timing. Timestamp-error packets cannot
// establish a trustworthy position anchor; they do not imply that the data itself was lost.
public sealed class CaptureContinuity
{
    private bool _first=true,_previousValid;
    private long _previousEnd;
    public (bool Discontinuous,long? Delta) Observe(int frames,long position,int flags)
    {
        if(frames<=0)return(false,null);
        bool valid=(flags&4)==0; // AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR
        long? delta=!_first && valid && _previousValid?position-_previousEnd:null;
        bool gap=!_first && ((flags&1)!=0 || delta is long n && n!=0);
        _first=false;_previousEnd=position+frames;_previousValid=valid;
        return(gap,delta);
    }
}
