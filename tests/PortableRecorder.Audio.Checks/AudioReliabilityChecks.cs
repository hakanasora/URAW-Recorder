using System.Buffers;
using System.Runtime.InteropServices;
using NAudio.Wave;
using PortableRecorder.Audio;

internal static class AudioReliabilityChecks
{
    public static async Task Run(string root,Action<bool,string> check)
    {
        string folder=Path.Combine(root,"audio-reliability");Directory.CreateDirectory(folder);
        async Task Until(Func<bool> ready,string message)
        {
            var deadline=DateTime.UtcNow.AddSeconds(5);
            while(!ready()){if(DateTime.UtcNow>deadline)throw new TimeoutException(message);await Task.Delay(5);}
        }
        AudioEngine Engine(ReliabilityFactory factory,string name,Func<string,AudioFormat,RecordingEncoding,IRecordingWriter>? writer=null)
            =>new(factory,new CalibrationLibrary(Path.Combine(folder,name,"calibrations")),writer,new InputFormatPreferences(Path.Combine(folder,name,"formats.json")));

        var factory=new ReliabilityFactory();
        await using(var engine=Engine(factory,"repeat"))
        {
            await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(folder,RecordingMode.Shared);
            string file=engine.Status.FilePath!;factory.Push(new float[480]);await Until(()=>engine.WrittenFrames==480,"Initial frames");
            await engine.PauseAsync(true);await engine.PauseAsync(true);
            factory.Push(new float[240]);await engine.PauseAsync(false);await Task.WhenAll(engine.PauseAsync(false),engine.PauseAsync(false));
            factory.Push(new float[480]);await Until(()=>engine.WrittenFrames>=960,"Continued frames");await engine.StopRecordingAsync();
            using var wave=new WaveFileReader(file);
            check(wave.Length==960*4,"Repeated pause/continue commands cannot create overlapping recording intervals");
            await engine.PauseAsync(false);check(engine.WrittenFrames==960,"Continue after stop is a no-op");
        }

        factory=new ReliabilityFactory();var existingWriter=new ReliabilityWriter(Path.Combine(folder,"duplicate-start.wav"));
        await using(var engine=Engine(factory,"duplicate-start",(_,_,_)=>existingWriter))
        {
            await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(folder,RecordingMode.Shared);
            factory.Push(new float[200]);await Until(()=>engine.WrittenFrames==200,"Initial duplicate-start frames");
            bool rejected=false;try{await engine.StartRecordingAsync(folder,RecordingMode.Shared);}catch(InvalidOperationException){rejected=true;}
            factory.Push(new float[200]);await Task.Delay(30);
            check(rejected&&engine.WrittenFrames==400&&existingWriter.CompletionCalls==0,"Rejected duplicate Start must not finalize or detach the ongoing recording");
            await engine.StopRecordingAsync();
        }

        factory=new ReliabilityFactory();int attempts=0;
        await using(var engine=Engine(factory,"header-failure",(path,_,_)=>
        {
            attempts++;using(var file=new FileStream(path,FileMode.CreateNew))file.WriteByte(1);
            throw new IOException("injected-header-disk-error");
        }))
        {
            await engine.SelectDeviceAsync(factory.Device);Exception? failure=null;
            try{await engine.StartRecordingAsync(folder,RecordingMode.Shared);}catch(IOException error){failure=error;}
            check(attempts==1&&failure?.Message.Contains("injected-header-disk-error")==true,"Header IO failure is not retried as a name collision");
        }

        factory=new ReliabilityFactory();attempts=0;string? collision=null;
        await using(var engine=Engine(factory,"name-collision",(path,_,_)=>
        {
            if(++attempts==1)
            {
                collision=path;File.WriteAllText(path,"existing-recording");
                // Match the Windows CreateNew HRESULT, rather than any IOException.
                using var existing=new FileStream(path,FileMode.CreateNew);
            }
            return new ReliabilityWriter(path);
        }))
        {
            await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(folder,RecordingMode.Shared);
            check(attempts==2&&File.ReadAllText(collision!)=="existing-recording"&&engine.Status.FilePath!=collision,"Real CreateNew collision retries without replacing the existing file");
            await engine.StopRecordingAsync();
        }

        foreach(var codec in Enum.GetValues<RecordingCodec>())
        {
            factory=new ReliabilityFactory();RecordingEncoding? observed=null;
            var writer=new ReliabilityWriter(Path.Combine(folder,"injected-"+codec));
            await using var engine=Engine(factory,"factory-"+codec,(_,_,encoding)=>{observed=encoding;return writer;});
            var requested=new RecordingEncoding(codec,codec==RecordingCodec.Mp3?2:5);
            await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(folder,RecordingMode.Shared,requested);
            factory.Push(new float[200]);await engine.PauseAsync(true);await engine.PauseAsync(true);
            await Task.WhenAll(engine.PauseAsync(false),engine.PauseAsync(false));factory.Push(new float[200]);
            await engine.StopRecordingAsync();
            check(observed==requested&&writer.WrittenFrames==400,"One writer injection boundary and idempotent commands cover "+codec);
        }

        factory=new ReliabilityFactory();var failedWriter=new ReliabilityWriter(Path.Combine(folder,"failed-writer.wav"));
        await using(var engine=Engine(factory,"failure-detail",(_,_,_)=>failedWriter))
        {
            var diagnostics=new List<string>();engine.Diagnostic+=message=>{lock(diagnostics)diagnostics.Add(message);};
            await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(folder,RecordingMode.Shared);
            failedWriter.WriteFailure=new IOException("injected-overload-use-WAV; recovery: owned.encoding");
            failedWriter.CompletionFailure=new IOException("injected-secondary-finalize-error");
            factory.Push(new float[480]);await Until(()=>engine.Status.State==AudioRunState.Faulted,"Writer fault finalization");
            check(engine.Status.Message.Contains("injected-overload-use-WAV")&&engine.Status.Message.Contains("injected-secondary-finalize-error"),"Fault state preserves primary writer reason and secondary finalization error");
            check(engine.Status.Partial&&!engine.Latest.Valid,"Writer fault marks partial and clears old measurements");
            lock(diagnostics)check(diagnostics.Any(s=>s.Contains("injected-overload-use-WAV")),"Full writer fault is available in diagnostics");
        }

        factory=new ReliabilityFactory();var releaseEngine=Engine(factory,"release-failure");
        await releaseEngine.SelectDeviceAsync(factory.Device);factory.Push(new float[480]);await Until(()=>releaseEngine.Latest.Valid,"Live snapshot before cleanup");
        factory.Current!.FailDispose=true;Exception? releaseFailure=null;
        try{await releaseEngine.DisposeAsync();}catch(Exception error){releaseFailure=error;}
        check(releaseFailure!=null&&!releaseEngine.Latest.Valid,"Capture disposal failure still detaches and disposes processing pipeline");
        factory.Push(new float[1]);check(!factory.Current!.LastSubmitAccepted,"Capture release failure still completes the old processing queue");
        await releaseEngine.DisposeAsync();check(factory.Current.DisposeCalls==1,"Repeated engine disposal does not release capture twice");

        factory=new ReliabilityFactory();
        await using(var engine=Engine(factory,"stale-fault"))
        {
            await engine.SelectDeviceAsync(factory.Device);var old=factory.Current!;
            await engine.SelectDeviceAsync(new("replacement","Replacement fake"));factory.Push(new float[480]);await Until(()=>engine.Latest.Valid,"New capture snapshot");
            old.TriggerFault();await Task.Delay(30);
            check(engine.Status.State==AudioRunState.Monitoring&&engine.Status.Capture!.Device.Id=="replacement"&&engine.Latest.Valid,"Late fault from released capture cannot invalidate a replacement session");
        }

        factory=new ReliabilityFactory();var closeWriter=new ReliabilityWriter(Path.Combine(folder,"close-failure.wav")){CompletionFailure=new IOException("injected-close-writer-error")};
        var closeEngine=Engine(factory,"composite-close",(_,_,_)=>closeWriter);
        await closeEngine.SelectDeviceAsync(factory.Device);await closeEngine.StartRecordingAsync(folder,RecordingMode.Shared);factory.Push(new float[200]);
        await Until(()=>closeEngine.WrittenFrames==200,"Composite shutdown initial data");factory.Current!.FailDispose=true;
        Exception? closeFailure=null;try{await closeEngine.DisposeAsync();}catch(Exception error){closeFailure=error;}
        check(closeFailure!=null&&AudioEngine.Friendly(closeFailure).Contains("injected-close-writer-error")&&AudioEngine.Friendly(closeFailure).Contains("injected-capture-disposal-error"),"Shutdown preserves recording failure and independent capture cleanup failure");
        factory.Push(new float[1]);check(!factory.Current.LastSubmitAccepted&&!closeEngine.Latest.Valid,"Composite shutdown still stops all processing");
        await closeEngine.DisposeAsync();

        factory=new ReliabilityFactory();
        await using(var engine=Engine(factory,"summary-failure"))
        {
            await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(folder,RecordingMode.Shared);
            string file=engine.Status.FilePath!;Directory.CreateDirectory(engine.GetRecordingMetadataPath(file));
            factory.Push(new float[200]);await engine.StopRecordingAsync();
            using var wave=new WaveFileReader(file);
            check(wave.Length==800&&!engine.Status.Partial&&engine.Status.Message.Contains("校准摘要写入失败"),"Failed sidecar does not claim audio failure or silently disappear");
        }

        factory=new ReliabilityFactory();
        await using(var engine=Engine(factory,"saved-release-failure"))
        {
            await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(folder,RecordingMode.Exclusive);
            string file=engine.Status.FilePath!;factory.Push(new float[200]);factory.Current!.FailDispose=true;
            bool failed=false;try{await engine.StopRecordingAsync();}catch(IOException){failed=true;}
            using var wave=new WaveFileReader(file);
            using var summary=System.Text.Json.JsonDocument.Parse(File.ReadAllText(engine.GetRecordingMetadataPath(file)));
            check(failed&&wave.Length==800&&!engine.Status.Partial&&summary.RootElement.GetProperty("Completed").GetBoolean(),"Capture cleanup failure preserves successful audio and Completed sidecar");
            check(engine.Status.Message.Contains("恢复监看失败")&&engine.Status.Message.Contains("injected-capture-disposal-error"),"Saved-audio cleanup failure remains actionable in the service status");
        }

        factory=new ReliabilityFactory();var gainWriter=new ReliabilityWriter(Path.Combine(folder,"gain.wav"));
        await using(var engine=Engine(factory,"gain",(_,_,_)=>gainWriter))
        {
            await engine.SelectDeviceAsync(factory.Device);engine.SetGain(6);
            foreach(double gain in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity})
            {
                bool rejected=false;try{engine.SetGain(gain);}catch(ArgumentOutOfRangeException){rejected=true;}
                check(rejected,"Non-finite service gain is rejected before replacing valid gain");
            }
            await engine.StartRecordingAsync(folder,RecordingMode.Shared);factory.Push(Enumerable.Repeat(.125f,480).ToArray());
            await Until(()=>engine.WrittenFrames==480,"Valid gain survives invalid input");await engine.StopRecordingAsync();
            check(gainWriter.Samples.All(x=>Math.Abs(x-.125*Math.Pow(10,6d/20))<1e-6),"Invalid gain retains previous gain and working recording path");
        }
    }

    private sealed class ReliabilityFactory:ICaptureFactory
    {
        public AudioDevice Device {get;}=new("reliability","Fake reliability input",true);
        public ReliabilityCapture? Current;
        public IReadOnlyList<AudioDevice> Enumerate()=>[Device];
        public Task<InputFormatOptions> QueryFormatsAsync(AudioDevice device)=>Task.FromResult(new InputFormatOptions(new(48000,1,32,32,PcmEncoding.Float),[]));
        public Task<IAudioCaptureService> OpenAsync(AudioDevice device,bool exclusive,bool raw,long epoch,Func<CapturePacket,bool> packet,Action<Exception> fault,AudioFormat? preferredFormat=null)
        {Current=new(new(device,new(48000,1,32,32,PcmEncoding.Float),exclusive,raw,epoch),packet,fault);return Task.FromResult<IAudioCaptureService>(Current);}
        public void Push(float[] data)=>Current!.Push(data);
    }
    private sealed class ReliabilityCapture(CaptureInfo info,Func<CapturePacket,bool> submit,Action<Exception> fault):IAudioCaptureService
    {
        private long _position;
        public CaptureInfo Info=>info;
        public bool FailDispose;
        public int DisposeCalls;
        public bool LastSubmitAccepted;
        public void TriggerFault()=>fault(new IOException("injected-late-capture-error"));
        public void Push(float[] samples)
        {
            var data=ArrayPool<byte>.Shared.Rent(samples.Length*4);MemoryMarshal.AsBytes(samples.AsSpan()).CopyTo(data);
            var packet=new CapturePacket(data,samples.Length*4,samples.Length,_position,false,false);_position+=samples.Length;
            LastSubmitAccepted=submit(packet);
            if(!LastSubmitAccepted){packet.Dispose();fault(new IOException("Rejected reliability packet"));}
        }
        public ValueTask DisposeAsync(){DisposeCalls++;if(FailDispose)throw new IOException("injected-capture-disposal-error");return ValueTask.CompletedTask;}
    }
    private sealed class ReliabilityWriter(string path):IRecordingWriter
    {
        public string Path=>path;
        public long WrittenFrames {get;private set;}
        public Exception? Failure {get;private set;}
        public Exception? WriteFailure,CompletionFailure;
        public int CompletionCalls;
        public List<float> Samples {get;}=[];
        public bool TryWrite(ReadOnlySpan<float> samples)
        {
            if(WriteFailure!=null){Failure=WriteFailure;return false;}
            Samples.AddRange(samples.ToArray());WrittenFrames+=samples.Length;return true;
        }
        public Task CompleteAsync(){CompletionCalls++;return CompletionFailure==null?Task.CompletedTask:Task.FromException(CompletionFailure);}
        public ValueTask DisposeAsync()=>new(CompleteAsync());
    }
}
