// Adapted from pitchy v4.1.0, Copyright 2018-2021 Ian Johnson (MIT).
// See third_party/pitchy/LICENSE and index.js. No JavaScript runtime is used.
using NAudio.Dsp;
namespace PortableRecorder.Audio;

public sealed class PitchDetector
{
    private readonly int _length,_fftLength,_power;
    private readonly Complex[] _fft;
    private readonly double[] _nsdf;
    private readonly List<int> _maxima=[];
    public PitchDetector(int length)
    {
        _length=length;_fftLength=1;while(_fftLength<2*length)_fftLength<<=1;
        _power=(int)Math.Log2(_fftLength);_fft=new Complex[_fftLength];_nsdf=new double[length];
    }
    public (double? Hertz,double Clarity) Detect(ReadOnlySpan<float> input,int sampleRate)
    {
        if(input.Length!=_length)throw new ArgumentException("Pitch window size mismatch.");
        double mean=0;foreach(var x in input)mean+=x;mean/=input.Length;
        double energy=0;
        Array.Clear(_fft);
        for(int i=0;i<_length;i++){float x=(float)(input[i]-mean);energy+=x*x;_fft[i].X=x;}
        if(energy/_length<1e-8)return(null,0); // -80 dBFS RMS, analysis-only gate.
        FastFourierTransform.FFT(true,_power,_fft);
        for(int i=0;i<_fftLength;i++){var x=_fft[i];_fft[i].X=(x.X*x.X+x.Y*x.Y)*_fftLength;_fft[i].Y=0;}
        FastFourierTransform.FFT(false,_power,_fft);
        double m=2*energy;
        for(int i=0;i<_length;i++)
        {
            _nsdf[i]=m>1e-20?2*_fft[i].X/m:0;
            double a=input[i]-mean,b=input[_length-i-1]-mean;m-=a*a+b*b;
        }
        _maxima.Clear();bool looking=false;int index=-1;double maximum=double.NegativeInfinity;
        for(int i=1;i<_length-1;i++)
        {
            if(_nsdf[i-1]<=0 && _nsdf[i]>0){looking=true;index=i;maximum=_nsdf[i];}
            else if(_nsdf[i-1]>0 && _nsdf[i]<=0)
            {looking=false;if(index>0 && index>=sampleRate/2093d-1 && index<=sampleRate/27.5+1)_maxima.Add(index);index=-1;}
            else if(looking && _nsdf[i]>maximum){index=i;maximum=_nsdf[i];}
        }
        if(_maxima.Count==0)return(null,0);
        double high=_maxima.Max(i=>_nsdf[i]);
        int peak=_maxima.First(i=>_nsdf[i]>=.9*high);
        double y0=_nsdf[peak-1],y1=_nsdf[peak],y2=_nsdf[peak+1];
        double denominator=y0-2*y1+y2;
        double delta=Math.Abs(denominator)>1e-12?.5*(y0-y2)/denominator:0;
        double clarity=Math.Clamp(y1-.25*(y0-y2)*delta,0,1);
        double hz=sampleRate/(peak+delta);
        return clarity>=.9 && hz>=27.5*Math.Pow(2,-.1/1200) && hz<=2093*Math.Pow(2,.1/1200)?(hz,clarity):(null,clarity);
    }
}
