using PortableRecorder.Desktop;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using PortableRecorder.Audio;

using static TestSupport;

static class LifecycleChecks
{
    public static async Task Run(string root,Action<bool,string> Assert)
    {
        void Near(double actual,double expected,double tolerance,string name)=>Assert(Math.Abs(actual-expected)<=tolerance,$"{name}: {actual} expected {expected} ± {tolerance}");
        {
            var factory=new FakeFactory();await using var engine=CreateEngine(root,factory);
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
            var factory=new FakeFactory{DenySharedRaw=true};await using var engine=CreateEngine(root,factory);
            await engine.SelectDeviceAsync(factory.Device);Assert(engine.RawDecisionPending,"RAW decision required");
            await engine.DecideRawFallbackAsync(true);Assert(!factory.Current!.Info.Raw,"Explicit ordinary monitoring");
            bool denied=false;try{await engine.StartRecordingAsync(root,RecordingMode.Shared);}catch(IOException){denied=true;}
            Assert(denied && !factory.Current!.Info.Raw && !engine.RawDecisionPending,"Consent never permits ordinary recording");
        }
        {
            var factory=new FakeFactory();await using var engine=CreateEngine(root,factory);await engine.SelectDeviceAsync(factory.Device);
            factory.Push(new float[480]);await Until(()=>engine.Latest.Valid,"fault preframe");factory.Push(new float[480],true);
            await Until(()=>engine.Latest.RecoveredDiscontinuities==1,"monitoring discontinuity recovery");
            Assert(engine.Status.State==AudioRunState.Monitoring && engine.Latest.Valid,"One monitoring glitch cannot kill capture");
            Assert(engine.Latest.Segment==1,"Analysis segment changes across gap");
        }
        {
            var factory=new FakeFactory();await using var engine=CreateEngine(root,factory);await engine.SelectDeviceAsync(factory.Device);
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
            var factory=new FakeFactory{DenySharedRaw=true};await using var engine=CreateEngine(root,factory);
            await engine.SelectDeviceAsync(factory.Device);await engine.DecideRawFallbackAsync(false);
            Assert(engine.Status.State==AudioRunState.Stopped && !engine.Latest.Valid,"RAW dialog close stops monitoring");
            await engine.SelectDeviceAsync(factory.Device);await engine.DecideRawFallbackAsync(true);
            await engine.SelectDeviceAsync(new("other","Other device"));Assert(engine.RawDecisionPending,"Ordinary consent expires on device change");
        }
        {
            var factory=new FakeFactory();await using var engine=CreateEngine(root,factory);
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
            var factory=new FakeFactory();var disk=new FailingWriter();await using var engine=CreateEngine(root,factory,writerFactory:(_,_,_)=>disk);
            await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(root,RecordingMode.Shared);
            factory.Push(new float[480]);await Until(()=>engine.WrittenFrames==480,"disk fixture first block");
            await engine.PauseAsync(true);disk.Fail();factory.Push(new float[480]);
            await Until(()=>engine.Status.State==AudioRunState.Faulted,"Disk failure while paused");
            Assert(engine.Status.Partial && !engine.Latest.Valid,"Failed file is partial, no stale data");
        }
        {
            var factory=new FakeFactory();var engine=CreateEngine(root,factory);
            await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(root,RecordingMode.Exclusive);
            string path=engine.Status.FilePath!;factory.Push(Sine(48000,1200,440,.1));
            await engine.DisposeAsync();
            using var file=new WaveFileReader(path);Assert(file.Length==1200*4,"Closing active recording drains final samples and header");
            Assert(factory.Current!.Closed,"Closing releases exclusive capture");
        }
        {
            var factory=new FakeFactory();var writer=new FailingWriter{Reject=true};
            await using var engine=CreateEngine(root,factory,writerFactory:(_,_,_)=>writer);
            await engine.SelectDeviceAsync(factory.Device);await engine.StartRecordingAsync(root,RecordingMode.Shared);factory.Push(new float[480]);
            await Until(()=>engine.Status.State==AudioRunState.Faulted,"Recording queue rejects data");
            Assert(engine.Status.Partial && !engine.Latest.Valid,"Recording overflow stops, never silently drops");
        }
        // Discard is paused-only, deletes exactly the current file, and releases exclusive capture.
        {
            var factory=new FakeFactory();await using var engine=CreateEngine(root,factory);
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
            var factory=new FakeFactory();await using var engine=CreateEngine(root,factory);
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
    }
}
