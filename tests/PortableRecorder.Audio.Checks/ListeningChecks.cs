using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using PortableRecorder.Audio;

static class ListeningChecks
{
    public static async Task Run(string root,Action<bool,string> Assert)
    {
        var factory=new FakeListeningFactory();
        await using var service=new ListeningService(factory);
        var source=new ListeningSource(ListeningScope.SystemBeforeEffects,"本机回环");
        Assert(service.Status.State==ListeningState.Stopped&&!service.Latest.Valid,"Listening begins stopped and empty");
        Assert(service.GainDb==0,"Listening gain defaults independently to zero");
        await service.StartAsync(source);
        var first=factory.Current!;
        await Task.Delay(600);
        Assert(service.Status.State==ListeningState.Waiting&&!service.Latest.Valid,"No render packets is waiting, not capture failure");
        for(int block=0;block<35;block++)first.Push(Sine(480,.1f));
        await Until(()=>service.Latest.FramePosition==16800,"all contiguous signal blocks");
        Assert(Math.Abs(service.Latest.RmsDbfs-(-23.0103))<.01,"Loopback reuses calibrated RMS conventions without microphone calibration");
        Assert(Math.Abs(service.Latest.HeldPeakDbfs!.Value-(-20))<.01,"History peak is sample peak");
        service.SetGain(30);first.Push(Sine(480,.1f));
        await Until(()=>service.Latest.OutputOverload,"float overrange");
        Assert(service.Latest.OutputPeakDbfs>9.99,"Listening gain is applied before analysis and does not clip floats");
        Assert(service.Latest.SourcePeakDbfs<(-19.99),"Original source peak is preserved separately");
        service.ResetPeak();Assert(service.Latest.HeldPeakDbfs==null,"Reset clears historical peak safely");
        service.SetGain(0);first.Push(Sine(480,.01f));
        await Until(()=>service.Latest.HeldPeakDbfs is <(-39.99),"new peak accumulation");
        double held=service.Latest.HeldPeakDbfs!.Value;
        await Task.Delay(850);
        Assert(service.Status.State==ListeningState.Waiting&&!service.Latest.Valid,"Stale waveform is hidden during packet silence");
        Assert(service.Latest.HeldPeakDbfs==held,"Waiting does not erase history peak");
        first.Push(new float[480],silent:true);
        await Until(()=>service.Latest.Valid,"silent packet");
        Assert(double.IsNegativeInfinity(service.Latest.RmsDbfs),"Returning silent packet resets old RMS window; silence differs from no input");
        long oldEpoch=service.Latest.Epoch;
        await service.StopAsync();
        Assert(first.Disposed&&!service.Latest.Valid&&service.Status.State==ListeningState.Stopped,"Stop releases native capture and invalidates meters");
        Assert(service.Latest.HeldPeakDbfs==null,"Stopping listening clears PEAK");
        await service.StartAsync(source);
        Assert(service.Latest.HeldPeakDbfs==null,"Starting listening has no prior PEAK before new audio");
        Assert(!first.Push(Sine(480,.9f)),"Retired capture callbacks cannot enter a new session");
        factory.Current!.Push(Sine(480,.001f));
        await Until(()=>service.Latest.Valid,"new session");
        Assert(service.Latest.Epoch!=oldEpoch&&service.Latest.HeldPeakDbfs<(-59.99),"New session has independent measurement history");
        factory.Current.Fail(new IOException("test endpoint removed"));
        await Until(()=>service.Status.State==ListeningState.Faulted&&!service.Latest.Valid,"native failure visibility");
        Assert(service.Status.Message.Contains("test endpoint removed"),"Native failure is visible, not synthesized data");
        await service.StopAsync();
        factory.FailOpening=true;bool failed=false;
        try{await service.StartAsync(source);}catch(IOException){failed=true;}
        Assert(failed&&service.Status.State==ListeningState.Faulted&&!service.Latest.Valid,"Opening failure is explicit and empty");
        factory.FailOpening=false;
        factory.FaultBeforeReturn=true;
        await service.StartAsync(source);
        await Until(()=>factory.Current!.Disposed,"early fault cleanup");
        Assert(QueuedPacketCount(service)==0,"Fault before native open returns drains accepted packets without Stop");
        await Task.Delay(300);
        Assert(service.Status.State==ListeningState.Faulted&&factory.Current!.Disposed,"Fault before native open returns releases the capture and remains faulted");
        factory.FaultBeforeReturn=false;
        await Task.WhenAll(service.StartAsync(source),service.StopAsync());
        Assert(service.Status.State==ListeningState.Stopped,"Concurrent commands serialize in arrival order");
        service.SetGain(100);Assert(service.GainDb==60,"Positive listening gain range");
        service.SetGain(-100);Assert(service.GainDb==(-60),"Negative listening gain range");
        bool invalid=false;try{service.SetGain(double.NaN);}catch(ArgumentOutOfRangeException){invalid=true;}
        Assert(invalid,"Nonfinite gain is rejected");
        Assert(!ListeningSources.IsSameApplication(new(ListeningScope.Application,"invalid",ProcessId:Environment.ProcessId,ProcessStartTicks:1)),"PID reuse guard compares creation time");
        await service.DisposeAsync();bool disposed=false;
        try{await service.StartAsync(source);}catch(ObjectDisposedException){disposed=true;}
        Assert(disposed,"Disposed listener cannot reopen capture");
        var readOnceFactory=new FakeListeningFactory{SuccessfulFormatReads=1};
        await using var readOnceService=new ListeningService(readOnceFactory);
        await readOnceService.StartAsync(source);
        readOnceFactory.Current!.Push(Sine(480,.1f));
        await Until(()=>readOnceService.Latest.Valid||readOnceFactory.Current.FormatReads>1,"first packet from capture with a single usable format read");
        Assert(readOnceService.Latest.Valid&&readOnceFactory.Current.FormatReads==1,
            "Listening worker and status reuse the initial capture format");
        readOnceFactory.Current.Fail(new IOException("test format already released"));
        await Until(()=>readOnceFactory.Current.Disposed,"read-once capture cleanup");
        Assert(readOnceService.Status is {State:ListeningState.Faulted,Format:not null}&&readOnceFactory.Current.FormatReads==1,
            "Runtime fault publishes the cached format and releases capture");

        var badFormatFactory=new FakeListeningFactory{SuccessfulFormatReads=0};
        await using var badFormatService=new ListeningService(badFormatFactory);
        IOException? formatError=null;
        try{await badFormatService.StartAsync(source);}catch(IOException error){formatError=error;}
        Assert(ReferenceEquals(formatError,badFormatFactory.Current!.FormatError)&&badFormatFactory.Current.FormatReads==1,
            "Unreadable capture format propagates the original failure without rereading it");
        Assert(badFormatService.Status is {State:ListeningState.Faulted,Format:null}&&
            badFormatService.Status.Message==formatError!.Message&&badFormatFactory.Current.Disposed,
            "Unreadable capture format publishes a fault and releases capture");

        var cleanupFactory=new FakeListeningFactory{SuccessfulFormatReads=0,FailDisposal=true};
        await using var cleanupService=new ListeningService(cleanupFactory);
        AggregateException? openingFailure=null;
        try{await cleanupService.StartAsync(source);}catch(AggregateException error){openingFailure=error;}
        var failedCapture=cleanupFactory.Current!;
        Assert(openingFailure?.InnerExceptions.SequenceEqual([failedCapture.FormatError,failedCapture.DisposeError])==true&&
            cleanupService.Status is {State:ListeningState.Faulted}&&cleanupService.Status.Message.StartsWith(failedCapture.FormatError.Message),
            "Capture cleanup failure preserves the first opening error and appends the cleanup error");
        cleanupFactory.SuccessfulFormatReads=1;cleanupFactory.FailDisposal=false;
        await cleanupService.StartAsync(source);
        Assert(cleanupService.Status.State==ListeningState.Waiting,
            "Opening failure followed by a failed release does not leave a disposed session attached");

        await CheckWatchdogUsesCurrentPacket(source,Assert);

        var backlogFactory=new FakeListeningFactory{BurstBeforeReturn=true};
        await using var backlogService=new ListeningService(backlogFactory);
        await backlogService.StartAsync(source);
        await Until(()=>backlogService.Latest.FramePosition==64*480,"bounded pre-start backlog");
        backlogFactory.Current!.Push(new float[480],silent:true);
        await Until(()=>backlogService.Latest.FramePosition==65*480,"post-drop boundary");
        Assert(double.IsNegativeInfinity(backlogService.Latest.RmsDbfs)&&backlogService.Latest.Segment>0,
            "Queue overflow resets the first accepted post-gap block, not an older queued block");
    }
    private static async Task CheckWatchdogUsesCurrentPacket(ListeningSource source,Action<bool,string> Assert)
    {
        using var clock=new WatchdogClock();
        var factory=new FakeListeningFactory();
        await using var service=new ListeningService(factory,clock);
        await service.StartAsync(source);
        var capture=factory.Current!;
        capture.Push(Sine(480,.1f));
        try
        {
            // The watchdog has read the old observation, and the time query is paused.
            await clock.ObservedOldPacket.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var oldObservation=SessionField(service,"_observation");
            capture.Push(Sine(480,.1f));
            await Until(()=>!ReferenceEquals(oldObservation,SessionField(service,"_observation")),"fresh packet status publication");
        }
        finally{clock.ResumeWatchdog.Set();}

        await Until(()=>clock.CheckedAgain.Task.IsCompleted||service.Status.State==ListeningState.Waiting,
            "watchdog idle check finished");
        Assert(service.Status.State==ListeningState.Listening&&service.Latest.FramePosition==960,
            "A stale watchdog observation cannot hide the newer packet");
    }

    private sealed class WatchdogClock:TimeProvider,IDisposable
    {
        private int _reads;
        public TaskCompletionSource ObservedOldPacket {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CheckedAgain {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ResumeWatchdog {get;}=new();

        public override long GetTimestamp()
        {
            int read=Interlocked.Increment(ref _reads);
            if(read==1)return 1; // Arrival of the first packet.
            if(read==2)
            {
                ObservedOldPacket.SetResult();
                if(!ResumeWatchdog.Wait(TimeSpan.FromSeconds(4)))throw new TimeoutException("Watchdog clock was not resumed.");
            }
            if(read>3)CheckedAgain.TrySetResult(); // The watchdog has completed the stale check.
            return TimestampFrequency+1; // First packet is stale; subsequent arrivals are current.
        }

        public void Dispose()=>ResumeWatchdog.Dispose();
    }
    private static object SessionField(ListeningService service,string field)
    {
        const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
        var session=typeof(ListeningService).GetField("_session",fields)!.GetValue(service)!;
        return session.GetType().GetField(field,fields)!.GetValue(session)!;
    }
    private static int QueuedPacketCount(ListeningService service)
    {
        var channel=SessionField(service,"_packets");
        var reader=channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
        return (int)reader.GetType().GetProperty("Count")!.GetValue(reader)!;
    }
    private static float[] Sine(int frames,float amplitude)=>Enumerable.Range(0,frames).Select(i=>(float)(amplitude*Math.Sin(2*Math.PI*1000*i/48000))).ToArray();
    private static async Task Until(Func<bool> condition,string name)
    {
        var timeout=System.Diagnostics.Stopwatch.StartNew();
        while(!condition()){if(timeout.Elapsed.TotalSeconds>4)throw new Exception("Timeout: "+name);await Task.Delay(10);}
    }
    private sealed class FakeListeningFactory:IListeningCaptureFactory
    {
        public FakeListeningCapture? Current;public bool FailOpening,FaultBeforeReturn,BurstBeforeReturn,FailDisposal;
        public int SuccessfulFormatReads=int.MaxValue;
        public Task<IListeningCaptureSession> OpenAsync(ListeningSource source,Func<CapturePacket,bool> submit,Action<Exception> fault)
        {
            if(FailOpening)throw new IOException("test opening failed");
            Current=new(submit,fault,SuccessfulFormatReads,FailDisposal);
            if(BurstBeforeReturn)for(int i=0;i<65;i++)Current.Push(Sine(480,.1f));
            if(FaultBeforeReturn)
            {
                if(!Current.Push(Sine(480,.1f)))throw new InvalidOperationException("Expected a packet to be accepted before the opening fault.");
                fault(new IOException("test early native fault"));
            }
            return Task.FromResult<IListeningCaptureSession>(Current);
        }
    }
    private sealed class FakeListeningCapture(Func<CapturePacket,bool> submit,Action<Exception> fault,
        int successfulFormatReads,bool failDisposal):IListeningCaptureSession
    {
        private readonly AudioFormat _format=new(48000,1,32,32,PcmEncoding.Float);
        private int _formatReads;
        public IOException FormatError {get;}=new("test capture format unavailable");
        public IOException DisposeError {get;}=new("test capture cleanup failed");
        public int FormatReads=>Volatile.Read(ref _formatReads);
        public AudioFormat Format=>Interlocked.Increment(ref _formatReads)<=successfulFormatReads?_format:throw FormatError;
        public volatile bool Disposed;private long _position;
        public bool Push(float[] samples,bool silent=false)
        {
            int length=samples.Length*4;var bytes=ArrayPool<byte>.Shared.Rent(length);
            MemoryMarshal.AsBytes(samples.AsSpan()).CopyTo(bytes);var packet=new CapturePacket(bytes,length,samples.Length,_position,silent,false);
            _position+=samples.Length;bool accepted=submit(packet);if(!accepted)packet.Dispose();return accepted;
        }
        public void Fail(Exception e)=>fault(e);
        public ValueTask DisposeAsync()
        {
            Disposed=true;
            return failDisposal?ValueTask.FromException(DisposeError):ValueTask.CompletedTask;
        }
    }
}
