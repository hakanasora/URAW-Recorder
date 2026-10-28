using NWaves.Filters.Fda;
using NWaves.Operations.Convolution;
using NWaves.Windows;
using NAudio.Dsp;
namespace PortableRecorder.Audio;

/// <summary>Immutable, verified design. Every capture session owns fresh convolution state.</summary>
public sealed class CalibrationPlan
{
    public CalibrationProfile? Profile {get;}
    public double[] Kernel {get;}
    public int FftSize {get;}
    public int LatencyFrames=>Kernel.Length==1?0:(FftSize-Kernel.Length)+(Kernel.Length-1)/2;
    public double MaximumFitErrorDb {get;}
    private CalibrationPlan(CalibrationProfile? profile,double[] kernel,int fft,double error)
    {Profile=profile;Kernel=kernel;FftSize=fft;MaximumFitErrorDb=error;}
    public static CalibrationPlan Identity {get;}=new(null,[1],1,0);
    public static double CorrectionDb(CalibrationProfile profile,double hz)
    {
        var p=profile.Points;if(hz<p[0].FrequencyHz||hz>p[^1].FrequencyHz)return 0;
        int lo=0,hi=p.Length-1;while(hi-lo>1){int m=(hi+lo)/2;if(p[m].FrequencyHz>hz)hi=m;else lo=m;}
        double t=(Math.Log(hz)-Math.Log(p[lo].FrequencyHz))/(Math.Log(p[hi].FrequencyHz)-Math.Log(p[lo].FrequencyHz));
        return -(p[lo].ResponseDb+(p[hi].ResponseDb-p[lo].ResponseDb)*t);
    }
    public static CalibrationPlan Prepare(CalibrationProfile profile,int sampleRate)
    {
        if(profile.UnsupportedReason!=null)throw new InvalidDataException(profile.UnsupportedReason);
        if(sampleRate<8000||sampleRate>384000)throw new InvalidDataException("校准采样率超出支持范围 8～384 kHz。");
        if(Math.Max(20,profile.MinimumHz)>=Math.Min(Math.Min(20000,sampleRate/2d),profile.MaximumHz))throw new InvalidDataException("校准曲线与当前有效音频频段没有交集。");
        if(profile.Points.All(p=>p.ResponseDb==0))return new(profile,[1],1,0);
        int fft=1;while(fft<sampleRate*.34)fft*=2;
        int taps=7*fft/8+1,design=fft*4;
        var gains=new double[design/2+1];for(int k=0;k<gains.Length;k++)gains[k]=Math.Pow(10,CorrectionDb(profile,k*(double)sampleRate/design)/20);
        var kernel=DesignFilter.Fir(taps,null!,gains,design,WindowType.Hamming);
        if(kernel.Any(x=>!double.IsFinite(x)))throw new InvalidDataException("校准滤波器包含非有限系数。");
        // An independent FFT implementation checks the actual windowed kernel, not just the target curve.
        var spectrum=new Complex[design];for(int k=0;k<kernel.Length;k++)spectrum[k].X=(float)kernel[k];
        FastFourierTransform.FFT(true,(int)Math.Log2(design),spectrum);
        double worst=0,low=Math.Max(20,profile.MinimumHz),high=Math.Min(20000,Math.Min(profile.MaximumHz,sampleRate/2d));
        var validation=Enumerable.Range(0,2049).Select(j=>low*Math.Pow(high/low,j/2048d))
            .Concat(profile.Points.Select(p=>p.FrequencyHz))
            .Concat(profile.Points.Zip(profile.Points.Skip(1),(a,b)=>Math.Exp((Math.Log(a.FrequencyHz)+Math.Log(b.FrequencyHz))/2)))
            .Where(hz=>hz>=low&&hz<=high);
        foreach(double hz in validation)
        {
            double bin=hz*design/sampleRate;int k=Math.Min((int)bin,design/2-1);double t=bin-k;
            double Mag(int b)=>Math.Sqrt((double)spectrum[b].X*spectrum[b].X+(double)spectrum[b].Y*spectrum[b].Y)*design;
            double actual=20*Math.Log10(Math.Max(1e-20,Mag(k)*(1-t)+Mag(k+1)*t));
            worst=Math.Max(worst,Math.Abs(actual-CorrectionDb(profile,hz)));
        }
        if(worst>.10001)throw new InvalidDataException($"当前采样率下频响拟合误差 {worst:0.000} dB 超过 0.1 dB，未切换校准。");
        return new(profile,kernel,fft,worst);
    }
    public CalibrationStatus Status(AudioFormat format,long revision)=>Profile==null?CalibrationStatus.Off(format.Channels,format.SampleRate,revision):
        new(Profile.Id,Profile.Name,true,format.Channels,LatencyFrames,format.SampleRate,revision,Profile.CapabilityNotice,MaximumFitErrorDb,Profile.MinimumHz,Profile.MaximumHz,Kernel.Length,FftSize);
}

public readonly record struct CalibrationOutput(long SourceStartFrame,int Frames,double SourcePeak);
public sealed class CalibrationProcessor
{
    private readonly CalibrationPlan _plan;
    private readonly OlaBlockConvolver64[] _filters;
    private readonly int _channels;
    private readonly double[] _raw,_gains;
    private long _input,_clock;
    public int LatencyFrames=>_plan.LatencyFrames;
    public CalibrationProcessor(CalibrationPlan plan,int channels)
    {
        _plan=plan;_channels=channels;
        _filters=plan.LatencyFrames==0?[]:Enumerable.Range(0,channels).Select(_=>new OlaBlockConvolver64(plan.Kernel,plan.FftSize)).ToArray();
        _raw=new double[LatencyFrames+1];_gains=new double[LatencyFrames+1];
    }
    public void Reset()
    {
        foreach(var filter in _filters)filter.Reset();
        Array.Clear(_raw);Array.Clear(_gains);_input=0;_clock=0;
    }
    public CalibrationOutput Flush(Span<float> output)=>Process(new float[LatencyFrames*_channels],output,1,drain:true);
    public CalibrationOutput Process(ReadOnlySpan<float> input,Span<float> output,double gain,bool drain=false)
    {
        if(input.Length%_channels!=0||output.Length<input.Length)throw new ArgumentException("校准缓冲区长度／声道数无效。");
        if(!double.IsFinite(gain))throw new InvalidDataException("软件增益不是有限值。");
        int frames=input.Length/_channels,written=0;long start=-1;double peak=0;
        for(int f=0;f<frames;f++)
        {
            int slot=(int)(_clock%_raw.Length);double raw=0;
            if(!drain)
            {
                for(int c=0;c<_channels;c++)raw=Math.Max(raw,Math.Abs(input[f*_channels+c]));
                _raw[slot]=raw;_gains[slot]=gain;_input++;
            }
            long source=_clock-LatencyFrames;bool emit=source>=0&&source<_input;
            int sourceSlot=emit?(int)(source%_raw.Length):0;
            if(emit){if(start<0)start=source;peak=Math.Max(peak,_raw[sourceSlot]);}
            for(int c=0;c<_channels;c++)
            {
                double y=LatencyFrames==0?input[f*_channels+c]:_filters[c].Process(input[f*_channels+c]);
                if(emit)
                {
                    float value=(float)(y*_gains[sourceSlot]);if(!float.IsFinite(value))throw new InvalidDataException("校准处理产生非有限音频，已停止，未自动旁路。");
                    output[written*_channels+c]=value;
                }
            }
            if(emit)written++;_clock++;
        }
        return new(Math.Max(0,start),written,peak);
    }
}


