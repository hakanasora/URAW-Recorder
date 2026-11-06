using PortableRecorder.Desktop;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using PortableRecorder.Audio;

using static TestSupport;

static class ContinuityChecks
{
    public static async Task Run(string root,Action<bool,string> Assert)
    {
        void Near(double actual,double expected,double tolerance,string name)=>Assert(Math.Abs(actual-expected)<=tolerance,$"{name}: {actual} expected {expected} ± {tolerance}");
        {
            var tracker=new CaptureContinuity();
            Assert(!tracker.Observe(480,0,1).Discontinuous,"Startup discontinuity flag is a stream boundary");
            Assert(!tracker.Observe(480,480,0).Discontinuous,"Continuous native frame position");
            var flagged=tracker.Observe(480,960,1);Assert(flagged.Discontinuous && flagged.Delta==0,"Driver glitch retained even with contiguous position");
            var jump=tracker.Observe(480,1920,0);Assert(jump.Discontinuous && jump.Delta==480,"Positive frame gap diagnosed");
            var backward=tracker.Observe(480,2000,0);Assert(backward.Discontinuous && backward.Delta==-400,"Backward frame jump diagnosed");
            Assert(!tracker.Observe(480,999999,4).Discontinuous,"Timestamp-error packet cannot establish continuity evidence");
            Assert(!tracker.Observe(480,2480,0).Discontinuous,"Ignore invalid prior timestamp anchor");
            Assert(!tracker.Observe(0,0,1).Discontinuous,"Empty packet does not change continuity state");
            Assert(!tracker.Observe(480,2960,0).Discontinuous,"Continuity continues after empty packet");
        }
        {
            // Native gap evidence must survive the worker boundary. A generic gap message
            // cannot distinguish a scheduler overrun from a timestamp-only driver report.
            var factory=new MetadataCaptureFactory();
            var diagnostics=new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var engine=new AudioEngine(factory,new CalibrationLibrary(Path.Combine(root,"metadata-calibrations")),
                inputFormats:new InputFormatPreferences(Path.Combine(root,"metadata-formats.json")));
            engine.Diagnostic+=diagnostics.Enqueue;
            await engine.SelectDeviceAsync(factory.Device);
            factory.Push(0,false,0,null,123356789);
            await Until(()=>engine.Latest.Valid,"native metadata initial signal");
            factory.Push(720,true,1,240,123456789);
            await Until(()=>engine.Latest.RecoveredDiscontinuities==1,"native metadata gap recovery");
            string detail=diagnostics.Single(message=>message.StartsWith("Capture discontinuity:"));
            foreach(string evidence in new[]{"flags=0x1","delta=240","qpc=123456789","rate=48000","exclusive=False","raw=True"})
                Assert(detail.Contains(evidence,StringComparison.Ordinal),"Gap diagnostic preserves native evidence: "+evidence);
            Assert(engine.Status.State==AudioRunState.Monitoring,"Native metadata diagnostics do not interrupt idle monitoring");
        }
        {
            var factory=new FakeFactory();await using var engine=CreateEngine(root,factory);var diagnostics=new List<string>();engine.Diagnostic+=message=>diagnostics.Add(message);
            await engine.SelectDeviceAsync(factory.Device);var original=factory.Current!;
            factory.Push(Enumerable.Repeat(.5f,480).ToArray());await Until(()=>engine.Latest.Valid,"glitch initial signal");
            double held=engine.Latest.HeldPeakDbfs!.Value;
            for(int i=1;i<=20;i++)
            {
                factory.Push(new float[480],true);int expected=i;
                await Until(()=>engine.Latest.RecoveredDiscontinuities==expected,"repeated idle glitches remain recoverable");
            }
            Assert(ReferenceEquals(original,factory.Current) && engine.Status.State==AudioRunState.Monitoring,"Idle recovery retains device and RAW stream");
            Assert(double.IsNegativeInfinity(engine.Latest.RmsDbfs),"Gap resets rolling RMS, no fabricated old energy");
            Assert(engine.Latest.Spectrum==null && engine.Latest.FrequencyHz==null,"Gap cannot connect old FFT or pitch windows");
            Near(engine.Latest.HeldPeakDbfs!.Value,held,.001,"Gap recovery retains held peak");
            Assert(engine.Latest.FramePosition==480*21,"No fake samples inserted on gap");
            Assert(diagnostics.Count(s=>s.StartsWith("Capture discontinuity:"))==20,"Each gap records detailed diagnostics");
            await engine.StartRecordingAsync(root,RecordingMode.Shared);string path=engine.Status.FilePath!;
            factory.Push(new float[480]);await Until(()=>engine.WrittenFrames==480,"recording after monitoring gap");
            factory.Push(new float[480],true);
            await Until(()=>engine.Status.Partial && engine.Status.State==AudioRunState.Monitoring,"Recording gap finalizes partial file and restores shared monitor");
            using(var file=new WaveFileReader(path))Assert(file.Length==480*4,"Gap packet never silently appended to recording");
            factory.Push(new float[480]);await Until(()=>engine.Latest.Valid,"Real samples resume after recording fault");
            Assert(engine.WrittenFrames==480,"Recording never automatically resumes after gap");
        }
        {
            var factory=new FakeFactory();await using var engine=CreateEngine(root,factory);
            await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(root,RecordingMode.Exclusive);string path=engine.Status.FilePath!;
            factory.Push(new float[480]);await Until(()=>engine.WrittenFrames==480,"pause gap initial recording");
            await engine.PauseAsync(true);factory.Push(new float[480],true);
            await Until(()=>engine.Latest.RecoveredDiscontinuities==1,"Pause monitoring gap recovers");
            Assert(engine.Status.State==AudioRunState.Paused && engine.WrittenFrames==480 && factory.Current!.Info.Exclusive,"Paused gap keeps file and capture mode");
            await engine.PauseAsync(false);factory.Push(new float[240]);await Until(()=>engine.WrittenFrames==720,"resume after pause gap");
            await engine.StopRecordingAsync();using var file=new WaveFileReader(path);Assert(file.Length==720*4,"Pause gap never writes gap or silence to file");
        }
        await TrustedFlagContinues(root,RecordingMode.Shared,Assert);
        await TrustedFlagContinues(root,RecordingMode.Exclusive,Assert);
        await TrustedFlagDuringPauseBoundary(root,Assert);
        await RecordingGapDiagnosis(root,0,0,"音频数据不连续",Assert);
        await RecordingGapDiagnosis(root,240,0,"帧位置前跳 240 帧",Assert);
        await RecordingGapDiagnosis(root,-240,0,"帧位置回跳，差值 -240 帧",Assert);
        await RecordingGapDiagnosis(root,null,1,"驱动报告异常，缺帧未确认",Assert);
        await RecordingGapDiagnosis(root,null,0,"音频数据不连续",Assert);
        await GapDuringPendingPauseAndStop(root,RecordingMode.Shared,Assert);
        await GapDuringPendingPauseAndStop(root,RecordingMode.Exclusive,Assert);
    }

    private static async Task TrustedFlagContinues(string root,RecordingMode mode,Action<bool,string> Assert)
    {
        var factory=new MetadataCaptureFactory();
        await using var engine=new AudioEngine(factory,new CalibrationLibrary(Path.Combine(root,$"trusted-calibrations-{mode}")),
            inputFormats:new InputFormatPreferences(Path.Combine(root,$"trusted-formats-{mode}.json")));
        await engine.SelectDeviceAsync(factory.Device);
        await engine.StartRecordingAsync(root,mode);
        string path=engine.Status.FilePath!;long epoch=engine.Status.Capture!.Epoch;
        factory.Push(0,false,0,null,1,.1f);
        factory.Push(480,true,1,0,2,.2f);
        factory.Push(960,false,0,0,3,.3f);
        await Until(()=>engine.WrittenFrames==1440,"trusted flag continues writing");
        Assert(engine.RecordingUnconfirmedDiscontinuities==1 && engine.Status.Capture!.Epoch==epoch && engine.Status.State==AudioRunState.Recording,"Trusted flag keeps current recording and epoch");
        await engine.StopRecordingAsync();
        using var wave=new WaveFileReader(path);
        var samples=new byte[1440*4];int read=wave.Read(samples,0,samples.Length);
        Assert(read==samples.Length && Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(samples.AsSpan(0,4))-.1f)<.0001 && Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(samples.AsSpan(480*4,4))-.2f)<.0001 && Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(samples.AsSpan(960*4,4))-.3f)<.0001,"WAV retains packet order including flagged packet");
        using var metadata=JsonDocument.Parse(File.ReadAllBytes(engine.GetRecordingMetadataPath(path)));
        Assert(metadata.RootElement.GetProperty("Completed").GetBoolean() && metadata.RootElement.GetProperty("UnconfirmedDiscontinuities").GetInt64()==1,"Saved metadata retains unconfirmed count after monitor reopen");
        await engine.StartRecordingAsync(root,RecordingMode.Shared);
        Assert(engine.RecordingUnconfirmedDiscontinuities==0,"New recording resets unconfirmed count");
        await engine.StopRecordingAsync();
    }

    private static async Task TrustedFlagDuringPauseBoundary(string root,Action<bool,string> Assert)
    {
        var factory=new MetadataCaptureFactory();
        await using var engine=new AudioEngine(factory,new CalibrationLibrary(Path.Combine(root,"trusted-boundary-calibrations")),
            inputFormats:new InputFormatPreferences(Path.Combine(root,"trusted-boundary-formats.json")));
        await engine.SelectDeviceAsync(factory.Device);
        string calibrationPath=Path.Combine(root,"trusted-boundary.cal");
        File.WriteAllText(calibrationPath,"1 -6\n24000 -6");
        var calibration=await engine.ImportCalibrationAsync(calibrationPath);
        await engine.SetCalibrationAsync(calibration.Id);
        await engine.StartRecordingAsync(root,RecordingMode.Shared);
        string path=engine.Status.FilePath!;long epoch=engine.Status.Capture!.Epoch;
        var boundary=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Diagnostic+=message=>{if(message.StartsWith("Recording source boundary: pause",StringComparison.Ordinal))boundary.TrySetResult();};
        factory.Push(0,false,0,null,1,.25f);
        var pause=engine.PauseAsync(true);
        await boundary.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(!pause.IsCompleted,"Calibrated pause awaits source tail");
        factory.Push(480,true,1,0,2,.5f);
        for(int i=2;i<40;i++)factory.Push(i*480,false,0,0,i+1,.5f);
        await pause.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(engine.Status.State==AudioRunState.Paused && engine.Status.Capture!.Epoch==epoch && engine.RecordingUnconfirmedDiscontinuities==1,"Trusted flag completes pending pause on same capture");
        await engine.StopRecordingAsync();
        using var wave=new WaveFileReader(path);
        Assert(wave.Length==480*sizeof(float),"Pending pause retains exactly its source interval");
        using var metadata=JsonDocument.Parse(File.ReadAllBytes(engine.GetRecordingMetadataPath(path)));
        Assert(metadata.RootElement.GetProperty("Completed").GetBoolean() && metadata.RootElement.GetProperty("UnconfirmedDiscontinuities").GetInt64()==1,"Pending boundary warning survives in completed metadata");
    }

    private static async Task RecordingGapDiagnosis(string root,long? delta,int flags,string expected,Action<bool,string> Assert)
    {
        var factory=new MetadataCaptureFactory();
        await using var engine=new AudioEngine(factory,new CalibrationLibrary(Path.Combine(root,"recording-metadata-calibrations")),
            inputFormats:new InputFormatPreferences(Path.Combine(root,"recording-metadata-formats.json")));
        await engine.SelectDeviceAsync(factory.Device);
        await engine.StartRecordingAsync(root,RecordingMode.Shared);
        string path=engine.Status.FilePath!;
        factory.Push(0,false,0,null,1);
        await Until(()=>engine.WrittenFrames==480,"diagnostic recording initial packet");
        factory.Push(480+(delta??0),true,flags,delta,2);
        await Until(()=>engine.Status.State==AudioRunState.Monitoring && engine.Status.Partial,"diagnostic recording stopped");
        Assert(engine.Status.Message.StartsWith(expected,StringComparison.Ordinal),"Recovery status leads with diagnosis: "+expected);
        Assert(engine.Status.FilePath==path,"Diagnosis keeps partial file path");
        using var wave=new WaveFileReader(path);
        Assert(wave.Length==480*sizeof(float),"Diagnosis preserves accepted frames only");
        using var metadata=JsonDocument.Parse(File.ReadAllBytes(engine.GetRecordingMetadataPath(path)));
        string error=metadata.RootElement.GetProperty("Error").GetString()!;
        Assert(!metadata.RootElement.GetProperty("Completed").GetBoolean() && error.Contains(expected,StringComparison.Ordinal),"Partial metadata records diagnosis");
        Assert(error.Contains($"flags=0x{flags:X}") && error.Contains($"delta={delta?.ToString()??"unknown"}"),"Partial metadata retains native evidence");
    }

    private static async Task GapDuringPendingPauseAndStop(string root,RecordingMode mode,Action<bool,string> Assert)
    {
        var factory=new FakeFactory();
        await using var engine=CreateEngine(root,factory);
        await engine.SelectDeviceAsync(factory.Device);
        string calibrationPath=Path.Combine(root,$"gap-stop-{mode}.cal");
        File.WriteAllText(calibrationPath,"1 -6\n24000 -6");
        var calibration=await engine.ImportCalibrationAsync(calibrationPath);
        await engine.SetCalibrationAsync(calibration.Id);
        const int acceptedFrames=100;
        Assert(engine.Calibration.LatencyFrames>acceptedFrames,"Calibrated pause requires more source frames");
        await engine.StartRecordingAsync(root,mode);
        var capture=factory.Current!;
        string path=engine.Status.FilePath!;

        var pauseBoundary=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveringStatus=new TaskCompletionSource<AudioStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Diagnostic+=message=>
        {
            if(message.StartsWith("Recording source boundary: pause",StringComparison.Ordinal))pauseBoundary.TrySetResult();
            if(capture.Closed&&message.StartsWith("Capture ready:",StringComparison.Ordinal))recoveringStatus.TrySetResult(engine.Status);
        };
        factory.Push(Enumerable.Repeat(.25f,acceptedFrames).ToArray());
        var pause=engine.PauseAsync(true);
        await pauseBoundary.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(!pause.IsCompleted,"Pause still owns the command while it waits for the calibrated tail");
        var stop=engine.StopRecordingAsync();
        Assert(!stop.IsCompleted,"Stop is queued before the capture gap");
        factory.Push(new float[20],true);

        Exception? pauseError=null,stopError=null;
        try{await pause;}catch(Exception error){pauseError=error;}
        try{await stop;}catch(Exception error){stopError=error;}
        Assert(pauseError is CaptureGapException,"Pending pause reports the capture gap");
        using var wave=new WaveFileReader(path);
        Assert(wave.Length==acceptedFrames*sizeof(float),"WAV preserves exactly the accepted frames");
        using var json=JsonDocument.Parse(File.ReadAllBytes(engine.GetRecordingMetadataPath(path)));
        var metadata=json.RootElement;
        Assert(!metadata.GetProperty("Completed").GetBoolean(),"Queued stop marked the interrupted recording complete");
        Assert(metadata.GetProperty("Frames").GetInt64()==acceptedFrames,"Partial metadata reports the WAV frame count");
        Assert(stopError is CaptureGapException && metadata.GetProperty("Error").GetString() is string errorDetail
            && errorDetail.Contains(AudioEngine.Friendly(stopError),StringComparison.Ordinal),"Stop and metadata report the original capture gap");
        var recovering=await recoveringStatus.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(recovering.Partial && recovering.FilePath==path,"Stopping retains the partial file path while monitoring recovers");
        await Until(()=>engine.Status.State==AudioRunState.Monitoring
            && engine.Status.Capture is {Exclusive:false} current && current.Epoch!=capture.Info.Epoch,
            $"shared monitoring resumes on a fresh capture after the {mode} recording gap");
        Assert(capture.Closed && engine.Status.Partial && engine.Status.FilePath==path,"Recovered monitoring retains the partial recording path");

        factory.Current!.TriggerFault();
        await Until(()=>engine.Status.State==AudioRunState.Faulted,"Unrelated fault on the replacement capture");
        Assert(!engine.Status.Partial && engine.Status.FilePath==null,"New capture failure does not claim the previous recording");
    }
    private sealed class MetadataCaptureFactory : ICaptureFactory
    {
        public AudioDevice Device {get;}=new("metadata","Native evidence input",true);
        private readonly AudioFormat _format=new(48000,1,32,32,PcmEncoding.Float);
        private Func<CapturePacket,bool>? _submit;
        public IReadOnlyList<AudioDevice> Enumerate()=>[Device];
        public Task<InputFormatOptions> QueryFormatsAsync(AudioDevice device)=>Task.FromResult(new InputFormatOptions(_format,[_format]));
        public Task<IAudioCaptureService> OpenAsync(AudioDevice device,bool exclusive,bool raw,long epoch,
            Func<CapturePacket,bool> packet,Action<Exception> fault,AudioFormat? preferredFormat=null)
        {
            _submit=packet;
            return Task.FromResult<IAudioCaptureService>(new MetadataCapture(new(device,_format,exclusive,raw,epoch)));
        }
        public void Push(long position,bool gap,int flags,long? delta,long qpc,float sample=0)
        {
            var data=ArrayPool<byte>.Shared.Rent(480*4);for(int i=0;i<480;i++)BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i*4,4),sample);
            var packet=new CapturePacket(data,480*4,480,position,false,gap,flags,delta,qpc);
            if(!_submit!(packet)){packet.Dispose();throw new IOException("Metadata fixture queue overflow");}
        }
    }
    private sealed class MetadataCapture(CaptureInfo info):IAudioCaptureService
    {
        public CaptureInfo Info=>info;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

}
