using System.Text;
using PortableRecorder.Audio;
using NAudio.Wave;
static class CalibrationChecks
{
    public static async Task Run(string root,Action<bool,string> check)
    {
        async Task Until(Func<bool> predicate)
        {for(int i=0;i<500;i++){if(predicate())return;await Task.Delay(10);}throw new Exception("Calibration boundary fixture timed out");}
        CalibrationProfile Parse(string text)=>CalibrationProfile.Parse(Encoding.UTF8.GetBytes(text),"fixture.cal");
        void Reject(string text,string match)
        {try{Parse(text);check(false,"Parser should reject "+match);}catch(InvalidDataException e){check(e.Message.Contains(match),e.Message);}}
        var basic=Parse("\"Sens Factor =-2.493dB, SERNO: 7146299\"\n10,0\n1e3,0,0\n24000,0\n");
        check(basic.Points.Length==3&&basic.SensitivityDb==-2.493&&basic.SerialNumber=="7146299","metadata / commas / scientific notation");
        foreach(var encoding in new Encoding[]{new UTF8Encoding(true),new UnicodeEncoding(false,true),new UnicodeEncoding(true,true)})
        {
            var bytes=encoding.GetPreamble().Concat(encoding.GetBytes("10\t0\n20000\t0\n")).ToArray();
            check(CalibrationProfile.Parse(bytes,"encoding.cal").Points.Length==2,"UTF encoding");
        }
        Reject("10 0\n10 1","第 2 行");Reject("20 0\n10 1","第 2 行");Reject("10 0\n20 NaN","第 2 行");Reject("0 0\n20 0","第 1 行");Reject("10 0\n20 1x","第 2 行");Reject("<html>error</html>","网页");
        check(Parse("10 0 0\n20000 0 2").UnsupportedReason!=null,"Nonzero phase cannot enable");
        check(Parse("10 -25\n20000 0").UnsupportedReason!=null,"Boost cannot silently clamp");
        var identity=CalibrationPlan.Prepare(basic,48000);check(identity.LatencyFrames==0,"Zero curve true bypass");
        var profile=Parse("1 -6\n10 -6\n100 -6\n1000 -6\n10000 -6\n24000 -6");
        var plan=CalibrationPlan.Prepare(profile,48000);
        check(plan.LatencyFrames==9215,"Actual FIR plus OLA delay");
        Console.WriteLine($"Synthetic calibration fit: {plan.MaximumFitErrorDb:F6} dB; latency {plan.LatencyFrames} frames");
        var processor=new CalibrationProcessor(plan,2);var input=new float[24000*2];
        for(int i=0;i<24000;i++){input[2*i]=(float)(.2*Math.Sin(2*Math.PI*1000*i/48000));input[2*i+1]=0;}
        var collected=new List<float>();int frame=0;
        foreach(int block in new[]{1,7,511,2048,37,4096,17300})
        {
            int n=Math.Min(block,24000-frame);if(n<=0)break;var output=new float[n*2];var result=processor.Process(input.AsSpan(frame*2,n*2),output,1);
            collected.AddRange(output.Take(result.Frames*2));frame+=n;
        }
        if(frame<24000){var output=new float[(24000-frame)*2];var result=processor.Process(input.AsSpan(frame*2),output,1);collected.AddRange(output.Take(result.Frames*2));}
        var tail=new float[plan.LatencyFrames*2];        var tailOut=new float[tail.Length];var flushed=processor.Process(tail,tailOut,1,drain:true);collected.AddRange(tailOut.Take(flushed.Frames*2));
        check(collected.Count==input.Length,"Drain retains exact original frame count");
        double max=0;for(int i=4000;i<20000;i++)max=Math.Max(max,Math.Abs(collected[i*2]-input[i*2]*Math.Pow(10,.3)));
        Console.WriteLine($"Steady sine amplitude max error: {max:G8}");check(max<.0002,"Correct inverse sign and gain");check(collected.Where((x,i)=>i%2==1).All(x=>x==0),"Independent channel histories");
        // Direct convolution is deliberately independent of the FFT/OLA implementation.
        var random=new Random(4);var shortInput=Enumerable.Range(0,257).Select(_=>(float)(random.NextDouble()*.1)).ToArray();
        var shortProcessor=new CalibrationProcessor(plan,1);var firstOut=new float[257];check(shortProcessor.Process(shortInput,firstOut,1).Frames==0,"Shorter than processing latency");
        var shortTail=new float[plan.LatencyFrames];var shortOut=new float[shortTail.Length];var sr=shortProcessor.Process(shortTail,shortOut,1,drain:true);
        check(sr.Frames==257,"Short recording drain count");double directError=0;int center=(plan.Kernel.Length-1)/2;
        for(int n=0;n<257;n++){double y=0;for(int j=0;j<257;j++){int k=n+center-j;if(k>=0&&k<plan.Kernel.Length)y+=shortInput[j]*plan.Kernel[k];}directError=Math.Max(directError,Math.Abs(shortOut[n]-y));}
        check(directError<1e-7,"OLA output equals independent direct convolution");
        string folder=Path.Combine(root,"library"),original=Path.Combine(root,"original.cal");File.WriteAllText(original,"1 -6\n24000 -6");
        var library=new CalibrationLibrary(folder);var item=library.Import(original);check(library.Import(original).Id==item.Id&&library.Entries.Count==1,"Hash dedup");
        library.Bind("fake",item.Id);check(new CalibrationLibrary(folder).SelectedId("fake")==item.Id,"Endpoint ID binding persists");check(library.SelectedId("different")==null,"New endpoint starts uncalibrated");
        File.Delete(original);check(library.Read(item.Id).Points.Length==2,"Original can move / disappear");
        var factory=new FakeFactory();await using(var engine=new AudioEngine(factory,calibrationLibrary:library))
        {
            int boundaries=0;engine.Diagnostic+=message=>{if(message.StartsWith("Recording source boundary:"))Interlocked.Increment(ref boundaries);};
            await engine.SelectDeviceAsync(factory.Device);check(engine.Calibration.Applied,"Saved calibration restores into capture session");
            await engine.StartRecordingAsync(root,RecordingMode.Shared);string path=engine.Status.FilePath!;
            factory.Push(Enumerable.Repeat(.1f,600).ToArray());await Task.Delay(80);
            var pause=engine.PauseAsync(true);await Until(()=>Volatile.Read(ref boundaries)==1);factory.Push(Enumerable.Repeat(.2f,12000).ToArray());await pause;
            await Until(()=>engine.WrittenFrames==600);
            check(engine.WrittenFrames==600,"Pause drains short recording but excludes paused source interval");
            bool locked=false;try{await engine.SetCalibrationAsync(null);}catch(InvalidOperationException){locked=true;}check(locked,"Paused calibration lock");
            await engine.PauseAsync(false);factory.Push(Enumerable.Repeat(.3f,333).ToArray());await Task.Delay(30);
            var stop=engine.StopRecordingAsync();await Until(()=>Volatile.Read(ref boundaries)==2);factory.Push(Enumerable.Repeat(.4f,12000).ToArray());await stop;
            check(engine.WrittenFrames==933,"Start / resume / stop source frame ownership");
            using(var wave=new WaveFileReader(path))check(wave.Length==933*4,"WAV exact sample count");
            check(File.Exists(path+".calibration.json"),"Traceable calibration sidecar");
            await engine.SetCalibrationAsync(null);check(!engine.Calibration.Applied,"Disable returns true bypass");
        }
        library.Remove(item.Id);check(library.Entries.Count==0&&library.SelectedId("fake")==null,"Remove also removes device binding");
        // Personal calibration files are checked locally without copying them into the repository.
        string real=@"D:\文档\UMIK-1 校准文件";
        if(!Directory.Exists(real))Console.WriteLine("SKIP personal calibration fixtures: directory not present; synthetic calibration checks still run.");
        else Console.WriteLine("Personal calibration fixtures: "+Directory.GetFiles(real,"*.txt").Length+" files available.");
        if(Directory.Exists(real))foreach(string file in Directory.GetFiles(real,"*.txt"))
        {
            var supplied=CalibrationProfile.Parse(File.ReadAllBytes(file),Path.GetFileName(file));check(supplied.Points.Length==615,"Supplied calibration points");
            foreach(int rate in new[]{44100,48000,96000,192000})
            {
                var actual=CalibrationPlan.Prepare(supplied,rate);check(actual.MaximumFitErrorDb<=.1,"Supplied C# fit <=0.1 dB");
                Console.WriteLine($"{supplied.Name}: {rate} Hz, fit {actual.MaximumFitErrorDb:F6} dB, delay {actual.LatencyFrames*1000d/rate:F3} ms");
            }
        }
    }
}


