using PortableRecorder.Desktop;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
        public void Push(long position,bool gap,int flags,long? delta,long qpc)
        {
            var data=ArrayPool<byte>.Shared.Rent(480*4);data.AsSpan(0,480*4).Clear();
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
