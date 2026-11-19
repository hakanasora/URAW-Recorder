using System.Diagnostics;
using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
namespace PortableRecorder.Audio;

internal sealed class ProcessingPipeline:IAsyncDisposable
{
    private sealed record Work(CapturePacket? Packet,Action? Command,TaskCompletionSource? Completion);
    private enum RecordingState { Detached, Writing, PauseDraining, Paused, StopDraining }
    internal static readonly TimeSpan BoundaryWait=TimeSpan.FromSeconds(3);
    private readonly Channel<Work> _queue=Channel.CreateBounded<Work>(new BoundedChannelOptions(256){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly TaskCompletionSource<(CaptureInfo Info,CalibrationPlan Plan)> _configured=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready=>_ready.Task;
    private readonly Task _worker;
    private readonly Func<double> _gain;
    private readonly Action<Exception> _fault;
    private readonly Action<string> _diagnostic;
    private long _queuedBytes,_budget=64*1024*1024,_frames,_base,_delivered,_revision,_unconfirmedDiscontinuities;
    private AudioAnalyzer? _analyzer;
    private CalibrationProcessor? _processor;
    private CalibrationPlan _plan=CalibrationPlan.Identity;
    private CaptureInfo? _info;
    private IRecordingWriter? _writer;
    private RecordingState _recording;
    private long _recordingStart,_cutoff;
    private TaskCompletionSource<Exception?>? _boundary;
    private bool _resetPeakOnDetach;
    private long _heldPeakStart;
    private Exception? _failure;
    private long _diagnosticTime=Stopwatch.GetTimestamp(),_packetsSinceReport,_queueHighWater;
    private double _maxWakeGapMs,_maxBufferHoldMs;
    internal CalibrationPlan Plan=>_plan;
    public CalibrationStatus Calibration=>_plan.Status(_info?.Format??new(0,0,32,32,PcmEncoding.Float),_revision);
    public AudioSnapshot Latest=>(Volatile.Read(ref _analyzer)?.Latest??AudioSnapshot.Empty()) with{CalibrationRevision=_revision,ProcessingDelayFrames=_plan.LatencyFrames};
    public int RecoveredDiscontinuities=>Latest.RecoveredDiscontinuities;
    public long RecordingUnconfirmedDiscontinuities=>Interlocked.Read(ref _unconfirmedDiscontinuities);
    internal Exception? Failure=>Volatile.Read(ref _failure);
    public ProcessingPipeline(Func<double> gain,Action<Exception> fault,Action<string> diagnostic)
    {_gain=gain;_fault=fault;_diagnostic=diagnostic;_worker=Task.Run(Run);}
    public void Configure(CaptureInfo info,CalibrationPlan plan)
    {_budget=Math.Min(64L*1024*1024,info.Format.SampleRate*(long)info.Format.BlockAlign*2);_configured.TrySetResult((info,plan));}
    public bool Submit(CapturePacket packet)
    {
        if(Failure!=null)return false;
        if(Interlocked.Add(ref _queuedBytes,packet.Length)>_budget){Interlocked.Add(ref _queuedBytes,-packet.Length);return false;}
        if(_queue.Writer.TryWrite(new(packet,null,null)))return true;
        Interlocked.Add(ref _queuedBytes,-packet.Length);return false;
    }
    private void Rebuild(CalibrationPlan plan,bool gap=false,bool preservePeak=false)
    {
        var old=_analyzer?.Latest;_analyzer?.Dispose();_plan=plan;_base=_frames;_delivered=_frames;
        _processor=new(plan,_info!.Format.Channels);_revision++;
        Volatile.Write(ref _analyzer,new AudioAnalyzer(_info.Format,_info.Epoch,segment:(old?.Segment??-1)+1,
            recoveredDiscontinuities:(old?.RecoveredDiscontinuities??0)+(gap?1:0),initialPeak:gap||preservePeak?old?.HeldPeakDbfs:null));
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
                    if(_recording is RecordingState.Writing or RecordingState.PauseDraining or RecordingState.StopDraining)
                    {
                        if(packet.PositionDelta!=0||(packet.NativeFlags&1)==0)throw new CaptureGapException(detail,packet.PositionDelta,packet.NativeFlags);
                        Interlocked.Increment(ref _unconfirmedDiscontinuities);
                    }
                    else Rebuild(_plan,true);
                }
                int count=checked(packet.Frames*_info.Format.Channels);
                var data=ArrayPool<float>.Shared.Rent(count);var output=ArrayPool<float>.Shared.Rent(count);
                try
                {
                    PcmDecoder.Decode(packet.Data.AsSpan(0,packet.Length),data.AsSpan(0,count),_info.Format,packet.Silent);
                    var result=_processor!.Process(data.AsSpan(0,count),output.AsSpan(0,count),_gain());
                    _frames+=packet.Frames;Deliver(output.AsSpan(0,result.Frames*_info.Format.Channels),result);
                }
                finally{ArrayPool<float>.Shared.Return(data);ArrayPool<float>.Shared.Return(output);}
            }
        }
        catch(OperationCanceledException){}
        catch(Exception error){Volatile.Write(ref _failure,error);_queue.Writer.TryComplete(error);_fault(error);}
        finally
        {
            if(_failure!=null)_ready.TrySetException(_failure);else _ready.TrySetCanceled();
            var error=_failure??new OperationCanceledException();
            _boundary?.TrySetResult(error);
            while(_queue.Reader.TryRead(out var pending)){pending.Packet?.Dispose();pending.Completion?.TrySetException(error);}
        }
    }
    private void Deliver(ReadOnlySpan<float> samples,CalibrationOutput result)
    {
        int channels=_info!.Format.Channels;long first=_base+result.SourceStartFrame,last=first+result.Frames;
        if(_writer?.Failure is Exception disk)throw new IOException("录制文件写入失败："+AudioFailure.Describe(disk),disk);
        if(result.Frames>0)
        {
            if(_writer!=null&&_recording is RecordingState.Writing or RecordingState.PauseDraining or RecordingState.StopDraining)
            {
                long a=Math.Max(first,_recordingStart),b=_recording==RecordingState.Writing?last:Math.Min(last,_cutoff);
                if(b>a&&!_writer.TryWrite(samples.Slice(checked((int)(a-first)*channels),checked((int)(b-a)*channels))))
                    throw new IOException(_writer.Failure is Exception failure
                        ? "录制文件写入失败："+AudioFailure.Describe(failure)
                        : "录制队列溢出，未静默丢样本。",_writer.Failure);
            }
            int skipHeldPeak=(int)Math.Clamp(_heldPeakStart-first,0,result.Frames);
            _analyzer!.Process(samples,result.SourcePeak,last,skipHeldPeak);_delivered=last;
        }
        FinishBoundary();
    }
    private void FinishBoundary()
    {
        if(_delivered<_cutoff)return;
        if(_recording==RecordingState.PauseDraining)
        {
            _recording=RecordingState.Paused;
            _boundary!.TrySetResult(null);
            _boundary=null;
        }
        else if(_recording==RecordingState.StopDraining)DetachWriter();
    }
    private void DetachWriter(Exception? error=null)
    {
        _writer=null;
        _recording=RecordingState.Detached;
        if(error==null&&_resetPeakOnDetach)_analyzer!.ResetPeak();
        _boundary?.TrySetResult(error);
        _boundary=null;
    }
    private void Drain()
    {
        if(_processor==null)return;
        if(_plan.LatencyFrames==0){Deliver(ReadOnlySpan<float>.Empty,new(0,0,0));return;}
        int count=_plan.LatencyFrames*_info!.Format.Channels;
        var output=new float[count];var result=_processor.Flush(output);
        Deliver(output.AsSpan(0,result.Frames*_info.Format.Channels),result);
    }
    public Task Command(Action action)
    {
        if(Failure is Exception failure)return Task.FromException(failure);
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);return Send();
        async Task Send()
        {
            try{await _queue.Writer.WriteAsync(new(null,action,done));}
            catch(ChannelClosedException) when(Failure is Exception failure){ExceptionDispatchInfo.Throw(failure);}
            await done.Task;
        }
    }
    public Task ApplyCalibrationAsync(CalibrationPlan plan)=>Command(()=>{if(_writer!=null)throw new InvalidOperationException("录制或暂停期间不能更换校准。");Rebuild(plan);});
    public Task AttachAsync(IRecordingWriter writer)=>Command(()=>
    {
        _writer=writer;
        _recording=RecordingState.Writing;
        _recordingStart=_heldPeakStart=_frames;
        Interlocked.Exchange(ref _unconfirmedDiscontinuities,0);
        _analyzer!.ResetPeak();
    });
    public async Task<Task<Exception?>> RequestPauseAsync()
    {
        Task<Exception?> done=Task.FromResult<Exception?>(null);
        await Command(()=>
        {
            if(_recording==RecordingState.Writing)
            {
                _recording=RecordingState.PauseDraining;
                _cutoff=_frames;
                _boundary=new(TaskCreationOptions.RunContinuationsAsynchronously);
                TraceBoundary("pause");
            }
            if(_boundary!=null)done=_boundary.Task;
            FinishBoundary();
        });
        return done;
    }
    public Task ResumeAsync()=>Command(()=>
    {
        if(_recording!=RecordingState.Paused)return;
        _recording=RecordingState.Writing;
        _recordingStart=_frames;
    });
    private void BeginStop(bool resetPeak)
    {
        if(_recording==RecordingState.StopDraining)return;
        if(_recording==RecordingState.Writing)_cutoff=_frames;
        _recording=RecordingState.StopDraining;
        _resetPeakOnDetach=resetPeak;
        _boundary??=new(TaskCreationOptions.RunContinuationsAsynchronously);
        TraceBoundary("stop");
    }
    private void TraceBoundary(string kind)
        =>_diagnostic($"Recording source boundary: {kind}; frame={_cutoff}; delivered={_delivered}; delay={_plan.LatencyFrames}");
    private void DrainAndDetach(bool rebuild)
    {
        try
        {
            Drain();
            if(_recording!=RecordingState.Detached)throw new IOException("校准尾部未完成，录音可能不完整。");
            // Zero-draining advances convolution beyond real capture. Restart the monitoring clock.
            if(rebuild)Rebuild(_plan,preservePeak:true);
        }
        catch(Exception error)
        {
            DetachWriter(error);
            throw;
        }
    }
    private async Task<Task<Exception?>> RequestDetachAsync(bool mathematicalDrain,bool resetPeak)
    {
        Task<Exception?> done=Task.FromResult<Exception?>(null);
        await Command(()=>
        {
            if(_recording==RecordingState.Detached)return;
            BeginStop(resetPeak);
            done=_boundary!.Task;
            if(mathematicalDrain)DrainAndDetach(rebuild:true);
            else FinishBoundary();
        });
        return done;
    }
    public async Task DetachAsync(bool mathematicalDrain,bool resetPeak)
    {
        try
        {
            Task<Exception?> boundary=await RequestDetachAsync(mathematicalDrain,resetPeak);
            Exception? error;
            try{error=await (mathematicalDrain?boundary:boundary.WaitAsync(BoundaryWait));}
            catch(TimeoutException)
            {
                var timeout=new IOException("等待校准尾部时采集未继续，本次录音可能不完整。");
                try{error=await await RequestDetachAsync(mathematicalDrain:true,resetPeak);}
                catch(Exception cleanupError) when(Failure==null){error=cleanupError;}
                throw error==null?timeout:AudioFailure.Combine(timeout,error);
            }
            if(error!=null)ExceptionDispatchInfo.Throw(error);
        }
        catch when(Failure is Exception failure)
        {
            await _worker;
            if(_writer!=null)
            {
                BeginStop(resetPeak:false);
                try{DrainAndDetach(rebuild:false);}
                catch(Exception drainError){throw AudioFailure.Combine(failure,drainError);}
            }
            ExceptionDispatchInfo.Throw(failure);
        }
    }
    public Task ResetPeakAsync()=>Command(()=>_analyzer!.ResetPeak());
    public async ValueTask DisposeAsync()
    {_configured.TrySetCanceled();_queue.Writer.TryComplete();await _worker.ConfigureAwait(false);_ = _ready.Task.Exception;_analyzer?.Dispose();}
}
