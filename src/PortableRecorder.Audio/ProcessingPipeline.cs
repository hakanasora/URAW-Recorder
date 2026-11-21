using System.Diagnostics;
using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
namespace PortableRecorder.Audio;

internal sealed class ProcessingPipeline:IAsyncDisposable
{
    private sealed record Work(CapturePacket? Packet,Action? Command,TaskCompletionSource? Completion);
    private sealed class ProcessingState(CalibrationPlan plan,CalibrationProcessor processor,AudioAnalyzer analyzer)
    {
        public CalibrationPlan Plan {get;}=plan;
        public CalibrationProcessor Processor {get;}=processor;
        public AudioAnalyzer Analyzer {get;}=analyzer;
        public int Segment,RecoveredDiscontinuities;
        public long Revision;
    }
    private abstract record WorkerPhase
    {
        private WorkerPhase(){}
        public sealed record AwaitingConfigure:WorkerPhase;
        public sealed record Running(CaptureInfo Info,ProcessingState State):WorkerPhase;
        public sealed record Faulted(Exception Error):WorkerPhase;
        public sealed record Disposed:WorkerPhase;
    }
    private abstract record AttachmentPhase
    {
        private AttachmentPhase(){}
        public sealed record Detached:AttachmentPhase;
        public sealed record Writing(IRecordingWriter Writer,long StartFrame,long HeldPeakStart):AttachmentPhase;
        public sealed record PauseDraining(IRecordingWriter Writer,long StartFrame,long CutoffFrame,long HeldPeakStart,TaskCompletionSource<Exception?> Boundary):AttachmentPhase;
        public sealed record Paused(IRecordingWriter Writer,long HeldPeakStart):AttachmentPhase;
        public sealed record StopDraining(IRecordingWriter Writer,long StartFrame,long CutoffFrame,bool ResetPeakOnDetach,long HeldPeakStart,TaskCompletionSource<Exception?>? Boundary):AttachmentPhase;
    }
    internal static readonly TimeSpan BoundaryWait=TimeSpan.FromSeconds(3);
    private readonly Channel<Work> _queue=Channel.CreateBounded<Work>(new BoundedChannelOptions(256){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly TaskCompletionSource<(CaptureInfo Info,CalibrationPlan Plan)> _configured=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready=>_ready.Task;
    private readonly Task _worker;
    private readonly Func<double> _gain;
    private readonly Action<Exception> _fault;
    private readonly Action<string> _diagnostic;
    private long _queuedBytes,_budget=64*1024*1024,_frames,_base,_delivered,_unconfirmedDiscontinuities;
    private WorkerPhase _workerPhase=new WorkerPhase.AwaitingConfigure();
    private AttachmentPhase _attachment=new AttachmentPhase.Detached();
    private ProcessingState? _processing;
    private CaptureInfo? _info;
    private long _diagnosticTime=Stopwatch.GetTimestamp(),_packetsSinceReport,_queueHighWater;
    private double _maxWakeGapMs,_maxBufferHoldMs;
    public CalibrationStatus Calibration
    {
        get
        {
            return Volatile.Read(ref _workerPhase) switch
            {
                WorkerPhase.Running running=>running.State.Plan.Status(running.Info.Format,running.State.Revision),
                _=>CalibrationPlan.Identity.Status(new(0,0,32,32,PcmEncoding.Float),0)
            };
        }
    }
    public AudioSnapshot Latest
    {
        get
        {
            if(Volatile.Read(ref _workerPhase) is not WorkerPhase.Running running)return AudioSnapshot.Empty();
            var processing=running.State;
            return processing.Analyzer.Latest with
            {
                Segment=processing.Segment,RecoveredDiscontinuities=processing.RecoveredDiscontinuities,
                CalibrationRevision=processing.Revision,ProcessingDelayFrames=processing.Plan.LatencyFrames
            };
        }
    }
    public int RecoveredDiscontinuities=>Latest.RecoveredDiscontinuities;
    public long RecordingUnconfirmedDiscontinuities=>Interlocked.Read(ref _unconfirmedDiscontinuities);
    internal Exception? Failure=>(Volatile.Read(ref _workerPhase) as WorkerPhase.Faulted)?.Error;
    public ProcessingPipeline(Func<double> gain,Action<Exception> fault,Action<string> diagnostic)
    {_gain=gain;_fault=fault;_diagnostic=diagnostic;_worker=Task.Run(Run);}
    public void Configure(CaptureInfo info,CalibrationPlan plan)
    {_budget=Math.Min(64L*1024*1024,info.Format.SampleRate*(long)info.Format.BlockAlign*2);_configured.TrySetResult((info,plan));}
    public bool Submit(CapturePacket packet)
    {
        if(Volatile.Read(ref _workerPhase) is WorkerPhase.Faulted or WorkerPhase.Disposed)return false;
        if(Interlocked.Add(ref _queuedBytes,packet.Length)>_budget){Interlocked.Add(ref _queuedBytes,-packet.Length);return false;}
        if(_queue.Writer.TryWrite(new(packet,null,null)))return true;
        Interlocked.Add(ref _queuedBytes,-packet.Length);return false;
    }
    private ProcessingState Prepare(CalibrationPlan plan,double? initialPeak=null)
        =>new(plan,new CalibrationProcessor(plan,_info!.Format.Channels),new AudioAnalyzer(_info.Format,_info.Epoch,initialPeak:initialPeak));
    private ProcessingState? Install(ProcessingState next,bool gap=false)
    {
        var previous=_processing;
        next.Segment=(previous?.Segment??-1)+1;
        next.RecoveredDiscontinuities=(previous?.RecoveredDiscontinuities??0)+(gap?1:0);
        next.Revision=(previous?.Revision??0)+1;
        _base=_delivered=_frames;
        _processing=next;
        Volatile.Write(ref _workerPhase,new WorkerPhase.Running(_info!,next));
        return previous;
    }
    private void Rebuild(CalibrationPlan plan,bool gap=false,bool preservePeak=false)
    {
        double? peak=gap||preservePeak?_processing?.Analyzer.Latest.HeldPeakDbfs:null;
        var next=Prepare(plan,peak);
        try{_processing?.Analyzer.Dispose();}
        catch{next.Analyzer.Dispose();throw;}
        Install(next,gap);
    }
    private async Task Run()
    {
        try
        {
            var configured=await _configured.Task;_info=configured.Info;Rebuild(configured.Plan);_ready.TrySetResult();
            await foreach(var work in _queue.Reader.ReadAllAsync())
            {
                if(work.Command!=null)
                {
                    try{work.Command();work.Completion!.TrySetResult();}catch(Exception e){work.Completion!.TrySetException(e);}continue;
                }
                using var packet=work.Packet!;
                _queueHighWater=Math.Max(_queueHighWater,Interlocked.Read(ref _queuedBytes));
                Interlocked.Add(ref _queuedBytes,-packet.Length);
                _packetsSinceReport++;
                _maxWakeGapMs=Math.Max(_maxWakeGapMs,packet.WakeGapMs);
                _maxBufferHoldMs=Math.Max(_maxBufferHoldMs,packet.BufferHoldMs);
                if(Stopwatch.GetElapsedTime(_diagnosticTime).TotalSeconds>=10)
                {
                    _diagnostic($"Capture health: epoch={_info.Epoch}; packets={_packetsSinceReport}; maxWakeMs={_maxWakeGapMs:F3}; maxBufferHoldMs={_maxBufferHoldMs:F3}; queueHighBytes={_queueHighWater}; budgetBytes={_budget}");
                    _diagnosticTime=Stopwatch.GetTimestamp();_packetsSinceReport=0;_queueHighWater=0;_maxWakeGapMs=0;_maxBufferHoldMs=0;
                }
                if(packet.Discontinuity)
                {
                    string detail=$" flags=0x{packet.NativeFlags:X}; position={packet.Position}; delta={packet.PositionDelta?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"unknown"}; qpc={packet.Qpc100ns}; frames={packet.Frames}; rate={_info.Format.SampleRate}; exclusive={_info.Exclusive}; raw={_info.Raw}; wakeMs={packet.WakeGapMs:F3}; bufferHoldMs={packet.BufferHoldMs:F3}; queuedBytes={Interlocked.Read(ref _queuedBytes)}; epoch={_info.Epoch}";
                    _diagnostic("Capture discontinuity:"+detail);
                    if(_attachment is AttachmentPhase.Writing or AttachmentPhase.PauseDraining or AttachmentPhase.StopDraining)
                    {
                        if(packet.PositionDelta!=0||(packet.NativeFlags&1)==0)throw new CaptureGapException(detail,packet.PositionDelta,packet.NativeFlags);
                        Interlocked.Increment(ref _unconfirmedDiscontinuities);
                    }
                    else Rebuild(_processing!.Plan,true);
                }
                int count=checked(packet.Frames*_info.Format.Channels);
                var data=ArrayPool<float>.Shared.Rent(count);var output=ArrayPool<float>.Shared.Rent(count);
                try
                {
                    PcmDecoder.Decode(packet.Data.AsSpan(0,packet.Length),data.AsSpan(0,count),_info.Format,packet.Silent);
                    var result=_processing!.Processor.Process(data.AsSpan(0,count),output.AsSpan(0,count),_gain());
                    _frames+=packet.Frames;Deliver(output.AsSpan(0,result.Frames*_info.Format.Channels),result);
                }
                finally{ArrayPool<float>.Shared.Return(data);ArrayPool<float>.Shared.Return(output);}
            }
        }
        catch(OperationCanceledException){}
        catch(Exception error)
        {
            error=FinalizeAttachmentOnFault(error);
            Volatile.Write(ref _workerPhase,new WorkerPhase.Faulted(error));
            _queue.Writer.TryComplete(error);
            _fault(error);
        }
        finally
        {
            var phase=Volatile.Read(ref _workerPhase);
            if(phase is WorkerPhase.Faulted faulted)_ready.TrySetException(faulted.Error);
            else _ready.TrySetCanceled();
            var error=(phase as WorkerPhase.Faulted)?.Error??new ObjectDisposedException(nameof(ProcessingPipeline));
            DetachAttachmentOnExit(error);
            while(_queue.Reader.TryRead(out var pending)){pending.Packet?.Dispose();pending.Completion?.TrySetException(error);}
            if(phase is not WorkerPhase.Faulted)Volatile.Write(ref _workerPhase,new WorkerPhase.Disposed());
        }
    }
    private IRecordingWriter? ActiveWriter=>_attachment switch
    {
        AttachmentPhase.Writing w=>w.Writer,
        AttachmentPhase.PauseDraining p=>p.Writer,
        AttachmentPhase.Paused p=>p.Writer,
        AttachmentPhase.StopDraining s=>s.Writer,
        _=>null
    };
    private Exception FinalizeAttachmentOnFault(Exception error)
    {
        switch(_attachment)
        {
            case AttachmentPhase.Writing w:
                _attachment=new AttachmentPhase.StopDraining(w.Writer,w.StartFrame,_frames,false,w.HeldPeakStart,new(TaskCreationOptions.RunContinuationsAsynchronously));
                break;
            case AttachmentPhase.PauseDraining p:
                _attachment=new AttachmentPhase.StopDraining(p.Writer,p.StartFrame,p.CutoffFrame,false,p.HeldPeakStart,p.Boundary);
                break;
            case AttachmentPhase.StopDraining s:
                _attachment=s with{ResetPeakOnDetach=false};
                break;
            default:
                DetachAttachmentOnExit(error);
                return error;
        }
        var stop=(AttachmentPhase.StopDraining)_attachment;
        var boundary=stop.Boundary;
        _attachment=stop with{ResetPeakOnDetach=false,Boundary=null};
        if(stop.Writer.Failure==null&&_processing!=null)
        {
            try{Drain();}
            catch(Exception drainError){error=AudioFailure.Combine(error,drainError);}
        }
        DetachWriter(resetPeak:false,boundary,error);
        return error;
    }
    private void DetachAttachmentOnExit(Exception error)
    {
        switch(_attachment)
        {
            case AttachmentPhase.PauseDraining p:DetachWriter(resetPeak:false,p.Boundary,error);break;
            case AttachmentPhase.StopDraining s:DetachWriter(resetPeak:false,s.Boundary,error);break;
            case AttachmentPhase.Writing or AttachmentPhase.Paused:_attachment=new AttachmentPhase.Detached();break;
        }
    }
    private void Deliver(ReadOnlySpan<float> samples,CalibrationOutput result)
    {
        int channels=_info!.Format.Channels;long first=_base+result.SourceStartFrame,last=first+result.Frames;
        if(ActiveWriter?.Failure is Exception disk)throw new IOException("录制文件写入失败："+AudioFailure.Describe(disk),disk);
        if(result.Frames>0)
        {
            switch(_attachment)
            {
                case AttachmentPhase.Writing w:
                    WriteInterval(samples,channels,first,w.Writer,w.StartFrame,last);
                    break;
                case AttachmentPhase.PauseDraining p:
                    WriteInterval(samples,channels,first,p.Writer,p.StartFrame,Math.Min(last,p.CutoffFrame));
                    break;
                case AttachmentPhase.StopDraining s:
                    WriteInterval(samples,channels,first,s.Writer,s.StartFrame,Math.Min(last,s.CutoffFrame));
                    break;
            }
            long heldPeakStart=_attachment switch
            {
                AttachmentPhase.Writing w=>w.HeldPeakStart,
                AttachmentPhase.PauseDraining p=>p.HeldPeakStart,
                AttachmentPhase.Paused p=>p.HeldPeakStart,
                AttachmentPhase.StopDraining s=>s.HeldPeakStart,
                _=>0
            };
            int skipHeldPeak=(int)Math.Clamp(heldPeakStart-first,0,result.Frames);
            _processing!.Analyzer.Process(samples,result.SourcePeak,last,skipHeldPeak);_delivered=last;
        }
        FinishBoundary();
    }
    private static void WriteInterval(ReadOnlySpan<float> samples,int channels,long first,IRecordingWriter writer,long aStart,long bEnd)
    {
        long a=Math.Max(first,aStart);
        if(bEnd>a&&!writer.TryWrite(samples.Slice(checked((int)(a-first)*channels),checked((int)(bEnd-a)*channels))))
            throw new IOException(writer.Failure is Exception failure
                ? "录制文件写入失败："+AudioFailure.Describe(failure)
                : "录制队列溢出，未静默丢样本。",writer.Failure);
    }
    private void FinishBoundary()
    {
        switch(_attachment)
        {
            case AttachmentPhase.PauseDraining p when _delivered>=p.CutoffFrame:
                _attachment=new AttachmentPhase.Paused(p.Writer,p.HeldPeakStart);
                p.Boundary.TrySetResult(null);
                break;
            case AttachmentPhase.StopDraining s when _delivered>=s.CutoffFrame:
                DetachWriter(s.ResetPeakOnDetach,s.Boundary,null);
                break;
        }
    }
    private void DetachWriter(bool resetPeak,TaskCompletionSource<Exception?>? boundary,Exception? error)
    {
        _attachment=new AttachmentPhase.Detached();
        if(error==null&&resetPeak)_processing!.Analyzer.ResetPeak();
        boundary?.TrySetResult(error);
    }
    private void Drain()
    {
        if(_processing==null)return;
        int delay=_processing.Plan.LatencyFrames;
        if(delay==0){Deliver(ReadOnlySpan<float>.Empty,new(0,0,0));return;}
        int count=delay*_info!.Format.Channels;
        var output=new float[count];var result=_processing.Processor.Flush(output);
        Deliver(output.AsSpan(0,result.Frames*_info.Format.Channels),result);
    }
    public Task Command(Action action)
    {
        var phase=Volatile.Read(ref _workerPhase);
        if(phase is WorkerPhase.Faulted faulted)return Task.FromException(faulted.Error);
        if(phase is WorkerPhase.Disposed)return Task.FromException(new ObjectDisposedException(nameof(ProcessingPipeline)));
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);return Send();
        async Task Send()
        {
            try{await _queue.Writer.WriteAsync(new(null,action,done));}
            catch(ChannelClosedException)
            {
                var closedPhase=Volatile.Read(ref _workerPhase);
                if(closedPhase is WorkerPhase.Faulted fault)ExceptionDispatchInfo.Throw(fault.Error);
                throw new ObjectDisposedException(nameof(ProcessingPipeline));
            }
            await done.Task;
        }
    }
    // AudioEngine serializes this transition with writer attachment and capture disposal.
    public Task ChangeCalibrationAsync(CalibrationPlan plan,Action persist)=>Task.Run(async()=>
    {
        var next=Prepare(plan);
        bool installed=false;
        try
        {
            if(Failure is Exception failure)ExceptionDispatchInfo.Throw(failure);
            persist();
            await Command(()=>
            {
                var previous=Install(next);
                installed=true;
                previous?.Analyzer.Dispose();
            });
        }
        finally
        {
            if(!installed)next.Analyzer.Dispose();
        }
    });
    public Task AttachAsync(IRecordingWriter writer)=>Command(()=>
    {
        if(_attachment is not AttachmentPhase.Detached)throw new InvalidOperationException("已有活动录音附着于管线。");
        _attachment=new AttachmentPhase.Writing(writer,_frames,_frames);
        Interlocked.Exchange(ref _unconfirmedDiscontinuities,0);
        _processing!.Analyzer.ResetPeak();
    });
    public async Task<Task<Exception?>> RequestPauseAsync()
    {
        Task<Exception?> done=Task.FromResult<Exception?>(null);
        await Command(()=>
        {
            if(_attachment is AttachmentPhase.Writing writing)
            {
                var boundary=new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _attachment=new AttachmentPhase.PauseDraining(writing.Writer,writing.StartFrame,_frames,writing.HeldPeakStart,boundary);
                TraceBoundary("pause",_frames);
                done=boundary.Task;
                FinishBoundary();
            }
            else if(_attachment is AttachmentPhase.PauseDraining draining)
            {
                done=draining.Boundary.Task;
            }
        });
        return done;
    }
    public Task ResumeAsync()=>Command(()=>
    {
        if(_attachment is AttachmentPhase.Paused paused)
            _attachment=new AttachmentPhase.Writing(paused.Writer,_frames,paused.HeldPeakStart);
    });
    private void TraceBoundary(string kind,long cutoff)
        =>_diagnostic($"Recording source boundary: {kind}; frame={cutoff}; delivered={_delivered}; delay={_processing!.Plan.LatencyFrames}");
    private void DrainAndDetach(AttachmentPhase.StopDraining stop,bool rebuild)
    {
        try
        {
            Drain();
            if(_attachment is not AttachmentPhase.Detached)throw new IOException("校准尾部未完成，录音可能不完整。");
            // Zero-draining advances convolution beyond real capture. Restart the monitoring clock.
            if(rebuild)Rebuild(_processing!.Plan,preservePeak:true);
        }
        catch(Exception error)
        {
            DetachWriter(resetPeak:false,stop.Boundary,error);
            throw;
        }
    }
    private async Task<Task<Exception?>> RequestDetachAsync(bool mathematicalDrain,bool resetPeak)
    {
        Task<Exception?> done=Task.FromResult<Exception?>(null);
        await Command(()=>
        {
            switch(_attachment)
            {
                case AttachmentPhase.Detached:
                    return;
                case AttachmentPhase.Paused:
                    DetachWriter(resetPeak,boundary:null,error:null);
                    return;
                case AttachmentPhase.Writing w:
                {
                    var boundary=new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var stop=new AttachmentPhase.StopDraining(w.Writer,w.StartFrame,_frames,resetPeak,w.HeldPeakStart,boundary);
                    _attachment=stop;
                    TraceBoundary("stop",stop.CutoffFrame);
                    done=boundary.Task;
                    if(mathematicalDrain)DrainAndDetach(stop,rebuild:true);else FinishBoundary();
                    return;
                }
                case AttachmentPhase.PauseDraining p:
                {
                    var stop=new AttachmentPhase.StopDraining(p.Writer,p.StartFrame,p.CutoffFrame,resetPeak,p.HeldPeakStart,p.Boundary);
                    _attachment=stop;
                    TraceBoundary("stop",stop.CutoffFrame);
                    done=p.Boundary.Task;
                    if(mathematicalDrain)DrainAndDetach(stop,rebuild:true);else FinishBoundary();
                    return;
                }
                case AttachmentPhase.StopDraining s:
                {
                    var stop=s;
                    if(s.ResetPeakOnDetach!=resetPeak||s.Boundary==null)
                        stop=s with{ResetPeakOnDetach=resetPeak,Boundary=s.Boundary??new(TaskCreationOptions.RunContinuationsAsynchronously)};
                    _attachment=stop;
                    done=stop.Boundary!.Task;
                    if(mathematicalDrain)DrainAndDetach(stop,rebuild:true);
                    return;
                }
            }
        });
        return done;
    }
    public async Task DetachAsync(bool mathematicalDrain,bool resetPeak,Task<Exception>? nativeFault)
    {
        Task<Exception?> boundary=await RequestDetachAsync(mathematicalDrain,resetPeak);
        Exception? error;
        try
        {
            if(mathematicalDrain)error=await boundary;
            else if(nativeFault==null)error=await boundary.WaitAsync(BoundaryWait);
            else
            {
                // Native input can stop while the engine holds its command lock waiting for this tail.
                var finished=await Task.WhenAny((Task)boundary,nativeFault).WaitAsync(BoundaryWait);
                if(finished==nativeFault)boundary=await RequestDetachAsync(mathematicalDrain:true,resetPeak);
                error=await boundary;
            }
        }
        catch(TimeoutException)
        {
            var timeout=new IOException("等待校准尾部时采集未继续，本次录音可能不完整。");
            try{error=await await RequestDetachAsync(mathematicalDrain:true,resetPeak);}
            catch(Exception cleanupError) when(Failure==null){error=cleanupError;}
            catch(Exception faultError){error=faultError;}
            throw error==null?timeout:AudioFailure.Combine(timeout,error);
        }
        if(error!=null)ExceptionDispatchInfo.Throw(error);
    }
    public Task ResetPeakAsync()=>Command(()=>_processing!.Analyzer.ResetPeak());
    public async ValueTask DisposeAsync()
    {_configured.TrySetCanceled();_queue.Writer.TryComplete();await _worker.ConfigureAwait(false);_ = _ready.Task.Exception;_processing?.Analyzer.Dispose();}
}
