using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace PortableRecorder.Audio;

// FFmpeg runs as a separate, hidden encoder process. Capture never waits for it.
public sealed class CompressedAudioWriter:IRecordingWriter
{
    private sealed record Block(float[] Data,int Count);
    private readonly Channel<Block> _queue=Channel.CreateBounded<Block>(new BoundedChannelOptions(512){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly Process _process;
    private readonly FileStream _destination;
    private readonly Task _worker;
    private readonly Task<string> _diagnostics;
    private readonly string _staging;
    private readonly int _channels;
    private readonly long _budget;
    private readonly int _minimumFrames;
    private long _queuedBytes,_frames;
    private Exception? _failure;
    public string Path {get;}
    public long WrittenFrames=>Interlocked.Read(ref _frames);
    public Exception? Failure=>Volatile.Read(ref _failure);
    public IReadOnlyList<string> AuxiliaryPaths=>[_staging];

    public CompressedAudioWriter(string path,AudioFormat format,RecordingEncoding encoding,string executable)
    {
        encoding.Validate(format);
        if(encoding.Codec==RecordingCodec.Wav)throw new ArgumentException("Use FloatWaveWriter for WAV.");
        if(!File.Exists(executable))throw new FileNotFoundException("缺少随程序发布的音频编码器，请重新构建或恢复发布文件。",executable);
        Path=path;_channels=format.Channels;_budget=Math.Min(64L*1024*1024,format.SampleRate*(long)format.Channels*4*2);
        _minimumFrames=encoding.Codec==RecordingCodec.Mp3?(format.SampleRate>=32000?1152:576):1;
        // Keep the final name exclusively reserved. A seekable sibling permits FLAC total/MD5
        // and MP3 gapless Xing headers; pipe output would lose that final header update.
        _staging=path+"."+Guid.NewGuid().ToString("N")+".encoding";
        _destination=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardError=true};
        void Add(params string[] values){foreach(var value in values)start.ArgumentList.Add(value);}
        Add("-hide_banner","-loglevel","error","-nostdin","-n","-f","f32le","-ar",format.SampleRate.ToString(CultureInfo.InvariantCulture),"-ac",format.Channels.ToString(CultureInfo.InvariantCulture),"-i","pipe:0","-map","0:a:0","-threads","1","-ar",format.SampleRate.ToString(CultureInfo.InvariantCulture),"-ac",format.Channels.ToString(CultureInfo.InvariantCulture));
        if(encoding.Codec==RecordingCodec.Flac)Add("-c:a","flac","-sample_fmt","s32","-bits_per_raw_sample","24","-compression_level",encoding.Quality.ToString(CultureInfo.InvariantCulture),"-f","flac");
        else Add("-c:a","libmp3lame","-q:a",encoding.Quality.ToString(CultureInfo.InvariantCulture),"-compression_level","2","-write_xing","1","-f","mp3");
        Add(_staging);_process=new Process{StartInfo=start};
        try{if(!_process.Start())throw new IOException("无法启动音频编码器。");}
        catch(Exception startError)
        {
            Exception failure=startError;
            try{_process.Dispose();}catch(Exception error){failure=AudioFailure.Combine(failure,error);}
            try{_destination.Dispose();}catch(Exception error){failure=AudioFailure.Combine(failure,error);}
            try{File.Delete(path);}catch(Exception error){failure=AudioFailure.Combine(failure,error);}
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        _diagnostics=ReadDiagnostics();_worker=Task.Run(WriteLoop);
    }
    private async Task<string> ReadDiagnostics()
    {
        var buffer=new char[1024];string tail="";int count;
        while((count=await _process.StandardError.ReadAsync(buffer))>0){tail+=new string(buffer,0,count);if(tail.Length>4096)tail=tail[^4096..];}
        return tail.Trim();
    }
    public bool TryWrite(ReadOnlySpan<float> samples)
    {
        if(Failure!=null)return false;
        if(samples.Length%_channels!=0){Fail(new InvalidDataException("编码样本声道未对齐。"));return false;}
        foreach(float sample in samples)
            if(!float.IsFinite(sample)||sample is >1 or <-1)
            {Fail(new InvalidDataException("压缩录音遇到非有限或超过满刻度的样本，已停止并保留此前有效录音。请降低增益，或使用可保存浮点超限的 WAV；未自动限幅。"));return false;}
        long bytes=samples.Length*4L;
        if(Interlocked.Add(ref _queuedBytes,bytes)>_budget){Interlocked.Add(ref _queuedBytes,-bytes);Fail(new IOException("压缩录音队列已满，未静默丢样本。"));return false;}
        var buffer=ArrayPool<float>.Shared.Rent(samples.Length);samples.CopyTo(buffer);
        if(_queue.Writer.TryWrite(new(buffer,samples.Length)))return true;
        ArrayPool<float>.Shared.Return(buffer);Interlocked.Add(ref _queuedBytes,-bytes);return false;
    }
    private void Fail(Exception error)
    {
        Exception? previous;
        do{previous=Failure;}
        while(Interlocked.CompareExchange(ref _failure,AudioFailure.Combine(previous,error),previous)!=previous);
        _queue.Writer.TryComplete();
    }
    private async Task WriteLoop()
    {
        try
        {
            await foreach(var block in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    // A stalled encoder cannot hold shutdown indefinitely.
                    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var bytes=ArrayPool<byte>.Shared.Rent(block.Count*4);
                    try{MemoryMarshal.AsBytes(block.Data.AsSpan(0,block.Count)).CopyTo(bytes);await _process.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(0,block.Count*4),deadline.Token);}
                    finally{ArrayPool<byte>.Shared.Return(bytes);}
                    Interlocked.Add(ref _frames,block.Count/_channels);
                }
                finally{Interlocked.Add(ref _queuedBytes,-block.Count*4L);ArrayPool<float>.Shared.Return(block.Data);}
            }
            _process.StandardInput.Close();
            using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30)))await _process.WaitForExitAsync(deadline.Token);
            string diagnostics=await _diagnostics;
            if(_process.ExitCode!=0)throw new IOException("编码器未成功完成文件："+diagnostics);
            if(WrittenFrames==0)throw new IOException("未收到录音样本，未生成可用的压缩录音。");
            if(WrittenFrames<_minimumFrames)throw new IOException("录音短于一个 MP3 音频帧，无法可靠保留原时长；请选择 WAV／FLAC 保存极短录音。");
            await using(var encoded=new FileStream(_staging,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan))
                await encoded.CopyToAsync(_destination);
            await _destination.FlushAsync();_destination.Flush(true);
            File.Delete(_staging);
        }
        catch(Exception error){Fail(new IOException("压缩录音写入或收尾失败："+error.Message+" 未完成文件可能位于："+_staging,error));}
        finally
        {
            _queue.Writer.TryComplete();
            while(_queue.Reader.TryRead(out var block)){Interlocked.Add(ref _queuedBytes,-block.Count*4L);ArrayPool<float>.Shared.Return(block.Data);}
            try
            {
                if(!_process.HasExited)
                {
                    _process.Kill(true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch(Exception error){Fail(new IOException("编码器进程清理失败："+error.Message,error));}
            try{await _diagnostics.WaitAsync(TimeSpan.FromSeconds(5));}
            catch(Exception error){Fail(new IOException("编码器错误输出收尾失败："+error.Message,error));}
            try{_process.Dispose();}catch(Exception error){Fail(error);}
            try{await _destination.DisposeAsync();}catch(Exception error){Fail(error);}
        }
    }
    public async Task CompleteAsync(){_queue.Writer.TryComplete();await _worker;if(Failure is Exception error)throw new IOException(AudioFailure.Describe(error),error);}
    public async ValueTask DisposeAsync()=>await CompleteAsync();
}
