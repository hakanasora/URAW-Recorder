using System.Runtime.InteropServices;
using PortableRecorder.Audio;

static class LiveMonitorChecks
{
    public static async Task Run(string root,Action<bool,string> check)
    {
        CheckBufferDsp(check);
        CheckPcmEncoding(check);
        await CheckIdleAndGainMath(root,check);
        await CheckRecordingPauseAndExclusiveFormatSwitch(root,check);
        await CheckCalibrationPostProcessing(root,check);
        await CheckSuspendDisablesMonitoring(root,check);
        await CheckFaultIsolationFromRecording(root,check);
        await CheckDefaultDeviceHotSwapAndInvalidatedRecovery(root,check);
    }

    private static void CheckBufferDsp(Action<bool,string> check)
    {
        // Mono -> Stereo dual-mono and initial gain without startup burst.
        var stereo=new LiveMonitorBuffer(new(48000,2));
        stereo.Write([0.5f,-0.25f,0.75f],48000,1,0.5);
        var outStereo=new float[10];
        int read=stereo.Read(outStereo);
        check(read==3,"Direct pass-through returns exact frame count");
        check(Math.Abs(outStereo[0]-0.25f)<1e-5f&&Math.Abs(outStereo[1]-0.25f)<1e-5f,"Mono maps dual-mono to left and right");
        check(Math.Abs(outStereo[2]+0.125f)<1e-5f&&Math.Abs(outStereo[3]+0.125f)<1e-5f,"Second mono frame preserves sign and gain");
        check(outStereo[6]==0f&&outStereo[7]==0f&&outStereo[8]==0f&&outStereo[9]==0f,"Underrun zero-fills remaining frames");

        // Mono -> 6ch maps to ch0/ch1 and zeros remaining channels.
        var surround=new LiveMonitorBuffer(new(48000,6));
        surround.Write([0.4f],48000,1,1.0);
        var outSix=new float[6];
        check(surround.Read(outSix)==1,"Surround output reads 1 frame");
        check(Math.Abs(outSix[0]-0.4f)<1e-5f&&Math.Abs(outSix[1]-0.4f)<1e-5f&&outSix[2]==0f&&outSix[3]==0f&&outSix[4]==0f&&outSix[5]==0f,"Mono to >2ch duplicates ch0/ch1 and zeros others");

        // Stereo -> Mono averages ch0 and ch1; 4ch -> 2ch takes first 2 channels.
        var mono=new LiveMonitorBuffer(new(48000,1));
        mono.Write([0.8f,-0.2f],48000,2,1.0);
        var outMono=new float[1];
        check(mono.Read(outMono)==1&&Math.Abs(outMono[0]-0.3f)<1e-5f,"Stereo to mono averages left and right");

        var down2=new LiveMonitorBuffer(new(48000,2));
        down2.Write([0.2f,0.4f,0.9f,-0.9f],48000,4,1.0);
        var outDown2=new float[2];
        check(down2.Read(outDown2)==1&&Math.Abs(outDown2[0]-0.2f)<1e-5f&&Math.Abs(outDown2[1]-0.4f)<1e-5f,"4ch to 2ch preserves first two channels");

        // Safety guard: non-finite becomes 0, out-of-range clamps to [-1, 1].
        var safe=new LiveMonitorBuffer(new(48000,1));
        safe.Write([float.NaN,float.PositiveInfinity,float.NegativeInfinity,2.5f,-3.0f],48000,1,1.0);
        var outSafe=new float[5];
        check(safe.Read(outSafe)==5,"Safety guard preserves frame count");
        check(outSafe[0]==0f&&outSafe[1]==0f&&outSafe[2]==0f,"Non-finite samples sanitize to zero");
        check(outSafe[3]==1f&&outSafe[4]==-1f,"Out-of-range samples clamp to [-1, 1]");

        // Gain slew (~5 ms linear ramp at 48 kHz = 240 frames).
        var slew=new LiveMonitorBuffer(new(48000,1));
        var dc=new float[300];
        Array.Fill(dc,0.5f);
        slew.Write(dc.AsSpan(0,10),48000,1,0.2);
        var warmup=new float[10];
        slew.Read(warmup);
        check(Math.Abs(warmup[0]-0.1f)<1e-5f&&Math.Abs(warmup[9]-0.1f)<1e-5f,"Initial gain applies immediately without ramp artifact");
        slew.Write(dc,48000,1,1.0);
        var ramped=new float[300];
        check(slew.Read(ramped)==300,"Gain ramp delivers all frames");
        check(ramped[0]<0.11f&&ramped[119]>0.25f&&ramped[119]<0.35f&&Math.Abs(ramped[250]-0.5f)<1e-4f,"Gain changes slew linearly over ~5 ms instead of stepping");
        bool monotonic=true;
        for(int i=1;i<240;i++)if(ramped[i]<ramped[i-1]-1e-6f){monotonic=false;break;}
        check(monotonic,"Gain ramp rises monotonically without zipper glitches");

        // Continuous resampling across small packet boundaries (44.1 kHz -> 48 kHz and 96 kHz -> 48 kHz).
        var up=new LiveMonitorBuffer(new(48000,1));
        var src44=TestSupport.Sine(44100,441*3,1000,0.8,1);
        var renderedUp=new List<float>();
        var chunkOut=new float[512];
        for(int offset=0;offset<src44.Length;offset+=137)
        {
            int len=Math.Min(137,src44.Length-offset);
            up.Write(src44.AsSpan(offset,len),44100,1,1.0);
            int got;
            while((got=up.Read(chunkOut))>0)for(int i=0;i<got;i++)renderedUp.Add(chunkOut[i]);
        }
        check(Math.Abs(renderedUp.Count-480*3)<=3,$"44.1->48 kHz output count matches ratio ({renderedUp.Count})");
        double maxJump=0,peakUp=0;
        for(int i=1;i<renderedUp.Count;i++)
        {
            maxJump=Math.Max(maxJump,Math.Abs(renderedUp[i]-renderedUp[i-1]));
            peakUp=Math.Max(peakUp,Math.Abs(renderedUp[i]));
        }
        double expectedMaxStep=0.8*2*Math.PI*1000/48000;
        check(peakUp>0.75&&peakUp<=0.81,"44.1->48 kHz preserves sine amplitude");
        check(maxJump<expectedMaxStep*1.25,$"44.1->48 kHz stays continuous across uneven packet boundaries (maxJump={maxJump:F4})");

        // Downsampling 96 -> 48 kHz and mid-stream format reset.
        var down=new LiveMonitorBuffer(new(48000,2));
        var src96=TestSupport.Sine(96000,960,1000,0.6,2);
        down.Write(src96.AsSpan(0,480*2),96000,2,1.0);
        down.Write(src96.AsSpan(480*2,480*2),96000,2,1.0);
        var renderedDown=new float[600*2];
        int gotDown=down.Read(renderedDown);
        check(Math.Abs(gotDown-480)<=2,$"96->48 kHz halves frame count ({gotDown})");
        down.Write([0.3f,0.3f,0.3f,0.3f],48000,1,1.0);
        var afterSwitch=new float[8];
        int gotSwitch=down.Read(afterSwitch);
        check(gotSwitch==4&&Math.Abs(afterSwitch[0]-0.3f)<1e-5f&&Math.Abs(afterSwitch[1]-0.3f)<1e-5f,"Mid-stream format change resets resampler cleanly");

        // Overrun drops oldest queued frames and keeps latency bounded (~40 ms).
        var bounded=new LiveMonitorBuffer(new(48000,1));
        for(int p=0;p<20;p++)
        {
            var packet=new float[480];
            Array.Fill(packet,(p+1)/25f);
            bounded.Write(packet,48000,1,1.0);
        }
        check(bounded.QueuedFrames<=bounded.MaxQueuedFrames,$"Overrun caps queued latency at <= {bounded.MaxQueuedFrames} frames (actual {bounded.QueuedFrames})");
        var newest=new float[bounded.MaxQueuedFrames];
        int gotBounded=bounded.Read(newest);
        check(gotBounded==bounded.MaxQueuedFrames,"Bounded read returns full retained tail");
        check(Math.Abs(newest[^1]-20f/25f)<1e-5f&&newest[0]>0.5f,"Overrun keeps newest frames and drops oldest");
    }

    private static void CheckPcmEncoding(Action<bool,string> check)
    {
        float[] input=[-1f,-0.5f,0f,0.25f,0.5f,0.99f];
        var formats=new[]
        {
            new AudioFormat(48000,2,16,16,PcmEncoding.SignedInteger),
            new AudioFormat(48000,2,24,24,PcmEncoding.SignedInteger),
            new AudioFormat(48000,2,32,24,PcmEncoding.SignedInteger),
            new AudioFormat(48000,2,32,32,PcmEncoding.SignedInteger),
            new AudioFormat(48000,2,32,32,PcmEncoding.Float)
        };
        foreach(var fmt in formats)
        {
            var encoded=new byte[input.Length*(fmt.Bits/8)];
            var decoded=new float[input.Length];
            WasapiLiveMonitorOutput.EncodePcm(input,encoded,fmt);
            PcmDecoder.Decode(encoded,decoded,fmt);
            double maxError=0;
            for(int i=0;i<input.Length;i++)maxError=Math.Max(maxError,Math.Abs(decoded[i]-input[i]));
            double tolerance=fmt.Bits==16?2e-4:1e-5;
            check(maxError<tolerance,$"PCM output round-trip {fmt.Description} accurate (err={maxError:E2})");
        }
    }

    private static async Task CheckIdleAndGainMath(string root,Action<bool,string> check)
    {
        var capture=new FakeFactory();
        var monitor=new FakeLiveMonitorFactory();
        await using var engine=TestSupport.CreateEngine(root,capture,liveMonitorFactory:monitor);
        await engine.SelectDeviceAsync(capture.Device);

        check(!engine.LiveMonitor.Enabled&&!engine.LiveMonitor.Active&&engine.LiveMonitor.Error==null,"Live monitor starts disabled");
        engine.SetGain(6.0);
        engine.SetLiveMonitorGain(-12.0);

        await engine.SetLiveMonitorEnabledAsync(true);
        await engine.SetLiveMonitorEnabledAsync(true);
        check(engine.LiveMonitor.Enabled&&engine.LiveMonitor.Active&&monitor.OpenCount==1,"Enable is idempotent and activates output once");

        var source=new float[480];
        Array.Fill(source,0.4f);
        capture.Push(source);
        await TestSupport.Until(()=>monitor.Current!.WriteCount>0,"idle monitor receives audio");

        var rendered=new float[480*2];
        int frames=monitor.Current!.ReadRendered(rendered);
        double expected=0.4*Math.Pow(10,6.0/20)*Math.Pow(10,-12.0/20);
        check(frames==480&&Math.Abs(rendered[100]-expected)<1e-3&&Math.Abs(rendered[101]-expected)<1e-3,
            $"Idle monitoring scales by inputGain * monitorGain (got={rendered[100]:F4}, expected={expected:F4})");

        await engine.SetLiveMonitorEnabledAsync(false);
        check(!engine.LiveMonitor.Enabled&&!engine.LiveMonitor.Active&&monitor.Current.Disposed&&monitor.ActiveWatchers==0,
            "Disable disposes output and watcher");
        int writesBefore=monitor.Current.WriteCount;
        capture.Push(source);
        await Task.Delay(40);
        check(monitor.Current.WriteCount==writesBefore,"Disabled monitor receives no further samples");
    }

    private static async Task CheckRecordingPauseAndExclusiveFormatSwitch(string root,Action<bool,string> check)
    {
        var capture=new FakeFactory();
        var monitor=new FakeLiveMonitorFactory();
        await using var engine=TestSupport.CreateEngine(root,capture,liveMonitorFactory:monitor);
        await engine.SelectDeviceAsync(capture.Device);
        await engine.SetInputFormatAsync(new AudioFormat(96000,1,24,24,PcmEncoding.SignedInteger));
        await engine.SetLiveMonitorEnabledAsync(true);

        capture.Push(TestSupport.Sine(48000,480,1000,0.4,1));
        await TestSupport.Until(()=>monitor.Current!.LastSourceRate==48000,"shared 48 kHz monitored");

        string dir=Path.Combine(root,"rec-switch");
        Directory.CreateDirectory(dir);
        await engine.StartRecordingAsync(dir,RecordingMode.Exclusive);

        capture.Push(TestSupport.Sine(96000,960,1000,0.5,1));
        await TestSupport.Until(()=>engine.WrittenFrames>=960&&monitor.Current!.LastSourceRate==96000,"exclusive 96 kHz recorded and monitored");
        check(engine.LiveMonitor.Enabled&&engine.LiveMonitor.Active&&monitor.OpenCount==1,"Exclusive capture transition preserves live monitor without reopening");

        var exclusiveOut=new float[1024*2];
        int exclusiveFrames=monitor.Current!.ReadRendered(exclusiveOut);
        check(exclusiveFrames>0,"Monitor renders audio during exclusive 96 kHz recording");

        await engine.PauseAsync(true);
        long writtenAtPause=engine.WrittenFrames;
        int writesAtPause=monitor.Current.WriteCount;
        capture.Push(TestSupport.Sine(96000,960,1000,0.5,1));
        await TestSupport.Until(()=>monitor.Current.WriteCount>writesAtPause,"paused recording continues live monitoring");
        check(engine.WrittenFrames==writtenAtPause,"Paused recording freezes file frames while monitor continues");

        await engine.PauseAsync(false);
        capture.Push(TestSupport.Sine(96000,960,1000,0.5,1));
        await TestSupport.Until(()=>engine.WrittenFrames>writtenAtPause,"resumed recording writes again");
        await engine.StopRecordingAsync();

        string? saved=engine.Status.FilePath;
        check(saved!=null&&File.Exists(saved)&&!engine.Status.Partial,"Recording completes cleanly after exclusive pause/resume with monitoring");
        capture.Push(TestSupport.Sine(48000,480,1000,0.4,1));
        await TestSupport.Until(()=>monitor.Current.LastSourceRate==48000,"monitor follows return to shared 48 kHz");
    }

    private static async Task CheckCalibrationPostProcessing(string root,Action<bool,string> check)
    {
        var capture=new FakeFactory();
        var monitor=new FakeLiveMonitorFactory();
        await using var engine=TestSupport.CreateEngine(root,capture,liveMonitorFactory:monitor);
        await engine.SelectDeviceAsync(capture.Device);

        string calPath=Path.Combine(root,"boost6db.cal");
        await File.WriteAllTextAsync(calPath,"1 -6\n10 -6\n100 -6\n1000 -6\n10000 -6\n24000 -6\n");
        var entry=await engine.ImportCalibrationAsync(calPath);
        await engine.SetCalibrationAsync(entry.Id);
        check(engine.Calibration.Applied&&engine.Calibration.LatencyFrames>0,"Non-flat calibration reports applied with positive latency");

        await engine.SetLiveMonitorEnabledAsync(true);
        int latency=engine.Calibration.LatencyFrames;
        int totalFrames=latency+4800;
        var sine=TestSupport.Sine(48000,totalFrames,1000,0.2,1);
        double maxRendered=0;
        var scratch=new float[4096*2];
        for(int offset=0;offset<sine.Length;offset+=960)
        {
            int count=Math.Min(960,sine.Length-offset);
            capture.Push(sine.AsSpan(offset,count).ToArray());
            await Task.Delay(5);
            int got;
            while((got=monitor.Current!.ReadRendered(scratch))>0)
                for(int i=0;i<got*2;i++)maxRendered=Math.Max(maxRendered,Math.Abs(scratch[i]));
        }
        await TestSupport.Until(()=>
        {
            int got=monitor.Current!.ReadRendered(scratch);
            for(int i=0;i<got*2;i++)maxRendered=Math.Max(maxRendered,Math.Abs(scratch[i]));
            return maxRendered>0.35;
        },"monitor receives post-calibration +6 dB boosted signal");
        check(maxRendered>0.35&&maxRendered<0.45,$"Calibrated monitor peak reflects +6 dB correction (peak={maxRendered:F3})");
    }

    private static async Task CheckSuspendDisablesMonitoring(string root,Action<bool,string> check)
    {
        var capture=new FakeFactory();
        var monitor=new FakeLiveMonitorFactory();
        await using var engine=TestSupport.CreateEngine(root,capture,liveMonitorFactory:monitor);
        await engine.SelectDeviceAsync(capture.Device);
        await engine.SetLiveMonitorEnabledAsync(true);
        check(engine.LiveMonitor.Enabled,"Monitor enabled before suspend");

        await engine.SuspendMonitoringAsync();
        check(!engine.LiveMonitor.Enabled&&!engine.LiveMonitor.Active&&monitor.Current!.Disposed&&monitor.ActiveWatchers==0,
            "SuspendMonitoringAsync turns off live monitoring and releases output/watcher");
    }

    private static async Task CheckFaultIsolationFromRecording(string root,Action<bool,string> check)
    {
        // Case 1: Initial open failure during active recording leaves recording intact.
        {
            var capture=new FakeFactory();
            var monitor=new FakeLiveMonitorFactory{FailOpen=new IOException("没有可用的默认播放设备。")};
            await using var engine=TestSupport.CreateEngine(root,capture,liveMonitorFactory:monitor);
            await engine.SelectDeviceAsync(capture.Device);

            string dir=Path.Combine(root,"fault-open");
            Directory.CreateDirectory(dir);
            await engine.StartRecordingAsync(dir,RecordingMode.Shared);
            capture.Push(TestSupport.Sine(48000,480,1000,0.4,1));
            await TestSupport.Until(()=>engine.WrittenFrames>=480,"recording active before monitor open failure");

            bool threw=false;
            try{await engine.SetLiveMonitorEnabledAsync(true);}
            catch(IOException){threw=true;}
            check(threw&&!engine.LiveMonitor.Enabled&&engine.LiveMonitor.Error!=null,"Open failure throws and records error");
            check(engine.Status.State==AudioRunState.Recording,"Recording stays active after monitor open failure");

            capture.Push(TestSupport.Sine(48000,480,1000,0.4,1));
            await TestSupport.Until(()=>engine.WrittenFrames>=960,"recording continues writing after monitor open failure");
            await engine.StopRecordingAsync();
            check(engine.Status.FilePath!=null&&!engine.Status.Partial,"Recording saves cleanly after monitor open failure");
        }

        // Case 2: Mid-recording stream fault with no fallback playback device disables monitor, notifies, never breaks recording.
        {
            var capture=new FakeFactory();
            var monitor=new FakeLiveMonitorFactory();
            await using var engine=TestSupport.CreateEngine(root,capture,liveMonitorFactory:monitor);
            var faulted=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.LiveMonitorFaulted+=reason=>faulted.TrySetResult(reason);

            await engine.SelectDeviceAsync(capture.Device);
            await engine.SetLiveMonitorEnabledAsync(true);

            string dir=Path.Combine(root,"fault-stream");
            Directory.CreateDirectory(dir);
            await engine.StartRecordingAsync(dir,RecordingMode.Shared);
            capture.Push(TestSupport.Sine(48000,480,1000,0.4,1));
            await TestSupport.Until(()=>engine.WrittenFrames>=480,"recording active before output fault");

            monitor.FailOpen=new IOException("没有可用的默认播放设备。");
            var failedOutput=monitor.Current!;
            failedOutput.TriggerFault(new COMException("Device invalidated",unchecked((int)0x88890004)));

            string reason=await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(reason.Contains("播放设备")&&!engine.LiveMonitor.Enabled&&failedOutput.Disposed&&monitor.ActiveWatchers==0,
                "Unrecoverable output fault disables monitor, cleans up, and emits LiveMonitorFaulted");
            check(engine.Status.State==AudioRunState.Recording,"Recording stays running after unrecoverable monitor fault");

            capture.Push(TestSupport.Sine(48000,960,1000,0.4,1));
            await TestSupport.Until(()=>engine.WrittenFrames>=1440,"recording keeps writing after monitor fault");
            await engine.StopRecordingAsync();
            check(engine.Status.FilePath!=null&&!engine.Status.Partial,"Recording finishes non-partial after monitor fault");
        }

        // Case 3: Synchronous throw inside output.Write() during active recording never crashes ProcessingPipeline.
        {
            var capture=new FakeFactory();
            var monitor=new FakeLiveMonitorFactory();
            await using var engine=TestSupport.CreateEngine(root,capture,liveMonitorFactory:monitor);
            var faulted=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.LiveMonitorFaulted+=reason=>faulted.TrySetResult(reason);

            await engine.SelectDeviceAsync(capture.Device);
            await engine.SetLiveMonitorEnabledAsync(true);
            string dir=Path.Combine(root,"fault-write");
            Directory.CreateDirectory(dir);
            await engine.StartRecordingAsync(dir,RecordingMode.Shared);

            monitor.FailOpen=new IOException("输出故障");
            monitor.Current!.ThrowOnWrite=new InvalidOperationException("Injected write throw");
            capture.Push(TestSupport.Sine(48000,480,1000,0.4,1));
            await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await TestSupport.Until(()=>engine.WrittenFrames>=480,"pipeline survives synchronous monitor Write exception");
            check(engine.Status.State==AudioRunState.Recording&&!engine.LiveMonitor.Enabled,"Recording continues unaffected after Write throw");
            await engine.StopRecordingAsync();
            check(engine.Status.FilePath!=null&&!engine.Status.Partial,"Recording finishes cleanly after Write throw");
        }
    }

    private static async Task CheckDefaultDeviceHotSwapAndInvalidatedRecovery(string root,Action<bool,string> check)
    {
        var capture=new FakeFactory();
        var monitor=new FakeLiveMonitorFactory();
        await using var engine=TestSupport.CreateEngine(root,capture,liveMonitorFactory:monitor);
        int faultCount=0;
        engine.LiveMonitorFaulted+=_=>Interlocked.Increment(ref faultCount);

        await engine.SelectDeviceAsync(capture.Device);
        await engine.SetLiveMonitorEnabledAsync(true);
        var first=monitor.Current!;

        // Rapid burst of default-device change events coalesces cleanly and swaps output.
        for(int i=0;i<5;i++)monitor.TriggerDefaultDeviceChanged();
        await TestSupport.Until(()=>!ReferenceEquals(monitor.Current,first)&&first.Disposed,"default device change swaps output and disposes old");
        var second=monitor.Current!;
        check(monitor.OpenCount<=3&&engine.LiveMonitor.Enabled&&engine.LiveMonitor.Active&&faultCount==0,
            $"Default device burst coalesced cleanly (opens={monitor.OpenCount})");

        capture.Push(TestSupport.Sine(48000,480,1000,0.5,1));
        await TestSupport.Until(()=>second.WriteCount>0,"audio resumes on new default output");

        // AUDCLNT_E_DEVICE_INVALIDATED auto-recovers onto the next default device.
        second.TriggerFault(new COMException("Invalidated",unchecked((int)0x88890004)));
        await TestSupport.Until(()=>!ReferenceEquals(monitor.Current,second)&&second.Disposed,"invalidated stream auto-rebinds to healthy default device");
        var third=monitor.Current!;
        check(engine.LiveMonitor.Enabled&&engine.LiveMonitor.Active&&faultCount==0,"Single invalidated stream recovers silently");
        capture.Push(TestSupport.Sine(48000,480,1000,0.5,1));
        await TestSupport.Until(()=>third.WriteCount>0,"audio resumes after invalidated stream recovery");

        // Repeated immediate faults break out cleanly instead of looping forever.
        var terminalFault=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.LiveMonitorFaulted+=reason=>terminalFault.TrySetResult(reason);
        int opensBeforeTerminal=monitor.OpenCount;
        third.TriggerFault(new IOException("Persistent render failure"));
        string terminalReason=await terminalFault.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(terminalReason.Contains("Persistent render failure")&&!engine.LiveMonitor.Enabled&&monitor.ActiveWatchers==0&&monitor.OpenCount==opensBeforeTerminal,
            "Consecutive immediate faults stop retrying and disable monitor cleanly");
    }
}
