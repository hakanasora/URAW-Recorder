using System.Globalization;
using System.Text;
using NAudio.Wave;
using PortableRecorder.Audio;
static class CalibrationRobustnessChecks
{
    public static async Task Run(string root,Action<bool,string> check)
    {
        async Task Until(Func<bool> predicate)
        {for(int i=0;i<500;i++){if(predicate())return;await Task.Delay(10);}throw new Exception("Calibration fixture timed out");}
        async Task Reject(Func<Task> action,string name)
        {bool failed=false;try{await action();}catch(Exception){failed=true;}check(failed,name);}
        void Invalid(byte[] bytes,string name)
        {bool failed=false;try{CalibrationProfile.Parse(bytes,"bad.cal");}catch(InvalidDataException){failed=true;}check(failed,name);}
        foreach(string text in new[]{"10,,0\n20,0","10 0\ngarbage 2\n20 0","10 0\n20 Infinity","10 0\n20 -Infinity","10 0\n20 0\0","10 0\n20 0 0 0"})Invalid(Encoding.UTF8.GetBytes(text),"Reject malformed / ambiguous row");
        Invalid(new byte[CalibrationProfile.MaximumBytes+1],"2 MiB limit");
        Invalid([0xff,0,0x81],"Invalid encoding");
        Invalid(Encoding.ASCII.GetBytes(string.Join('\n',Enumerable.Range(1,100001).Select(i=>$"{i} 0"))),"100000 point limit");
        var priorCulture=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
            var invariant=CalibrationProfile.Parse(Encoding.UTF8.GetBytes("# Sens Factor =-2.493dB\n10.05 -1.25\n20000.25 0.5"),"invariant.cal");
            check(invariant.Points[0].FrequencyHz==10.05&&invariant.SensitivityDb==-2.493,"Invariant parsing / commented metadata");
        }
        finally{CultureInfo.CurrentCulture=priorCulture;}
        var library=new CalibrationLibrary(Path.Combine(root,"robust-library"));
        CalibrationLibrary.Entry Add(string name,string text)
        {string path=Path.Combine(root,name);File.WriteAllText(path,text);return library.Import(path);}
        var boost=Add("boost.cal","1 -6\n24000 -6");var attenuate=Add("attenuate.cal","1 6\n24000 6");
        var zero=Add("zero.cal","1 0\n24000 0");var phase=Add("phase.cal","1 0 0\n24000 0 .01");
        var plan=CalibrationPlan.Prepare(library.Read(boost.Id),48000);
        // Independent channels and software gain: no cross-channel energy or hidden limiter.
        var processor=new CalibrationProcessor(plan,4);var input=new float[48000*4];
        for(int n=0;n<48000;n++)for(int c=0;c<4;c++)input[n*4+c]=(float)(.2*(c+1)*Math.Sin(2*Math.PI*1000*n/48000));
        var output=new float[input.Length];var result=processor.Process(input,output,2);
        check(output.Max()>3,"Calibration plus gain can exceed float full scale");
        for(int c=0;c<4;c++)
        {
            double sum=0;for(int n=15000;n<30000;n++)sum+=output[n*4+c]*output[n*4+c];
            double rms=Math.Sqrt(sum/15000);double expected=.2*(c+1)*2*Math.Pow(10,.3)/Math.Sqrt(2);
            check(Math.Abs(20*Math.Log10(rms/expected))<.01,"Multichannel correction and gain magnitude");
        }
        var factory=new FakeFactory();await using(var engine=new AudioEngine(factory,calibrationLibrary:library))
        {
            await engine.SelectDeviceAsync(factory.Device);await engine.SetCalibrationAsync(boost.Id);engine.SetGain(6);
            factory.Push(Enumerable.Range(0,48000).Select(i=>(float)(.1*Math.Sin(2*Math.PI*1000*i/48000))).ToArray());
            await Until(()=>engine.Latest.FramePosition>=48000-plan.LatencyFrames);
            check(Math.Abs(engine.Latest.RmsDbfs-(-20-3.0103+12))<.02,"RMS uses calibrated plus gain samples");
            check(Math.Abs(engine.Latest.VuLeft-10)<.1,"VU uses same calibrated samples");
            check(Math.Abs(engine.Latest.SourcePeakDbfs+20)<.01,"Raw entry peak remains separate");
            check(engine.Latest.CalibrationRevision>0&&engine.Latest.ProcessingDelayFrames==9215,"Snapshot carries calibration revision and delay");
            await Until(()=>engine.Latest.Spectrum!=null&&engine.Latest.FrequencyHz.HasValue);
            var spectrum=engine.Latest.Spectrum!;int bin=Enumerable.Range(0,spectrum.FrequenciesHz.Length).MinBy(k=>Math.Abs(spectrum.FrequenciesHz[k]-1000));
            check(spectrum.Dbfs[bin]>-9&&spectrum.Dbfs[bin]<-7,"Spectrum receives compensated plus gain samples");
            check(Math.Abs(1200*Math.Log2(engine.Latest.FrequencyHz!.Value/1000))<5,"Pitch receives the same calibrated waveform");
            await Reject(()=>engine.SetCalibrationAsync(phase.Id),"Nonzero phase fails activation");check(engine.Calibration.ProfileId==boost.Id,"Failed activation retains previous working configuration");
            string index=Path.Combine(library.Folder,"calibration-library.json"),backup=index+".fixture";
            File.Move(index,backup);Directory.CreateDirectory(index);
            try
            {
                await Reject(()=>engine.SetCalibrationAsync(attenuate.Id),"Persistence failure cannot change live calibration");
                check(engine.Calibration.ProfileId==boost.Id&&library.SelectedId("fake")==boost.Id,"Failed atomic index save retains old profile and binding");
            }
            finally{Directory.Delete(index);File.Move(backup,index);}
            await engine.SetCalibrationAsync(attenuate.Id);engine.SetGain(0);factory.Push(Enumerable.Repeat(1f,24000).ToArray());
            await Until(()=>engine.Latest.Valid);
            check(engine.Latest.InputFullScale&&!engine.Latest.OutputOverload,"Attenuation cannot conceal original rail detection");
            check(engine.Latest.HeldPeakDbfs<0,"Peak reset prevents mixing calibration conditions");
            await Task.WhenAll(engine.SetCalibrationAsync(boost.Id),engine.SetCalibrationAsync(zero.Id),engine.SetCalibrationAsync(attenuate.Id));
            await engine.SetCalibrationAsync(boost.Id);check(engine.Calibration.ProfileId==boost.Id,"Rapid selection serialized without stale background result");
            await engine.StartRecordingAsync(root,RecordingMode.Shared);string path=engine.Status.FilePath!;
            var captured=new List<float>();
            void Push(float value,int count){var data=Enumerable.Repeat(value,count).ToArray();captured.AddRange(data);factory.Push(data);}
            Push(.1f,600);await Task.Delay(40);var pausing=engine.PauseAsync(true);await Task.Delay(30);Push(.2f,12000);await pausing;
            await engine.PauseAsync(false);Push(.3f,333);await Task.Delay(30);var stopping=engine.StopRecordingAsync();await Task.Delay(30);Push(.4f,12000);await stopping;
            using(var wave=new WaveFileReader(path))
            {
                var recorded=new float[933];wave.ToSampleProvider().Read(recorded);
                // The continuous monitoring stream already contained earlier samples before record start.
                // Compare the second interval, far from the pre-record history boundary, exactly.
                double worst=0;int center=(plan.Kernel.Length-1)/2;
                for(int n=0;n<333;n++)
                {
                    int source=12600+n;double expected=0;
                    for(int k=0;k<plan.Kernel.Length;k++){int j=source+center-k;if(j>=0&&j<captured.Count)expected+=captured[j]*plan.Kernel[k];}
                    worst=Math.Max(worst,Math.Abs(expected-recorded[600+n]));
                }
                check(worst<1e-6,"Pause/resume recorded content equals continuous offline convolution");
            }
            await engine.StartRecordingAsync(root,RecordingMode.Shared);string discard=engine.Status.FilePath!;
            Push(.1f,300);await Task.Delay(20);var pending=engine.PauseAsync(true);await Task.Delay(20);Push(.2f,12000);await pending;
            await engine.DiscardRecordingAsync();check(!File.Exists(discard)&&!File.Exists(discard+".calibration.json"),"Discard removes owned WAV and summary");
            Push(.15f,20000);await Until(()=>engine.Latest.Valid);check(double.IsFinite(engine.Latest.RmsDbfs),"Monitoring rebuilds correctly after mathematical drain / discard");
            await engine.StartRecordingAsync(root,RecordingMode.Shared);string partial=engine.Status.FilePath!;
            Push(.1f,777);await Task.Delay(30);factory.Push(new float[100],true);
            await Until(()=>engine.Status.Partial&&engine.Status.State==AudioRunState.Monitoring);
            using(var wave=new WaveFileReader(partial))check(wave.Length==777*4,"Gap drains accepted calibrated input only, not missing / gap packet");
            check(File.ReadAllText(partial+".calibration.json").Contains("\"Completed\":false"),"Partial summary accurately marks interruption");
        }
        // Reopening at another actual format builds fresh kernels and per-channel state.
        library.Bind("fake",attenuate.Id);
        var higherRate=new FakeFactory{Format=new(96000,2,32,32,PcmEncoding.Float)};
        await using(var engine=new AudioEngine(higherRate,calibrationLibrary:library,writerFactory:(path,format)=>new FloatWaveWriter(path,format,256)))
        {
            await engine.SelectDeviceAsync(higherRate.Device);
            check(engine.Calibration.SampleRate==96000&&engine.Calibration.Channels==2&&engine.Calibration.LatencyFrames==18431,"Format change rebuilds kernel / channel states");
            await engine.StartRecordingAsync(root,RecordingMode.Exclusive);string path=engine.Status.FilePath!;
            higherRate.Push(Enumerable.Range(0,2000).Select(i=>i%2==0?.4f:0f).ToArray());
            await engine.DisposeAsync();
            check(Encoding.ASCII.GetString(File.ReadAllBytes(path),0,4)=="RF64","Calibrated short recording RF64 promotion / close drain");
            using var wave=new WaveFileReader(path);var samples=new float[2000];wave.ToSampleProvider().Read(samples);
            check(wave.WaveFormat.Channels==2&&wave.Length==8000,"Calibrated RF64 native stereo / exact frames");
            check(samples.Where((_,i)=>i%2==1).All(x=>x==0),"Rebuilt stereo processor never copies mono channel");
        }
        // A sidecar collision is separate from WAV success and cannot delete someone else's file.
        var collisionFactory=new FakeFactory();await using(var engine=new AudioEngine(collisionFactory,calibrationLibrary:library))
        {
            await engine.SelectDeviceAsync(collisionFactory.Device);await engine.SetCalibrationAsync(null);
            await engine.StartRecordingAsync(root,RecordingMode.Shared);string path=engine.Status.FilePath!;
            collisionFactory.Push(new float[40]);await engine.PauseAsync(true);
            File.WriteAllText(path+".calibration.json","unowned");await engine.DiscardRecordingAsync();
            check(!File.Exists(path)&&File.ReadAllText(path+".calibration.json")=="unowned","Discard only owns the sidecar it actually created");
            await engine.SetCalibrationAsync(boost.Id);await engine.StartRecordingAsync(root,RecordingMode.Shared);string pendingPath=engine.Status.FilePath!;
            collisionFactory.Push(new float[321]);await Task.Delay(30);var pending=engine.PauseAsync(true);await Task.Delay(30);collisionFactory.Push(new float[20],true);
            await Reject(async()=>await pending,"Pending pause rejects a real discontinuity without hanging");
            await Until(()=>engine.Status.Partial);
            using(var wave=new WaveFileReader(pendingPath))check(wave.Length==321*4,"Fault during pending tail still finalizes accepted source frames");
        }
        // Application-copy damage is explicit, never substitutes another profile or records silently.
        library.Bind("fake",boost.Id);File.WriteAllText(Path.Combine(library.Folder,boost.Id+".cal"),"broken");
        var brokenFactory=new FakeFactory();await using(var engine=new AudioEngine(brokenFactory,calibrationLibrary:library))
        {
            await engine.SelectDeviceAsync(brokenFactory.Device);
            check(!engine.Calibration.Applied&&engine.Calibration.Notice.Contains("指纹"),"Damaged application copy disables selected correction visibly");
            await Reject(()=>engine.StartRecordingAsync(root,RecordingMode.Shared),"Damaged remembered calibration cannot silently record uncorrected audio");
            await engine.SetCalibrationAsync(null);await engine.StartRecordingAsync(root,RecordingMode.Shared);await engine.StopRecordingAsync();
        }
        File.WriteAllText(Path.Combine(library.Folder,"calibration-library.json"),"broken-index");var damaged=new CalibrationLibrary(library.Folder);
        check(damaged.Warning!=null&&damaged.Entries.Count==0,"Corrupt index fails closed without guessing another file");
        File.WriteAllText(Path.Combine(library.Folder,"calibration-library.json"),"{\"Version\":1,\"Files\":[null],\"Devices\":{}}");
        check(new CalibrationLibrary(library.Folder).Warning!=null,"Null catalog entry cannot crash startup");
        var openingFactory=new FakeFactory{FailDuringOpen=true};
        await using(var engine=new AudioEngine(openingFactory,calibrationLibrary:new CalibrationLibrary(Path.Combine(root,"opening-failure"))))
        {
            await engine.SelectDeviceAsync(openingFactory.Device);
            check(engine.Status.State==AudioRunState.Faulted&&!engine.Latest.Valid&&openingFactory.Current!.Closed,"Capture fault while preparing calibration cannot disappear before session publication");
        }
    }
}
