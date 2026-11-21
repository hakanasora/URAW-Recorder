using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PortableRecorder.Audio;

// FFmpeg runs as a separate, hidden encoder process. Capture never waits for it.
public sealed class CompressedAudioWriter:IRecordingWriter
{
    private enum EncoderPhase { Streaming, Finalizing, Failed, Completed }

    private readonly RecordingWriteQueue _queue;
    private readonly RecordingWriteQueue.PrepareBlock _prepareBlock;
    private readonly Process _process;
    private readonly FileStream _destination;
    private readonly Task _completion;
    private readonly Task<string> _diagnostics;
    private readonly string _staging;
    private readonly int _channels;
    private readonly int _minimumFrames;
    private EncoderPhase _phase=EncoderPhase.Streaming;
    private Exception? _failure;
    private bool _copiedToDestination;
    private long _frames;
    private long _clippedSamples;
    public long ClippedSamples=>Interlocked.Read(ref _clippedSamples);
    public string Path {get;}
    public long WrittenFrames=>Interlocked.Read(ref _frames);
    public Exception? Failure=>Volatile.Read(ref _failure)??_queue.Failure;
    public IReadOnlyList<string> AuxiliaryPaths=>[_staging];

    public CompressedAudioWriter(string path,AudioFormat format,RecordingEncoding encoding,string executable)
    {
        encoding.Validate(format);
        if(encoding.Codec==RecordingCodec.Wav)throw new ArgumentException("Use FloatWaveWriter for WAV.");
        if(!File.Exists(executable))throw new FileNotFoundException("缺少随程序发布的音频编码器，请重新构建或恢复发布文件。",executable);
        Path=path;_channels=format.Channels;
        _queue=new(format,"压缩录音队列已满，未静默丢样本。");
        _prepareBlock=PrepareBlock;
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
        _diagnostics=ReadDiagnostics();_completion=Task.Run(WriteLoop);
    }
    private async Task<string> ReadDiagnostics()
    {
        var buffer=new char[1024];string tail="";int count;
        while((count=await _process.StandardError.ReadAsync(buffer))>0){tail+=new string(buffer,0,count);if(tail.Length>4096)tail=tail[^4096..];}
        return tail.Trim();
    }
    public bool TryWrite(ReadOnlySpan<float> samples)=>_queue.TryWrite(samples,_prepareBlock);

    private Exception? PrepareBlock(Span<float> samples)
    {
        if(samples.Length%_channels!=0)return new InvalidDataException("编码样本声道未对齐。");
        long clipped=0;
        for(int i=0;i<samples.Length;i++)
        {
            float sample=samples[i];
            if(!float.IsFinite(sample))
                return new InvalidDataException("压缩录音遇到非有限音频数据，已停止并保留此前有效录音。");
            if(sample is >1 or <-1){samples[i]=Math.Clamp(sample,-1f,1f);clipped++;}
        }
        Interlocked.Add(ref _clippedSamples,clipped);
        return null;
    }

    private void RecordEncoderFailure(Exception error)
    {
        _phase=EncoderPhase.Failed;
        Volatile.Write(ref _failure,AudioFailure.Combine(Failure,error));
        _queue.CloseInput();
    }

    private async Task WriteLoop()
    {
        try
        {
            await foreach(var block in _queue.ReadAllAsync())
            {
                try
                {
                    // A stalled encoder cannot hold shutdown indefinitely.
                    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    int length=block.Count*sizeof(float);
                    var bytes=ArrayPool<byte>.Shared.Rent(length);
                    try
                    {
                        MemoryMarshal.AsBytes(block.Data.AsSpan(0,block.Count)).CopyTo(bytes);
                        await _process.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(0,length),deadline.Token);
                    }
                    finally{ArrayPool<byte>.Shared.Return(bytes);}
                    Interlocked.Add(ref _frames,block.Count/_channels);
                }
                catch(Exception error)
                {
                    RecordEncoderFailure(EncodingFailure(error));
                    break;
                }
                finally{_queue.Release(block);}
            }
            if(_phase==EncoderPhase.Streaming)await FinalizeFileAsync();
        }
        catch(Exception error){RecordEncoderFailure(EncodingFailure(error));}
        finally
        {
            _queue.DiscardPending();
            try
            {
                if(!_process.HasExited)
                {
                    _process.Kill(true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch(Exception error){RecordEncoderFailure(new IOException("编码器进程清理失败："+error.Message,error));}
            try{await _diagnostics.WaitAsync(TimeSpan.FromSeconds(5));}
            catch(Exception error){RecordEncoderFailure(new IOException("编码器错误输出收尾失败："+error.Message,error));}
            try{_process.Dispose();}catch(Exception error){RecordEncoderFailure(error);}
            try{await _destination.DisposeAsync();}catch(Exception error){RecordEncoderFailure(error);}
            if(!_copiedToDestination)
            {
                DeleteIfEmpty(Path);
                if(WrittenFrames==0)DeleteFile(_staging);
                else DeleteIfEmpty(_staging);
            }
        }
        if(Failure is Exception failure)throw new IOException(AudioFailure.Describe(failure),failure);
    }

    private static void DeleteIfEmpty(string file)
    {
        try
        {
            if(File.Exists(file)&&new FileInfo(file).Length==0)File.Delete(file);
        }
        catch{}
    }

    private static void DeleteFile(string file)
    {
        try{if(File.Exists(file))File.Delete(file);}
        catch{}
    }

    private IOException EncodingFailure(Exception error)
        =>new("压缩录音写入或收尾失败："+error.Message+(WrittenFrames>0&&File.Exists(_staging)?" 未完成文件可能位于："+_staging:""),error);

    private async Task FinalizeFileAsync()
    {
        _phase=EncoderPhase.Finalizing;
        _process.StandardInput.Close();
        using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30)))await _process.WaitForExitAsync(deadline.Token);
        string diagnostics=await _diagnostics;
        if(_process.ExitCode!=0)throw new IOException("编码器未成功完成文件："+diagnostics);
        if(WrittenFrames==0)throw new IOException("未收到录音样本，未生成可用的压缩录音。");
        if(WrittenFrames<_minimumFrames)throw new IOException("录音短于一个 MP3 音频帧，无法可靠保留原时长；请选择 WAV／FLAC 保存极短录音。");
        await using(var encoded=new FileStream(_staging,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan))
            await encoded.CopyToAsync(_destination);
        await _destination.FlushAsync();_destination.Flush(true);
        _copiedToDestination=true;
        _phase=EncoderPhase.Completed;
        File.Delete(_staging);
    }

    public Task CompleteAsync()
    {
        _queue.CloseInput();
        return _completion;
    }
    public ValueTask DisposeAsync()=>new(CompleteAsync());
}
