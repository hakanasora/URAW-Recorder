using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PortableRecorder.Audio;
using PortableRecorder.Desktop;

static class EncodingChecks
{
    public static async Task Run(string root,Action<bool,string> check)
    {
        string encoder=Path.GetFullPath("artifacts/encoders/ffmpeg.exe"),directory=Path.Combine(root,"编码 formats");Directory.CreateDirectory(directory);
        async Task<float[]> Decode(string path)
        {
            var start=new ProcessStartInfo(encoder){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"-v","error","-i",path,"-f","f32le","-c:a","pcm_f32le","pipe:1"})start.ArgumentList.Add(arg);
            var result=await ProcessCapture.RunAsync(start,TimeSpan.FromSeconds(15));
            check(result.ExitCode==0,"Compressed file decodes: "+result.Error);
            return MemoryMarshal.Cast<byte,float>(result.Bytes).ToArray();
        }
        float[] Signal(int rate,int frames,int channels)=>Enumerable.Range(0,frames*channels).Select(i=>
            (float)(Math.Round(.3*Math.Sin(2*Math.PI*(440+110*(i%channels))*(i/channels)/rate)*8388608)/8388608)).ToArray();
        async Task Write(CompressedAudioWriter writer,float[] samples,int channels)
        {
            await using(writer)
            {
                for(int offset=0;offset<samples.Length;offset+=127*channels)
                {
                    if(!writer.TryWrite(samples.AsSpan(offset,Math.Min(127*channels,samples.Length-offset))))
                        throw new IOException("Encoder rejected a block in "+writer.Path,writer.Failure);
                    if(offset%(127*channels*16)==0)await Task.Delay(1);
                }
                check(true,"All irregular blocks accepted: "+Path.GetFileName(writer.Path));
                await writer.CompleteAsync();check(writer.WrittenFrames==samples.Length/channels,"Written frame count exact");
                check(writer.AuxiliaryPaths.All(p=>!File.Exists(p)),"Successful encoder removes staging file");
            }
        }
        foreach(int level in new[]{0,5,8})
        foreach(var tuple in new[]{(44100,1),(48000,2),(96000,6)})
        {
            var (rate,channels)=tuple;var format=new AudioFormat(rate,channels,32,32,PcmEncoding.Float);
            var signal=Signal(rate,rate/8+13,channels);string path=Path.Combine(directory,$"flac-{level}-{rate}-{channels}.flac");
            await Write(new(path,format,new(RecordingCodec.Flac,level),encoder),signal,channels);
            var decoded=await Decode(path);check(decoded.SequenceEqual(signal),"FLAC exact 24-bit PCM roundtrip; rate/channels/length unchanged");
            check(File.ReadAllBytes(path).Take(4).SequenceEqual("fLaC"u8.ToArray()),"Real FLAC header");
        }
        foreach(int quality in new[]{0,2,9})
        foreach(var tuple in new[]{(44100,1),(48000,2),(24000,1),(8000,1)})
        {
            var(rate,channels)=tuple;var format=new AudioFormat(rate,channels,32,32,PcmEncoding.Float);
            var signal=Signal(rate,rate/2+13,channels);string path=Path.Combine(directory,$"mp3-{quality}-{rate}-{channels}.mp3");
            await Write(new(path,format,new(RecordingCodec.Mp3,quality),encoder),signal,channels);
            var decoded=await Decode(path);check(decoded.Length==signal.Length,"MP3 gapless Xing/LAME metadata preserves source duration");
            double error=decoded.Zip(signal,(a,b)=>(a-b)*(double)(a-b)).Average();check(error<.012,"MP3 decoded signal contains the source tone, not silence");
        }
        foreach(var codec in new[]{RecordingCodec.Flac,RecordingCodec.Mp3})
        {
            var format=new AudioFormat(48000,1,32,32,PcmEncoding.Float);string path=Path.Combine(directory,"pause."+codec.ToString().ToLowerInvariant());
            foreach(int count in new[]{1,13})
            {
                string tiny=Path.Combine(directory,$"tiny-{codec}-{count}.{codec.ToString().ToLowerInvariant()}");
                if(codec==RecordingCodec.Flac)
                {await Write(new(tiny,format,new(codec,2),encoder),Enumerable.Repeat(.125f,count).ToArray(),1);check((await Decode(tiny)).Length==count,"FLAC preserves sub-codec-frame duration");}
                else
                {
                    var tinyWriter=new CompressedAudioWriter(tiny,format,new(codec,2),encoder);tinyWriter.TryWrite(Enumerable.Repeat(.125f,count).ToArray());
                    bool tinyRejected=false;try{await tinyWriter.CompleteAsync();}catch(IOException){tinyRejected=true;}check(tinyRejected,"Sub-MPEG-frame MP3 cannot silently pad or claim exact duration");try{await tinyWriter.DisposeAsync();}catch(IOException){}
                }
            }
            await using(var writer=new CompressedAudioWriter(path,format,new(codec,2),encoder))
            {
                var a=Signal(48000,1001,1);var b=Signal(48000,203,1);check(writer.TryWrite(a),"First interval");
                // No writer call during pause, exactly as ProcessingPipeline source-interval gating.
                check(writer.TryWrite(b),"Continue interval");await writer.CompleteAsync();check(writer.WrittenFrames==1204,"Pause has no silence placeholders");
                var decoded=await Decode(path);check(decoded.Length==1204,"Short recording final tail / decoded duration");
                if(codec==RecordingCodec.Flac)check(decoded.SequenceEqual(a.Concat(b)),"FLAC pause/continue exact content");
            }
            string rejected=Path.Combine(directory,"overload."+codec.ToString().ToLowerInvariant());
            var invalid=new CompressedAudioWriter(rejected,format,new(codec,2),encoder);
            check(invalid.TryWrite(Signal(48000,2000,1)),"Valid prefix accepted");
            check(!invalid.TryWrite([1.01f])&&invalid.Failure!=null,"Compressed overrange rejected before integer clipping");
            bool failed=false;try{await invalid.CompleteAsync();}catch(IOException){failed=true;}check(failed,"Failed prefix completion is never reported successful");
            check((await Decode(rejected)).Length==2000,"Overload retains playable valid prefix only");
            try{await invalid.DisposeAsync();}catch(IOException){}
            var empty=new CompressedAudioWriter(rejected+"-empty",format,new(codec,2),encoder);
            bool emptyFailed=false;try{await empty.CompleteAsync();}catch(IOException){emptyFailed=true;}check(emptyFailed,"Empty compressed file cannot report saved successfully");try{await empty.DisposeAsync();}catch(IOException){}
            foreach(float sample in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity,-1.01f})
            {
                var bad=new CompressedAudioWriter(rejected+"-bad-"+BitConverter.SingleToInt32Bits(sample),format,new(codec,2),encoder);
                check(!bad.TryWrite([sample]),"Invalid sample never enters encoder");try{await bad.DisposeAsync();}catch(IOException){}
            }
        }
        foreach(var format in new[]{new AudioFormat(96000,2,32,32,PcmEncoding.Float),new AudioFormat(48000,6,32,32,PcmEncoding.Float)})
        {bool rejected=false;try{new RecordingEncoding(RecordingCodec.Mp3,2).Validate(format);}catch(NotSupportedException){rejected=true;}check(rejected,"MP3 rejects unsupported input without automatic downmix/resampling");}
        {
            string path=Path.Combine(directory,"existing.flac");File.WriteAllText(path,"original");bool rejected=false;
            try{new CompressedAudioWriter(path,new(48000,1,32,32,PcmEncoding.Float),new(RecordingCodec.Flac,5),encoder);}catch(IOException){rejected=true;}
            check(rejected&&File.ReadAllText(path)=="original","CreateNew never replaces pre-existing recording");
            bool missing=false;try{new CompressedAudioWriter(path+".missing",new(48000,1,32,32,PcmEncoding.Float),new(RecordingCodec.Flac,5),"missing.exe");}catch(FileNotFoundException){missing=true;}
            check(missing&&!File.Exists(path+".missing"),"Missing encoder fails before creating a recording");
        }
        string preference=Path.Combine(directory,"encoding.json");var settings=new EncodingPreferences(RecordingCodec.Mp3,8,2);settings.Save(preference);
        check(EncodingPreferences.Load(preference)==settings,"Format / separate FLAC and MP3 quality persist");File.WriteAllText(preference,"{broken");check(EncodingPreferences.Load(preference)==new EncodingPreferences(),"Damaged encoding preferences recover to float WAV");
        {
            var format=new AudioFormat(48000,1,32,32,PcmEncoding.Float);
            foreach(var encoding in new[]{new RecordingEncoding(RecordingCodec.Flac,-1),new RecordingEncoding(RecordingCodec.Flac,9),new RecordingEncoding(RecordingCodec.Mp3,10)})
            {bool invalid=false;try{encoding.Validate(format);}catch(ArgumentOutOfRangeException){invalid=true;}check(invalid,"Invalid compression parameter rejected");}
            string bounded=Path.Combine(directory,"bounded.flac");var writer=new CompressedAudioWriter(bounded,format,new(RecordingCodec.Flac,5),encoder);
            check(!writer.TryWrite(new float[96001])&&writer.Failure!=null,"Queue budget is bounded independently of encoder speed");try{await writer.DisposeAsync();}catch(IOException){}
            string crashed=Path.Combine(directory,"encoder-failed.flac");writer=new(crashed,format,new(RecordingCodec.Flac,5),Path.Combine(Environment.SystemDirectory,"where.exe"));
            writer.TryWrite(new float[1200]);bool failed=false;try{await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));}catch(IOException){failed=true;}
            check(failed&&writer.Failure!=null,"Premature encoder process exit cannot report success or hang finalization");try{await writer.DisposeAsync();}catch(IOException){}
        }
        await EngineChecks(directory,encoder,check,Decode);
    }
    private static async Task EngineChecks(string directory,string encoder,Action<bool,string> check,Func<string,Task<float[]>> decode)
    {
        // Use the exact published location resolution without involving a hardware endpoint.
        string target=RecordingEncoding.EncoderPath;Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(encoder,target,true);
        foreach(var codec in new[]{RecordingCodec.Flac,RecordingCodec.Mp3})
        {
            var factory=new FakeFactory();var library=new CalibrationLibrary(Path.Combine(directory,"engine-"+codec));
            await using var engine=new AudioEngine(factory,library,inputFormats:new(Path.Combine(directory,codec+"-formats.json")));
            await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(directory,RecordingMode.Shared,new(codec,2));string path=engine.Status.FilePath!;
            factory.Push(Enumerable.Repeat(.125f,1000).ToArray());await engine.PauseAsync(true);
            factory.Push(Enumerable.Repeat(.9f,1000).ToArray());await engine.PauseAsync(false);
            factory.Push(Enumerable.Repeat(.25f,200).ToArray());await engine.StopRecordingAsync();
            check(engine.RecordedTime.TotalSeconds==1200/48000d,"Engine sample clock excludes paused frames for "+codec);
            var samples=await decode(path);check(samples.Length==1200,"Engine pause gate and encoder tail length for "+codec);
            if(codec==RecordingCodec.Flac)check(samples.SequenceEqual(Enumerable.Repeat(.125f,1000).Concat(Enumerable.Repeat(.25f,200))),"Engine sends post-processing samples to FLAC");
            using(var sidecar=JsonDocument.Parse(File.ReadAllText(path+".calibration.json")))
                check(sidecar.RootElement.GetProperty("Completed").GetBoolean()&&sidecar.RootElement.GetProperty("Recording").GetProperty("Encoding").GetProperty("Codec").GetInt32()==(int)codec,"Calibration summary records actual encoder selection");
            await engine.StartRecordingAsync(directory,RecordingMode.Shared,new(codec,2));string discarded=engine.Status.FilePath!;factory.Push(new float[200]);await engine.PauseAsync(true);await engine.DiscardRecordingAsync();
            check(!File.Exists(discarded)&&!File.Exists(discarded+".calibration.json")&&!Directory.GetFiles(directory,Path.GetFileName(discarded)+".*.encoding").Any(),"Discard removes only current encoded file, summary and staging");
        }
    }
}
