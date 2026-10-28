using System.Buffers;
using System.Threading.Channels;
namespace PortableRecorder.Audio;

internal sealed class ProcessingPipeline:IAsyncDisposable
{
    private sealed record Work(CapturePacket? Packet,Action? Command,TaskCompletionSource? Completion);
    private sealed class Interval(long start){public long Start=start;public long End=long.MaxValue;}
    private sealed record Boundary(long End,bool Detach,TaskCompletionSource Done);
    private readonly Channel<Work> _queue=Channel.CreateBounded<Work>(new BoundedChannelOptions(256){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly TaskCompletionSource<(CaptureInfo Info,CalibrationPlan Plan)> _configured=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready=>_ready.Task;
    private readonly Task _worker;
    private readonly Func<double> _gain;
    private readonly Action<Exception> _fault;
    private readonly Action<string> _diagnostic;
    private long _queuedBytes,_budget=64*1024*1024,_frames,_base,_delivered,_revision;
    private AudioAnalyzer? _analyzer;
    private CalibrationProcessor? _processor;
    private CalibrationPlan _plan=CalibrationPlan.Identity;
    private CaptureInfo? _info;
    private IRecordingWriter? _writer;
    private readonly List<Interval> _intervals=[];
    private readonly List<Boundary> _boundaries=[];
    private bool _paused;
    private Exception? _failure;
    internal CalibrationPlan Plan=>_plan;
    public CalibrationStatus Calibration=>_plan.Status(_info?.Format??new(0,0,32,32,PcmEncoding.Float),_revision);
    public AudioSnapshot Latest=>(Volatile.Read(ref _analyzer)?.Latest??AudioSnapshot.Empty()) with{CalibrationRevision=_revision,ProcessingDelayFrames=_plan.LatencyFrames};
    public int RecoveredDiscontinuities=>Latest.RecoveredDiscontinuities;
    public ProcessingPipeline(Func<double> gain,Action<Exception> fault,Action<string> diagnostic)
    {_gain=gain;_fault=fault;_diagnostic=diagnostic;_worker=Task.Run(Run);}
    public void Configure(CaptureInfo info,CalibrationPlan plan)
    {_budget=Math.Min(64L*1024*1024,info.Format.SampleRate*(long)info.Format.BlockAlign*2);_configured.TrySetResult((info,plan));}
    public bool Submit(CapturePacket packet)
    {
        if(Volatile.Read(ref _failure)!=null)return false;
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
                using var packet=work.Packet!;Interlocked.Add(ref _queuedBytes,-packet.Length);
                if(packet.Discontinuity)
                {
                    string detail=$" flags=0x{packet.NativeFlags:X}; position={packet.Position}; frames={packet.Frames}; epoch={_info.Epoch}";
                    _diagnostic("Capture discontinuity:"+detail);
                    if(_writer!=null&&(!_paused||_boundaries.Count>0))throw new CaptureGapException(detail);
                    Rebuild(_plan,true);
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
            foreach(var b in _boundaries)b.Done.TrySetException(_failure??new OperationCanceledException());
            while(_queue.Reader.TryRead(out var pending)){pending.Packet?.Dispose();pending.Completion?.TrySetException(_failure??new OperationCanceledException());}
        }
    }
    private void Deliver(ReadOnlySpan<float> samples,CalibrationOutput result)
    {
        int channels=_info!.Format.Channels;long first=_base+result.SourceStartFrame,last=first+result.Frames;
        if(_writer?.Failure is Exception disk)throw new IOException("录制文件写入失败："+AudioFailure.Describe(disk),disk);
        if(result.Frames>0)
        {
            if(_writer!=null)foreach(var interval in _intervals)
            {
                long a=Math.Max(first,interval.Start),b=Math.Min(last,interval.End);
                if(b>a&&!_writer.TryWrite(samples.Slice(checked((int)(a-first)*channels),checked((int)(b-a)*channels))))
                    throw new IOException(_writer.Failure is Exception failure
                        ? "录制文件写入失败："+AudioFailure.Describe(failure)
                        : "录制队列溢出，未静默丢样本。",_writer.Failure);
            }
            _analyzer!.Process(samples,result.SourcePeak,last);_delivered=last;
        }
        for(int i=_boundaries.Count-1;i>=0;i--)
        {
            var b=_boundaries[i];if(_delivered<b.End)continue;
            if(b.Detach){_writer=null;_intervals.Clear();}b.Done.TrySetResult();_boundaries.RemoveAt(i);
        }
        _intervals.RemoveAll(i=>i.End<=_delivered);
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
        if(_failure!=null)return Task.FromException(_failure);
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);return Send();
        async Task Send(){await _queue.Writer.WriteAsync(new(null,action,done));await done.Task;}
    }
    public Task ApplyCalibrationAsync(CalibrationPlan plan)=>Command(()=>{if(_writer!=null)throw new InvalidOperationException("录制或暂停期间不能更换校准。");Rebuild(plan);});
    public Task AttachAsync(IRecordingWriter writer)=>Command(()=>{_writer=writer;_paused=false;_intervals.Clear();_intervals.Add(new(_frames));});
    private async Task BoundaryAsync(bool detach,bool drain)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if(_failure!=null)
        {
            // Worker has finished: drain only already accepted samples, never manufacture capture frames.
            await _worker;Drain();_writer=null;return;
        }
        await Command(()=>
        {
            // Repeated pause commands must not add boundaries or shorten an existing interval.
            if(!detach&&_paused){done.TrySetResult();return;}
            long end=_paused?(_intervals.LastOrDefault()?.End??_delivered):_frames;
            if(!_paused&&_intervals.Count>0)_intervals[^1].End=end;
            _paused=true;_boundaries.Add(new(end,detach,done));
            _diagnostic($"Recording source boundary: {(detach?"stop":"pause")}; frame={end}; delivered={_delivered}; delay={_plan.LatencyFrames}");
            if(drain)
            {
                Drain();
                // Zero-draining consumes the processor's future. Shared monitoring must start
                // fresh rather than reusing that artificially advanced convolution clock.
                if(detach)Rebuild(_plan,preservePeak:true);
            }
            else Deliver(ReadOnlySpan<float>.Empty,new(0,0,0));
        });
        try{await done.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        catch(TimeoutException){throw new IOException("等待校准尾部时采集未继续，本次录音可能不完整。");}
    }
    public Task PauseAsync(bool paused)=>paused?BoundaryAsync(false,false):Command(()=>
    {
        if(!_paused||_writer==null)return;
        _paused=false;
        _intervals.Add(new(_frames));
    });
    public Task DetachAsync(bool mathematicalDrain=false)=>BoundaryAsync(true,mathematicalDrain);
    public Task ResetPeakAsync()=>Command(()=>_analyzer!.ResetPeak());
    public async ValueTask DisposeAsync()
    {_configured.TrySetCanceled();_queue.Writer.TryComplete();await _worker.ConfigureAwait(false);_ = _ready.Task.Exception;_analyzer?.Dispose();}
}
