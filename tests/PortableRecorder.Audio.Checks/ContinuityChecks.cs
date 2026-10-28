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
}
