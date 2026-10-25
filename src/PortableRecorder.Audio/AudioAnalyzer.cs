using NAudio.Dsp;
namespace PortableRecorder.Audio;

public sealed class VuDetector
{
    // Double-precision bilinear second-order meter. Float biquad coefficients at
    // 2 Hz / 48 kHz lose DC gain through cancellation and bias a VU indication.
    private readonly double _b0,_b1,_b2,_a1,_a2;
    private double _z1,_z2;
    private const double ReferenceMean=2/Math.PI*.12589254117941673;
    public double Linear {get;private set;}
    public VuDetector(int rate)
    {
        double k=Math.Tan(Math.PI*2.16406132/rate),q=.61329845;
        double norm=1/(1+k/q+k*k);
        _b0=k*k*norm;_b1=2*_b0;_b2=_b0;
        _a1=2*(k*k-1)*norm;_a2=(1-k/q+k*k)*norm;
    }
    public void Push(float sample)
    {
        double x=Math.Abs(sample),y=_b0*x+_z1;
        _z1=_b1*x-_a1*y+_z2;_z2=_b2*x-_a2*y;
        Linear=Math.Max(0,y);
    }
    public double Vu=>Linear>0?20*Math.Log10(Linear/ReferenceMean):double.NegativeInfinity;
}

public sealed class SpectrumEstimator
{
    private readonly Complex[] _fft;
    private readonly float[] _window;
    private readonly double[] _smoothed;
    private readonly double _coherentGain;
    private bool _initialized;
    public int Size {get;}
    public double[] Frequencies {get;}
    public SpectrumEstimator(int size,int rate)
    {
        Size=size;_fft=new Complex[size];_window=new float[size];_smoothed=new double[size/2+1];
        for(int i=0;i<size;i++)_window[i]=(float)FastFourierTransform.HannWindow(i,size);
        _coherentGain=_window.Sum(x=>(double)x)/size;
        Frequencies=Enumerable.Range(0,size/2+1).Select(k=>k*(double)rate/size).ToArray();
    }
    public float[] Analyze(float[][] channels,double dt,bool smooth=true)
    {
        var powers=new double[Size/2+1];
        foreach(var input in channels)
        {
            for(int i=0;i<Size;i++){_fft[i].X=input[i]*_window[i];_fft[i].Y=0;}
            FastFourierTransform.FFT(true,(int)Math.Log2(Size),_fft);
            for(int k=0;k<powers.Length;k++)
            {
                double scale=(k==0 || k==Size/2?1:2)/_coherentGain;
                powers[k]+=(_fft[k].X*_fft[k].X+_fft[k].Y*_fft[k].Y)*scale*scale/channels.Length;
            }
        }
        var db=new float[powers.Length];
        for(int k=0;k<db.Length;k++)
        {
            double p=powers[k];
            if(smooth && _initialized)
            {
                double coefficient=Math.Exp(-dt/(p>_smoothed[k]?.020:.250));
                p=coefficient*_smoothed[k]+(1-coefficient)*p;
            }
            _smoothed[k]=p;db[k]=p>0?(float)(10*Math.Log10(p)):float.NegativeInfinity;
        }
        _initialized=true;return db;
    }
}

public sealed class AudioAnalyzer:IAudioAnalyzer
{
    private readonly object _gate=new();
    private readonly AudioFormat _format;
    private readonly long _epoch;
    private readonly double[] _rmsPower;
    private int _rmsIndex,_rmsCount;private double _rmsSum;
    private readonly VuDetector[] _vu;
    private readonly float[][] _ring,_work;
    private int _write,_count;
    private long _frame,_lastSpectrum,_lastPitch;
    private readonly SpectrumEstimator _spectrum;
    private readonly PitchDetector _pitch;
    private readonly int _pitchSize;
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _analysisTask;
    private readonly Queue<double> _pitchMedian=new();
    private int _pitchChannel,_candidateChannel=-1;private long _candidateSince;
    private AudioSnapshot _latest;
    public AudioSnapshot Latest=>Volatile.Read(ref _latest);
    public Exception? Failure {get;private set;}
    public AudioAnalyzer(AudioFormat format,long epoch,bool background=true)
    {
        _format=format;_epoch=epoch;_latest=AudioSnapshot.Empty(epoch);
        int size=PowerOfTwo(format.SampleRate*.170);_pitchSize=PowerOfTwo(format.SampleRate*.085);
        _rmsPower=new double[(int)Math.Round(format.SampleRate*.3)];
        _vu=Enumerable.Range(0,Math.Min(format.Channels,2)).Select(_=>new VuDetector(format.SampleRate)).ToArray();
        _ring=Enumerable.Range(0,format.Channels).Select(_=>new float[size]).ToArray();
        _work=Enumerable.Range(0,format.Channels).Select(_=>new float[size]).ToArray();
        _spectrum=new(size,format.SampleRate);_pitch=new(_pitchSize);
        _analysisTask=background?Task.Run(Loop):Task.CompletedTask;
    }
    private static int PowerOfTwo(double n){int size=256;while(size<n)size<<=1;return size;}
    public void Process(ReadOnlySpan<float> samples,double sourcePeak,long endFrame)
    {
        lock(_gate)
        {
            if(Failure!=null)throw new IOException("音频分析已停止。",Failure);
            double peak=0;int channels=_format.Channels;
            for(int i=0;i<samples.Length;i+=channels)
            {
                double power=0;
                for(int c=0;c<channels;c++)
                {
                    float x=samples[i+c];if(!float.IsFinite(x))throw new InvalidDataException("Non-finite processed sample.");
                    peak=Math.Max(peak,Math.Abs(x));power+=(double)x*x/channels;_ring[c][_write]=x;
                    if(c<_vu.Length)_vu[c].Push(x);
                }
                if(_rmsCount==_rmsPower.Length)_rmsSum-=_rmsPower[_rmsIndex];else _rmsCount++;
                _rmsPower[_rmsIndex]=power;_rmsSum+=power;_rmsIndex=(_rmsIndex+1)%_rmsPower.Length;
                _write=(_write+1)%_spectrum.Size;_count=Math.Min(_count+1,_spectrum.Size);
            }
            _frame=endFrame;var old=_latest;
            double output=Db(peak);
            Volatile.Write(ref _latest,old with
            {
                Epoch=_epoch,FramePosition=endFrame,SampleRate=_format.SampleRate,Channels=channels,Valid=true,
                RmsDbfs=Db(Math.Sqrt(Math.Max(0,_rmsSum/_rmsCount))),SourcePeakDbfs=Db(sourcePeak),OutputPeakDbfs=output,
                HeldPeakDbfs=old.HeldPeakDbfs is double held?Math.Max(held,output):output,
                VuLeft=_vu[0].Vu,VuRight=_vu.Length==1?_vu[0].Vu:_vu[1].Vu
            });
        }
    }
    private async Task Loop()
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1d/60));
            while(await timer.WaitForNextTickAsync(_stop.Token))AnalyzeNow();
        }
        catch(OperationCanceledException){}
        catch(Exception error){Failure=error;lock(_gate)Volatile.Write(ref _latest,AudioSnapshot.Empty(_epoch));}
    }
    public void AnalyzeNow()
    {
        long frame;int count;
        lock(_gate)
        {
            frame=_frame;count=_count;
            if(frame==0 || frame-_lastSpectrum<_format.SampleRate/60)return;
            for(int c=0;c<_work.Length;c++)
            {
                Array.Clear(_work[c]);int length=Math.Min(count,_spectrum.Size);
                int start=(_write-length+_spectrum.Size)%_spectrum.Size;
                for(int i=0;i<length;i++)_work[c][_spectrum.Size-length+i]=_ring[c][(start+i)%_spectrum.Size];
            }
        }
        SpectrumFrame? spectrum=null;double? frequency=Latest.FrequencyHz;double clarity=Latest.PitchClarity;long pitchFrame=Latest.PitchFramePosition;
        if(count>=_spectrum.Size)
            spectrum=new(_spectrum.Frequencies,_spectrum.Analyze(_work,(frame-_lastSpectrum)/(double)_format.SampleRate),_spectrum.Size,frame);
        _lastSpectrum=frame;
        if(count>=_pitchSize && frame-_lastPitch>=_format.SampleRate*.020)
        {
            var energies=_work.Select(a=>a.AsSpan(a.Length-_pitchSize).ToArray().Sum(x=>(double)x*x)).ToArray();
            int strongest=Array.IndexOf(energies,energies.Max());
            if(strongest!=_pitchChannel && energies[strongest]>energies[_pitchChannel]*Math.Pow(10,.3))
            {
                if(_candidateChannel!=strongest){_candidateChannel=strongest;_candidateSince=frame;}
                if(frame-_candidateSince>=_format.SampleRate*.2){_pitchChannel=strongest;_pitchMedian.Clear();_candidateChannel=-1;}
            }
            else _candidateChannel=-1;
            (frequency,clarity)=_pitch.Detect(_work[_pitchChannel].AsSpan(_spectrum.Size-_pitchSize),_format.SampleRate);
            if(frequency is double f)
            {
                _pitchMedian.Enqueue(f);while(_pitchMedian.Count>3)_pitchMedian.Dequeue();
                var sorted=_pitchMedian.Order().ToArray();frequency=sorted[sorted.Length/2];
            }
            else _pitchMedian.Clear();
            _lastPitch=frame;pitchFrame=frame;
        }
        lock(_gate)
        {
            var latest=_latest;
            Volatile.Write(ref _latest,latest with{Spectrum=spectrum??latest.Spectrum,FrequencyHz=frequency,PitchClarity=clarity,PitchFramePosition=pitchFrame});
        }
    }
    public void ResetPeak(){lock(_gate)Volatile.Write(ref _latest,_latest with{HeldPeakDbfs=null});}
    public static double Db(double amplitude)=>amplitude>0?20*Math.Log10(amplitude):double.NegativeInfinity;
    public void Dispose(){_stop.Cancel();_analysisTask.GetAwaiter().GetResult();_stop.Dispose();}
}
