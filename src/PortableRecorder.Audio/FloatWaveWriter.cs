using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.Wave;
namespace PortableRecorder.Audio;

public sealed class FloatWaveWriter:IRecordingWriter
{
    private sealed record Block(float[] Data,int Count);
    private readonly Channel<Block> _queue=Channel.CreateBounded<Block>(new BoundedChannelOptions(512){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly Task _worker;
    private readonly WaveFileWriter _wave;
    private readonly FileStream _file;
    private readonly int _channels;
    private readonly long _budget;
    private long _queuedBytes,_frames;
    private Exception? _failure;
    public string Path {get;}
    public long WrittenFrames=>Interlocked.Read(ref _frames);
    public Exception? Failure=>Volatile.Read(ref _failure);
    public FloatWaveWriter(string path,AudioFormat capture,long? testRf64Threshold=null)
    {
        Path=path;_channels=capture.Channels;_budget=Math.Min(64L*1024*1024,capture.SampleRate*(long)capture.Channels*4*2);
        _file=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read,65536,FileOptions.SequentialScan);
        try{_wave=new WaveFileWriter(_file,WaveFormat.CreateIeeeFloatWaveFormat(capture.SampleRate,capture.Channels),
            new WaveFileWriterOptions{EnableRf64=true,Rf64PromotionThreshold=testRf64Threshold??uint.MaxValue});}
        catch{_file.Dispose();throw;}
        _worker=Task.Run(WriteLoop);
    }
    public bool TryWrite(ReadOnlySpan<float> samples)
    {
        if(Failure!=null)return false;
        long bytes=samples.Length*4L;
        if(Interlocked.Add(ref _queuedBytes,bytes)>_budget){Interlocked.Add(ref _queuedBytes,-bytes);return false;}
        var buffer=ArrayPool<float>.Shared.Rent(samples.Length);samples.CopyTo(buffer);
        if(_queue.Writer.TryWrite(new(buffer,samples.Length)))return true;
        ArrayPool<float>.Shared.Return(buffer);Interlocked.Add(ref _queuedBytes,-bytes);return false;
    }
    private async Task WriteLoop()
    {
        try
        {
            int flush=0;
            await foreach(var block in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    _wave.Write(MemoryMarshal.AsBytes(block.Data.AsSpan(0,block.Count)));
                    Interlocked.Add(ref _frames,block.Count/_channels);
                    if(++flush>=32){_wave.Flush();flush=0;}
                }
                finally{Interlocked.Add(ref _queuedBytes,-block.Count*4L);ArrayPool<float>.Shared.Return(block.Data);}
            }
        }
        catch(Exception error){Volatile.Write(ref _failure,error);_queue.Writer.TryComplete(error);}
        finally
        {
            while(_queue.Reader.TryRead(out var block)){Interlocked.Add(ref _queuedBytes,-block.Count*4L);ArrayPool<float>.Shared.Return(block.Data);}
            try{_wave.Dispose();}catch(Exception error){Interlocked.CompareExchange(ref _failure,error,null);}
            try{_file.Dispose();}catch(Exception error){Interlocked.CompareExchange(ref _failure,error,null);}
        }
    }
    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();await _worker.ConfigureAwait(false);
        if(Failure is Exception error)throw new IOException("WAV 写入或文件头收尾失败，文件可能不完整。",error);
    }
    public async ValueTask DisposeAsync()=>await CompleteAsync();
}
