using System.Runtime.InteropServices;
using NAudio.Wave;
namespace PortableRecorder.Audio;

public sealed class FloatWaveWriter:IRecordingWriter
{
    private readonly RecordingWriteQueue _queue;
    private readonly Task _completion;
    private readonly WaveFileWriter _wave;
    private readonly Stream _file;
    private readonly int _channels;
    private long _frames;
    public string Path {get;}
    public long WrittenFrames=>Interlocked.Read(ref _frames);
    public Exception? Failure=>_queue.Failure;
    public FloatWaveWriter(string path,AudioFormat capture,long? testRf64Threshold=null)
        :this(path,capture,testRf64Threshold,new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read,65536,FileOptions.SequentialScan)){}

    internal FloatWaveWriter(string path,AudioFormat capture,long? testRf64Threshold,Stream file)
    {
        Path=path;
        _channels=capture.Channels;
        _queue=new(capture,"录制队列溢出，未静默丢样本。");
        _file=file;
        try{_wave=new WaveFileWriter(_file,WaveFormat.CreateIeeeFloatWaveFormat(capture.SampleRate,capture.Channels),
            new WaveFileWriterOptions{EnableRf64=true,Rf64PromotionThreshold=testRf64Threshold??uint.MaxValue});}
        catch(Exception headerError)
        {
            try{_file.Dispose();}
            catch(Exception cleanupError){throw AudioFailure.Combine(headerError,cleanupError);}
            throw;
        }
        _completion=Task.Run(WriteLoop);
    }
    public bool TryWrite(ReadOnlySpan<float> samples)=>_queue.TryWrite(samples);

    private async Task WriteLoop()
    {
        int flush=0;
        try
        {
            await foreach(var block in _queue.ReadAllAsync())
            {
                try
                {
                    _wave.Write(MemoryMarshal.AsBytes(block.Data.AsSpan(0,block.Count)));
                    Interlocked.Add(ref _frames,block.Count/_channels);
                    if(++flush>=32){_wave.Flush();flush=0;}
                }
                catch(Exception error){_queue.Fault(error);break;}
                finally{_queue.Release(block);}
            }
        }
        catch(Exception error){_queue.Fault(error);}
        finally
        {
            _queue.DiscardPending();
            try{_wave.Dispose();}catch(Exception error){_queue.Fault(error);}
            try{_file.Dispose();}catch(Exception error){_queue.Fault(error);}
        }
        if(Failure is Exception failure)throw new IOException("WAV 写入或文件头收尾失败，文件可能不完整："+AudioFailure.Describe(failure),failure);
    }

    public Task CompleteAsync()
    {
        _queue.CloseInput();
        return _completion;
    }
    public ValueTask DisposeAsync()=>new(CompleteAsync());
}
