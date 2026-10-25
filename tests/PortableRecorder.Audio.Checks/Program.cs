using PortableRecorder.Desktop;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using PortableRecorder.Audio;

var root=Path.GetFullPath(Path.Combine("artifacts","audio-checks",DateTime.Now.ToString("yyyyMMdd-HHmmss")));Directory.CreateDirectory(root);
int checks=0;
void Assert(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
void Near(double actual,double expected,double tolerance,string name)=>Assert(Math.Abs(actual-expected)<=tolerance,$"{name}: {actual} expected {expected} ± {tolerance}");
float[] Sine(int rate,int frames,double frequency,double peak,int channels=1,bool inverted=false)
{
    var samples=new float[frames*channels];for(int i=0;i<frames;i++)for(int c=0;c<channels;c++)samples[i*channels+c]=(float)(peak*Math.Sin(2*Math.PI*frequency*i/rate)*(inverted && c%2==1?-1:1));return samples;
}
async Task Until(Func<bool> condition,string name)
{var elapsed=Stopwatch.StartNew();while(!condition()){if(elapsed.Elapsed.TotalSeconds>5)throw new Exception("Timeout: "+name);await Task.Delay(10);}}
var mono=new AudioFormat(48000,1,32,32,PcmEncoding.Float);
foreach(int bits in new[]{16,24,32})
{
    var format=new AudioFormat(48000,1,bits,bits,PcmEncoding.SignedInteger);var bytes=new byte[3*bits/8];
    if(bits==16){BinaryPrimitives.WriteInt16LittleEndian(bytes,short.MinValue);BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4),short.MaxValue);}
    if(bits==24){bytes[2]=128;bytes[6]=255;bytes[7]=255;bytes[8]=127;}
    if(bits==32){BinaryPrimitives.WriteInt32LittleEndian(bytes,int.MinValue);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8),int.MaxValue);}
    var decoded=new float[3];PcmDecoder.Decode(bytes,decoded,format);
    Near(decoded[0],-1,1e-7,$"PCM{bits} minimum");Near(decoded[1],0,0,$"PCM{bits} zero");Near(decoded[2],1,1d/Math.Pow(2,bits-1),$"PCM{bits} maximum");
    PcmDecoder.Decode([],decoded,format,true);Assert(decoded.All(x=>x==0),"Silent packet");
}
{
    var bytes=new byte[8];BinaryPrimitives.WriteInt32LittleEndian(bytes,int.MinValue);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4),0x7fffff00);
    var decoded=new float[2];PcmDecoder.Decode(bytes,decoded,new(48000,1,32,24,PcmEncoding.SignedInteger));Near(decoded[0],-1,0,"24-in-32 min");Near(decoded[1],8388607/8388608d,0,"24-in-32 max");
}
{
    using var analyzer=new AudioAnalyzer(new(48000,1,16,16,PcmEncoding.SignedInteger),1,false);
    analyzer.Process(new float[]{.5f},32767/32768d,1);
    Assert(analyzer.Latest.InputFullScale && !analyzer.Latest.OutputOverload,"Input rail remains detected after attenuation");
    analyzer.Process(new float[]{2},.5,2);
    Assert(!analyzer.Latest.InputFullScale && analyzer.Latest.OutputOverload,"Software output overrange distinct from input clipping");
}
foreach(int block in new[]{1,127,1024})
{
    using var analyzer=new AudioAnalyzer(mono,7,false);var signal=Sine(48000,48000,1000,Math.Pow(10,-18d/20));
    for(int offset=0;offset<signal.Length;offset+=block){int count=Math.Min(block,signal.Length-offset);analyzer.Process(signal.AsSpan(offset,count),signal.AsSpan(offset,count).ToArray().Max(x=>(double)Math.Abs(x)),offset+count);}
    Near(analyzer.Latest.RmsDbfs,-21.01029996,.05,"Sine RMS");Near(analyzer.Latest.HeldPeakDbfs!.Value,-18,.05,"Sine peak");Near(analyzer.Latest.VuLeft,0,.1,"Steady VU");
    analyzer.ResetPeak();Assert(analyzer.Latest.HeldPeakDbfs==null,"Reset peak");analyzer.Process(new float[480],0,48480);Assert(double.IsNegativeInfinity(analyzer.Latest.HeldPeakDbfs!.Value),"Reaccumulate reset");
}
{
    var detector=new VuDetector(48000);double peak=Math.Pow(10,-18d/20),max=0;var levels=new double[48000];
    for(int i=0;i<levels.Length;i++){detector.Push((float)(peak*Math.Sin(2*Math.PI*1000*i/48000)));levels[i]=detector.Linear;max=Math.Max(max,detector.Linear);}
    var steady=levels[^1];int rise=Array.FindIndex(levels,x=>x>=steady*.99);
    Near(rise/48d,300,30,"VU 99% rise ms");Near((max/steady-1)*100,1.2,.3,"VU overshoot percent");
    int fall=-1;for(int i=0;i<24000;i++){detector.Push(0);if(fall<0 && detector.Linear<=steady*.01)fall=i;}
    Near(fall/48d,300,30,"VU fall ms");Console.WriteLine($"VU rise={rise/48d:0.00}ms overshoot={(max/steady-1)*100:0.000}% fall={fall/48d:0.00}ms");
}
{
    const int n=8192;double frequency=171*48000d/n,peak=.25;var signal=Sine(48000,n,frequency,peak);
    var estimator=new SpectrumEstimator(n,48000);var db=estimator.Analyze([signal],1,false);
    Near(db[171],20*Math.Log10(peak),.05,"FFT coherent gain");Near(estimator.Frequencies[171],frequency,1e-10,"FFT Hz");
    var opposite=signal.Select(x=>-x).ToArray();var stereo=new SpectrumEstimator(n,48000).Analyze([signal,opposite],1,false);Near(stereo[171],db[171],.001,"Antiphase energy average");
    var nonBin=Sine(48000,n,1000,.25);var off=new SpectrumEstimator(n,48000).Analyze([nonBin],1,false);var highest=Array.IndexOf(off,off.Max());Near(estimator.Frequencies[highest],1000,48000d/n,"Non-bin frequency");
    var twin=signal.Zip(Sine(48000,n,500*48000d/n,.125),(a,b)=>a+b).ToArray();var dual=new SpectrumEstimator(n,48000).Analyze([twin],1,false);Near(dual[171],20*Math.Log10(.25),.05,"Dual tone first");Near(dual[500],20*Math.Log10(.125),.05,"Dual tone second");
    var noise=new Random(123);var white=Enumerable.Range(0,n).Select(_=>(float)((noise.NextDouble()*2-1)*.1)).ToArray();Assert(new SpectrumEstimator(n,48000).Analyze([white],1,false).All(float.IsFinite),"Noise spectrum finite");
    var silence=new SpectrumEstimator(n,48000).Analyze([new float[n]],1,false);Assert(silence.All(float.IsNegativeInfinity),"Silence spectrum");
}
{
    const int size=8192;const double amplitude=.2;
    var dc=Enumerable.Repeat((float)amplitude,size).ToArray();
    Near(new SpectrumEstimator(size,48000).Analyze([dc],1,false)[0],20*Math.Log10(amplitude),.05,"DC amplitude single sided");
    var nyquist=Enumerable.Range(0,size).Select(i=>(float)(i%2==0?amplitude:-amplitude)).ToArray();
    Near(new SpectrumEstimator(size,48000).Analyze([nyquist],1,false)[size/2],20*Math.Log10(amplitude),.05,"Nyquist amplitude single sided");
}
double maximumPitchError=0;
foreach(int rate in new[]{44100,48000,96000})
foreach(double frequency in new[]{27.5,32.7032,110,123.47,440,1000,2093})
{
    int size=256;while(size<rate*.085)size<<=1;
    var detector=new PitchDetector(size);var result=detector.Detect(Sine(rate,size,frequency,.2),rate);
    Assert(result.Hertz!=null,$"Missing pitch {rate}/{frequency} clarity={result.Clarity}");
    double cents=1200*Math.Log2(result.Hertz!.Value/frequency);maximumPitchError=Math.Max(maximumPitchError,Math.Abs(cents));Near(cents,0,5,$"Pitch cents {rate}/{frequency}");
}
{
    var detector=new PitchDetector(4096);Assert(detector.Detect(new float[4096],48000).Hertz==null,"Silent pitch");
    var noise=new Random(123);Assert(detector.Detect(Enumerable.Range(0,4096).Select(_=>(float)(noise.NextDouble()-.5)).ToArray(),48000).Hertz==null,"Noise pitch");
    var harmonic=Sine(48000,4096,220,.1).Zip(Sine(48000,4096,440,.2),(a,b)=>a+b).Zip(Sine(48000,4096,660,.1),(a,b)=>a+b).ToArray();
    var detected=detector.Detect(harmonic,48000);Assert(detected.Hertz!=null,"Harmonic pitch");Near(1200*Math.Log2(detected.Hertz!.Value/220),0,5,"Harmonic cents");
}
{
    string path=Path.Combine(root,"float-headroom.wav");var values=new float[]{0,2.5f,-3f,.25f};
    var writer=new FloatWaveWriter(path,mono,64);Assert(writer.TryWrite(values),"WAV enqueue");await writer.CompleteAsync();
    using var reader=new WaveFileReader(path);Assert(reader.WaveFormat.Channels==1 && reader.WaveFormat.BitsPerSample==32,"WAV format mono");
    var read=new float[values.Length];reader.ToSampleProvider().Read(read);for(int i=0;i<values.Length;i++)Near(read[i],values[i],0,"WAV preserves float headroom");
    Assert(Encoding.ASCII.GetString(File.ReadAllBytes(path),0,4)=="RF64","RF64 promotion");Assert(writer.WrittenFrames==4,"WAV frames");
}
{
    var factory=new FakeFactory();await using var engine=new AudioEngine(factory);
    await engine.SelectDeviceAsync(factory.Device);Assert(!factory.Current!.Info.Exclusive && factory.Current.Info.Raw,"Startup shared RAW");
    factory.Push(Sine(48000,480,1000,.1));await Until(()=>engine.Latest.Valid,"initial frame");
    engine.SetGain(6);await engine.StartRecordingAsync(root,RecordingMode.Shared);
    string path=engine.Status.FilePath!;factory.Push(new float[480].Select(_=>.1f).ToArray());await Until(()=>engine.WrittenFrames==480,"first write");
    await engine.PauseAsync(true);factory.Push(new float[480]);await Task.Delay(40);Assert(engine.WrittenFrames==480,"Pause excludes samples");
    await engine.PauseAsync(false);factory.Push(new float[240]);await Until(()=>engine.WrittenFrames==720,"resume write");
    await engine.StopRecordingAsync();Assert(engine.WrittenFrames==720,"stop exact tail");Near(engine.RecordedTime.TotalSeconds,.015,1e-9,"sample clock");
    using var reader=new WaveFileReader(path);var first=new float[1];reader.ToSampleProvider().Read(first);Near(first[0],.1*Math.Pow(10,6d/20),1e-6,"Recorded gain");Assert(reader.Length==720*4,"Pause file length");
    await engine.StartRecordingAsync(root,RecordingMode.Exclusive);Assert(factory.Current!.Info.Exclusive,"Exclusive only on record");
    factory.Push(new float[480]);await Until(()=>engine.WrittenFrames==480,"exclusive write");await engine.StopRecordingAsync();Assert(!factory.Current!.Info.Exclusive,"Restore shared");
    factory.DenyExclusive=true;bool denied=false;try{await engine.StartRecordingAsync(root,RecordingMode.Exclusive);}catch(IOException){denied=true;}
    Assert(denied && !factory.Current!.Info.Exclusive && engine.Status.State==AudioRunState.Monitoring,"Exclusive failure restore");
}
{
    var factory=new FakeFactory{DenySharedRaw=true};await using var engine=new AudioEngine(factory);
    await engine.SelectDeviceAsync(factory.Device);Assert(engine.RawDecisionPending,"RAW decision required");
    await engine.DecideRawFallbackAsync(true);Assert(!factory.Current!.Info.Raw,"Explicit ordinary monitoring");
    bool denied=false;try{await engine.StartRecordingAsync(root,RecordingMode.Shared);}catch(IOException){denied=true;}
    Assert(denied && !factory.Current!.Info.Raw && !engine.RawDecisionPending,"Consent never permits ordinary recording");
}
{
    var factory=new FakeFactory();await using var engine=new AudioEngine(factory);await engine.SelectDeviceAsync(factory.Device);
    factory.Push(new float[480]);await Until(()=>engine.Latest.Valid,"fault preframe");factory.Push(new float[480],true);
    await Until(()=>engine.Status.State==AudioRunState.Faulted,"discontinuity failure");Assert(!engine.Latest.Valid,"No stale snapshot after fault");
}
{
    var factory=new FakeFactory();await using var engine=new AudioEngine(factory);await engine.SelectDeviceAsync(factory.Device);
    factory.Push(new float[144001]);await Until(()=>engine.Status.State==AudioRunState.Faulted,"queue overflow");Assert(!engine.Latest.Valid,"No false data on overflow");
}
// Interleaved multichannel samples retain all channels, including float headroom.
{
    var format=new AudioFormat(44100,4,32,32,PcmEncoding.Float);
    float[] values=[.1f,-.2f,1.25f,-2f,.4f,-.5f,.6f,-.7f];
    var writer=new FloatWaveWriter(Path.Combine(root,"multichannel.wav"),format);
    Assert(writer.TryWrite(values),"Multichannel enqueue");await writer.CompleteAsync();
    using var reader=new WaveFileReader(writer.Path);var samples=new float[values.Length];reader.ToSampleProvider().Read(samples);
    Assert(reader.WaveFormat.Channels==4 && reader.WaveFormat.SampleRate==44100,"Native multichannel format retained");
    Assert(values.SequenceEqual(samples),"Interleaved channels unchanged");
    var decoded=new float[values.Length];PcmDecoder.Decode(MemoryMarshal.AsBytes(values.AsSpan()),decoded,format);Assert(values.SequenceEqual(decoded),"Float interleaved decoder");
}
{
    var factory=new FakeFactory{DenySharedRaw=true};await using var engine=new AudioEngine(factory);
    await engine.SelectDeviceAsync(factory.Device);await engine.DecideRawFallbackAsync(false);
    Assert(engine.Status.State==AudioRunState.Stopped && !engine.Latest.Valid,"RAW dialog close stops monitoring");
    await engine.SelectDeviceAsync(factory.Device);await engine.DecideRawFallbackAsync(true);
    await engine.SelectDeviceAsync(new("other","Other device"));Assert(engine.RawDecisionPending,"Ordinary consent expires on device change");
}
{
    var factory=new FakeFactory();await using var engine=new AudioEngine(factory);
    await engine.SelectDeviceAsync(factory.Device);var old=factory.Current!;
    factory.Push(new float[480].Select(_=>.8f).ToArray());await Until(()=>engine.Latest.Valid,"old epoch valid");
    await engine.StartRecordingAsync(root,RecordingMode.Exclusive);
    factory.Push(new float[480]);await Until(()=>engine.WrittenFrames==480,"new epoch record");
    old.TriggerFault();await Task.Delay(40);Assert(engine.Status.State==AudioRunState.Recording,"Stale capture error cannot end new session");
    Near(engine.Latest.HeldPeakDbfs!.Value,20*Math.Log10(.8),.01,"Historical peak survives capture switch");
    await engine.ResetPeakAsync();factory.Push(new float[480]);await Until(()=>engine.Latest.FramePosition>=960,"reset sample boundary");
    Assert(double.IsNegativeInfinity(engine.Latest.HeldPeakDbfs!.Value),"Reset clears history across capture epochs");
    await engine.StopRecordingAsync();
    factory.Push(new float[480]);await Until(()=>engine.Latest.Valid,"shared after stop");
    Assert(double.IsNegativeInfinity(engine.Latest.RmsDbfs),"Stopped shared stream has real silence");
}
{
    var factory=new FakeFactory();var disk=new FailingWriter();await using var engine=new AudioEngine(factory,writerFactory:(_,_)=>disk);
    await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(root,RecordingMode.Shared);
    factory.Push(new float[480]);await Until(()=>engine.WrittenFrames==480,"disk fixture first block");
    await engine.PauseAsync(true);disk.Fail();factory.Push(new float[480]);
    await Until(()=>engine.Status.State==AudioRunState.Faulted,"Disk failure while paused");
    Assert(engine.Status.Partial && !engine.Latest.Valid,"Failed file is partial, no stale data");
}
{
    var factory=new FakeFactory();var engine=new AudioEngine(factory);
    await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(root,RecordingMode.Exclusive);
    string path=engine.Status.FilePath!;factory.Push(Sine(48000,1200,440,.1));
    await engine.DisposeAsync();
    using var file=new WaveFileReader(path);Assert(file.Length==1200*4,"Closing active recording drains final samples and header");
    Assert(factory.Current!.Closed,"Closing releases exclusive capture");
}
{
    var factory=new FakeFactory();var writer=new FailingWriter{Reject=true};
    await using var engine=new AudioEngine(factory,writerFactory:(_,_)=>writer);
    await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(root,RecordingMode.Shared);factory.Push(new float[480]);
    await Until(()=>engine.Status.State==AudioRunState.Faulted,"Recording queue rejects data");
    Assert(engine.Status.Partial && !engine.Latest.Valid,"Recording overflow stops, never silently drops");
}
// Discard is paused-only, deletes exactly the current file, and releases exclusive capture.
{
    var factory=new FakeFactory();await using var engine=new AudioEngine(factory);
    await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(root,RecordingMode.Exclusive);
    string discarded=engine.Status.FilePath!;string keep=Path.Combine(root,"keep-existing.wav");File.WriteAllText(keep,"keep");
    factory.Push(new float[480]);await Until(()=>engine.WrittenFrames==480,"discard first write");
    bool rejected=false;try{await engine.DiscardRecordingAsync();}catch(InvalidOperationException){rejected=true;}
    Assert(rejected && File.Exists(discarded),"Cannot discard an unpaused recording");
    await engine.PauseAsync(true);await engine.DiscardRecordingAsync();
    Assert(!File.Exists(discarded) && File.ReadAllText(keep)=="keep","Discard removes only owned current file");
    Assert(engine.WrittenFrames==0 && engine.Status.State==AudioRunState.Monitoring && !factory.Current!.Info.Exclusive,"Discard clears clock and restores shared monitoring");
}
{
    var factory=new FakeFactory();await using var engine=new AudioEngine(factory);
    var second=new AudioDevice("second","Second microphone");var third=new AudioDevice("third","Third microphone");
    await engine.ReconcileDevicesAsync([factory.Device]);
    Assert(engine.SelectedDevice!.Id==factory.Device.Id,"Initial topology chooses default");
    await engine.ReconcileDevicesAsync([factory.Device,second]);Assert(engine.SelectedDevice!.Id==second.Id,"New device automatically selected while idle");
    await engine.StartRecordingAsync(root,RecordingMode.Exclusive);var current=factory.Current!;string saved=engine.Status.FilePath!;
    factory.Push(new float[480]);await Until(()=>engine.WrittenFrames==480,"hotplug recording");
    await engine.ReconcileDevicesAsync([factory.Device,second,third]);
    Assert(engine.SelectedDevice!.Id==second.Id && ReferenceEquals(current,factory.Current),"New device cannot switch ongoing recording");
    await engine.ReconcileDevicesAsync([factory.Device,third]);
    Assert(engine.SelectedDevice!.Id!=second.Id && !factory.Current!.Info.Exclusive && engine.Status.State==AudioRunState.Monitoring,"Removed recording device finalizes and restores shared on remaining input");
    using(var reader=new WaveFileReader(saved))Assert(reader.Length==480*4,"Removed device preserves completed samples");
    await engine.ReconcileDevicesAsync([]);Assert(engine.SelectedDevice==null && !engine.Latest.Valid,"No input leaves invalid readings");
    await engine.ReconcileDevicesAsync([second]);Assert(engine.SelectedDevice!.Id==second.Id && engine.Status.State==AudioRunState.Monitoring,"New microphone recovers without manual refresh");
}
{
    string settings=Path.Combine(root,"preferences.json");
    Assert(RecordingPreferences.Load(settings)==RecordingMode.Exclusive,"Missing mode preference uses default");
    RecordingPreferences.Save(RecordingMode.Shared,settings);Assert(RecordingPreferences.Load(settings)==RecordingMode.Shared,"Shared choice survives process reload");
    RecordingPreferences.Save(RecordingMode.Exclusive,settings);Assert(RecordingPreferences.Load(settings)==RecordingMode.Exclusive,"Exclusive choice survives process reload");
    File.WriteAllText(settings,"{broken");Assert(RecordingPreferences.Load(settings)==RecordingMode.Exclusive,"Invalid preference safely falls back");
    Assert(!Directory.GetFiles(root,"*.tmp").Any(),"Atomic preference save cleans temporary file");
}
{
    var motion=new MeterMotion();var low=AudioSnapshot.Empty(10) with{Valid=true,SampleRate=48000,FramePosition=1440,VuLeft=-20,VuRight=-10};
    motion.Push(low,0);var high=low with{FramePosition=2880,VuLeft=0,VuRight=3};motion.Push(high,.030);
    Near(motion.Read(.030,false).Left,-20,.001,"Visual interpolation begins at previous sample");
    double last=-20;
    for(int i=0;i<=4;i++)
    {
        double value=motion.Read(.030+i/120d,false).Left;
        Assert(value>=last && value<=0,"120 Hz interpolation is monotonic, no overshoot");last=value;
    }
    Near(motion.Read(.060,false).Left,0,.001,"Visual interpolation reaches measured target");
    Near(motion.Read(.035,true).Left,0,0,"Reduced motion uses measured target");
    motion.Push(high with{Epoch=11,VuLeft=-8},.070);Near(motion.Read(.070,false).Left,-8,.001,"Epoch change never blends old device");
    motion.Push(AudioSnapshot.Empty(11),.080);Assert(double.IsNegativeInfinity(motion.Read(.080,false).Left),"Failure clears interpolated pointer");
}
{
    var motion=new CentsMotion();motion.Advance(1,69,0,0,false);
    double previous=0;
    for(int i=0;i<12;i++)
    {
        double position=motion.Advance(1,69.3,30,1d/240,false)!.Value;
        Assert(position>previous && position<30,"Cents ruler advances each 240 Hz frame without overshoot");previous=position;
    }
    Near(motion.Advance(1,70.49,49,.004,false)!.Value,49,0,"New note resets ruler");
    Near(motion.Advance(1,70.51,-49,.004,false)!.Value,-49,0,"No sweeping across note boundary");
    Assert(motion.Advance(1,null,null,.004,false)==null,"Invalid pitch clears ruler immediately");
    Near(motion.Advance(2,60.2,20,.004,false)!.Value,20,0,"New session starts directly");
    Near(motion.Advance(2,60.3,30,.004,true)!.Value,30,0,"Reduced motion bypasses interpolation");
    var slower=new CentsMotion();var faster=new CentsMotion();slower.Advance(1,69,0,0,false);faster.Advance(1,69,0,0,false);
    double a=0,b=0;for(int i=0;i<6;i++)a=slower.Advance(1,69.2,20,1d/60,false)!.Value;
    for(int i=0;i<24;i++)b=faster.Advance(1,69.2,20,1d/240,false)!.Value;
    Near(a,b,1e-10,"Cents response independent of refresh rate");
    Assert(DisplayRefreshRate.NativeLayoutsValid,"DisplayConfig native layouts");
    var display=DisplayRefreshRate.Read(IntPtr.Zero);
    Console.WriteLine($"Read-only display probe: {display.Device}, {display.Hertz?.ToString("F3")??"unknown"} Hz ({display.Source})");
    Assert(display.Hertz==null || double.IsFinite(display.Hertz.Value) && display.Hertz>0,"System refresh probe valid or explicit compositor fallback");
}
Console.WriteLine($"Maximum pure-tone pitch error: {maximumPitchError:0.0000} cents");
Console.WriteLine($"PASS: {checks} targeted assertions. No microphone, desktop, or browser used. Fixtures: {root}");

sealed class FakeFactory:ICaptureFactory
{
    public AudioDevice Device {get;}=new("fake","Deterministic input",true);
    public bool DenySharedRaw,DenyExclusive;
    public FakeCapture? Current;
    public IReadOnlyList<AudioDevice> Enumerate()=>[Device];
    public Task<IAudioCaptureService> OpenAsync(AudioDevice device,bool exclusive,bool raw,long epoch,Func<CapturePacket,bool> packet,Action<Exception> fault)
    {
        if(exclusive && DenyExclusive)throw new IOException("Exclusive denied");
        if(!exclusive && raw && DenySharedRaw)throw new RawUnavailableException("RAW unsupported");
        Current=new(new(device,new(48000,1,32,32,PcmEncoding.Float),exclusive,raw,epoch),packet,fault);return Task.FromResult<IAudioCaptureService>(Current);
    }
    public void Push(float[] samples,bool gap=false)=>Current!.Push(samples,gap);
}
sealed class FakeCapture(CaptureInfo info,Func<CapturePacket,bool> packet,Action<Exception> fault):IAudioCaptureService
{
    public CaptureInfo Info=>info;long _frame;bool _closed;
    public bool Closed=>_closed;
    public void TriggerFault()=>fault(new IOException("Old session fault"));
    public void Push(float[] data,bool gap)
    {
        if(_closed)throw new InvalidOperationException("Closed capture");
        var bytes=ArrayPool<byte>.Shared.Rent(data.Length*4);MemoryMarshal.AsBytes(data.AsSpan()).CopyTo(bytes);
        var block=new CapturePacket(bytes,data.Length*4,data.Length/info.Format.Channels,_frame,false,gap);_frame+=block.Frames;
        if(!packet(block)){block.Dispose();fault(new IOException("queue overflow"));}
    }
    public ValueTask DisposeAsync(){_closed=true;return ValueTask.CompletedTask;}
}

sealed class FailingWriter:IRecordingWriter
{
    public string Path=>"injected-disk-failure.wav";
    public long WrittenFrames {get;private set;}
    public Exception? Failure {get;private set;}
    public bool Reject;
    public bool TryWrite(ReadOnlySpan<float> samples){if(Failure!=null || Reject)return false;WrittenFrames+=samples.Length;return true;}
    public void Fail()=>Failure=new IOException("Injected disk failure");
    public Task CompleteAsync()=>Failure==null?Task.CompletedTask:Task.FromException(Failure);
    public ValueTask DisposeAsync()=>new(CompleteAsync());
}
