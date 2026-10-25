using System.Buffers;
using System.Threading.Channels;
namespace PortableRecorder.Audio;

internal sealed class ProcessingPipeline:IAsyncDisposable
{
    private sealed record Work(CapturePacket? Packet,Action? Command,TaskCompletionSource? Completion);
    private readonly Channel<Work> _queue=Channel.CreateBounded<Work>(new BoundedChannelOptions(256){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly TaskCompletionSource<CaptureInfo> _configured=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private readonly Func<double> _gain;
    private readonly Action<Exception> _fault;
    private readonly ICalibrationStage _calibration;
    private long _queuedBytes,_budget=64*1024*1024,_frames;
    private AudioAnalyzer? _analyzer;
    private IRecordingWriter? _writer;
    private bool _paused;
    private Exception? _failure;
    public AudioSnapshot Latest=>_analyzer?.Latest??AudioSnapshot.Empty();
    public ProcessingPipeline(Func<double> gain,ICalibrationStage calibration,Action<Exception> fault)
    {_gain=gain;_calibration=calibration;_fault=fault;_worker=Task.Run(Run);}
    public void Configure(CaptureInfo info)
    {
        _budget=Math.Min(64L*1024*1024,info.Format.SampleRate*(long)info.Format.BlockAlign*2);
        _configured.TrySetResult(info);
    }
    public bool Submit(CapturePacket packet)
    {
        if(Volatile.Read(ref _failure)!=null)return false;
        if(Interlocked.Add(ref _queuedBytes,packet.Length)>_budget){Interlocked.Add(ref _queuedBytes,-packet.Length);return false;}
        if(_queue.Writer.TryWrite(new(packet,null,null)))return true;
        Interlocked.Add(ref _queuedBytes,-packet.Length);return false;
    }
    private async Task Run()
    {
        try
        {
            var info=await _configured.Task;_analyzer=new AudioAnalyzer(info.Format,info.Epoch);
            await foreach(var work in _queue.Reader.ReadAllAsync())
            {
                if(work.Command!=null)
                {
                    try{work.Command();work.Completion!.TrySetResult();}
                    catch(Exception error){work.Completion!.TrySetException(error);}
                    continue;
                }
                using var packet=work.Packet!;Interlocked.Add(ref _queuedBytes,-packet.Length);
                if(packet.Discontinuity)throw new IOException("检测到音频包断流，未插入虚构静音来掩盖缺口。");
                var data=ArrayPool<float>.Shared.Rent(packet.Frames*info.Format.Channels);
                try
                {
                    var samples=data.AsSpan(0,packet.Frames*info.Format.Channels);
                    PcmDecoder.Decode(packet.Data.AsSpan(0,packet.Length),samples,info.Format,packet.Silent);
                    double sourcePeak=0;foreach(var x in samples)sourcePeak=Math.Max(sourcePeak,Math.Abs(x));
                    _calibration.Process(samples,info.Format);
                    float multiplier=(float)_gain();
                    if(multiplier!=1)for(int i=0;i<samples.Length;i++)samples[i]*=multiplier;
                    if(_writer?.Failure is Exception diskFailure)throw new IOException("录制文件写入失败。",diskFailure);
                    if(_writer!=null && !_paused && !_writer.TryWrite(samples))
                        throw new IOException(_writer.Failure==null?"录制队列溢出，录音已停止，未静默丢样本。":"录制文件写入失败。",_writer.Failure);
                    _frames+=packet.Frames;_analyzer.Process(samples,sourcePeak,_frames);
                }
                finally{ArrayPool<float>.Shared.Return(data);}
            }
        }
        catch(OperationCanceledException){}
        catch(Exception error){Volatile.Write(ref _failure,error);_queue.Writer.TryComplete(error);_fault(error);}
        finally
        {
            while(_queue.Reader.TryRead(out var pending))
            {
                pending.Packet?.Dispose();pending.Completion?.TrySetException(_failure??new OperationCanceledException());
            }
        }
    }
    public Task Command(Action action)
    {
        if(_failure!=null)return Task.FromException(_failure);
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return Send();
        async Task Send(){await _queue.Writer.WriteAsync(new(null,action,done));await done.Task;}
    }
    public Task AttachAsync(IRecordingWriter writer)=>Command(()=>{_writer=writer;_paused=false;});
    public Task PauseAsync(bool paused)=>Command(()=>_paused=paused);
    public Task DetachAsync()=>Command(()=>_writer=null);
    public Task ResetPeakAsync()=>Command(()=>_analyzer!.ResetPeak());
    public async ValueTask DisposeAsync()
    {
        _configured.TrySetCanceled();_queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);_analyzer?.Dispose();
    }
}
