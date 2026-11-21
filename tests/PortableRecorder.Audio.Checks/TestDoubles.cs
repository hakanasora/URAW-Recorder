using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PortableRecorder.Audio;

sealed class FakeFactory:ICaptureFactory
{
    public AudioDevice Device {get;}=new("fake","Deterministic input",true);
    public AudioFormat Format=new(48000,1,32,32,PcmEncoding.Float);
    public bool DenySharedRaw,DenyExclusive,FailDuringOpen;
    public Action<AudioDevice>? BeforeOpen;
    public FakeCapture? Current;
    public IReadOnlyList<AudioDevice> Enumerate()=>[Device];
    public AudioFormat? LastRequestedFormat;
    public bool RejectPreferred;
    public Task<InputFormatOptions> QueryFormatsAsync(AudioDevice device)=>Task.FromResult(new InputFormatOptions(Format,
        new[]{Format,new AudioFormat(96000,Format.Channels,24,24,PcmEncoding.SignedInteger)}));
    public Task<IAudioCaptureService> OpenAsync(AudioDevice device,bool exclusive,bool raw,long epoch,Func<CapturePacket,bool> packet,Action<Exception> fault,AudioFormat? preferredFormat=null)
    {
        BeforeOpen?.Invoke(device);
        if(exclusive && DenyExclusive)throw new IOException("Exclusive denied");
        if(!exclusive && raw && DenySharedRaw)throw new RawUnavailableException("RAW unsupported");
        LastRequestedFormat=preferredFormat;
        if(preferredFormat!=null&&RejectPreferred)throw new NotSupportedException("Requested format unavailable");
        Current=new(new(device,preferredFormat??Format,exclusive,raw,epoch),packet,fault);
        if(FailDuringOpen)fault(new IOException("Capture stopped during preparation"));
        return Task.FromResult<IAudioCaptureService>(Current);
    }
    public void Push(float[] samples,bool gap=false)=>Current!.Push(samples,gap);
}
sealed class FakeCapture(CaptureInfo info,Func<CapturePacket,bool> packet,Action<Exception> fault):IAudioCaptureService,IPauseTailCapture
{
    public CaptureInfo Info=>info;long _frame;bool _closed;
    private bool _pauseTailPending;
    public Action? BeforeDispose;
    public bool Closed=>_closed;
    public bool PauseTailPending=>Volatile.Read(ref _pauseTailPending);
    public void SetPauseTailPending(bool pending)=>Volatile.Write(ref _pauseTailPending,pending);
    public void TriggerFault(Exception? error=null)=>fault(error??new IOException("Old session fault"));
    public void Push(float[] data,bool gap)
    {
        if(_closed)throw new InvalidOperationException("Closed capture");
        int width=info.Format.Bits/8;var bytes=ArrayPool<byte>.Shared.Rent(data.Length*width);
        if(info.Format.Encoding==PcmEncoding.Float)MemoryMarshal.AsBytes(data.AsSpan()).CopyTo(bytes);
        else for(int i=0;i<data.Length;i++)
        {
            long scale=1L<<(info.Format.ValidBits-1);
            int code=(int)(Math.Clamp((long)Math.Round(data[i]*scale),-scale,scale-1)<<(info.Format.Bits-info.Format.ValidBits));
            var target=bytes.AsSpan(i*width,width);
            if(width==2)BinaryPrimitives.WriteInt16LittleEndian(target,(short)code);
            else if(width==3){target[0]=(byte)code;target[1]=(byte)(code>>8);target[2]=(byte)(code>>16);}
            else BinaryPrimitives.WriteInt32LittleEndian(target,code);
        }
        var block=new CapturePacket(bytes,data.Length*width,data.Length/info.Format.Channels,_frame,false,gap);_frame+=block.Frames;
        if(!packet(block)){block.Dispose();fault(new IOException("queue overflow"));}
    }
    public ValueTask DisposeAsync(){BeforeDispose?.Invoke();_closed=true;return ValueTask.CompletedTask;}
}

sealed class FailingWriter:IRecordingWriter
{
    public string Path=>"injected-disk-failure.wav";
    public long WrittenFrames {get;private set;}
    public Exception? Failure {get;private set;}
    public bool Reject;
    public bool TryWrite(ReadOnlySpan<float> samples){if(Failure!=null || Reject)return false;WrittenFrames+=samples.Length;return true;}
    public void Fail()=>Failure=new IOException("Injected disk failure");
    public Task CompleteAsync()=>Failure==null?Task.CompletedTask:Task.FromException(Failure);
    public ValueTask DisposeAsync()=>new(CompleteAsync());
}

sealed class FakeLiveMonitorOutput:ILiveMonitorOutput
{
    private readonly LiveMonitorBuffer _buffer;
    private int _writeCount;
    private int _lastSourceRate;
    private int _lastSourceChannels;
    private double _lastLinearGain;
    private int _disposed;

    public MonitorOutputFormat OutputFormat {get;}
    public int WriteCount=>Volatile.Read(ref _writeCount);
    public int LastSourceRate=>Volatile.Read(ref _lastSourceRate);
    public int LastSourceChannels=>Volatile.Read(ref _lastSourceChannels);
    public double LastLinearGain=>Volatile.Read(ref _lastLinearGain);
    public long QueuedFrames=>_buffer.QueuedFrames;
    public bool Disposed=>Volatile.Read(ref _disposed)!=0;
    public Exception? ThrowOnWrite;
    public event Action<Exception>? Faulted;

    public FakeLiveMonitorOutput(MonitorOutputFormat format)
    {
        OutputFormat=format;
        _buffer=new LiveMonitorBuffer(format);
    }

    public void Write(ReadOnlySpan<float> interleavedSamples,int sourceSampleRate,int sourceChannels,double linearGain)
    {
        if(ThrowOnWrite is Exception error)throw error;
        Volatile.Write(ref _lastSourceRate,sourceSampleRate);
        Volatile.Write(ref _lastSourceChannels,sourceChannels);
        Volatile.Write(ref _lastLinearGain,linearGain);
        _buffer.Write(interleavedSamples,sourceSampleRate,sourceChannels,linearGain);
        Interlocked.Increment(ref _writeCount);
    }

    public int ReadRendered(Span<float> destination)=>_buffer.Read(destination);
    public void TriggerFault(Exception error)=>Faulted?.Invoke(error);

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed,1);
        return ValueTask.CompletedTask;
    }
}

sealed class FakeLiveMonitorFactory:ILiveMonitorFactory
{
    private readonly object _gate=new();
    private readonly List<Action> _watchers=[];
    private int _openCount;
    private int _disposedWatchers;

    public MonitorOutputFormat OutputFormat=new(48000,2);
    public Exception? FailOpen;
    public FakeLiveMonitorOutput? Current {get;private set;}
    public List<FakeLiveMonitorOutput> Opened {get;}=[];
    public int OpenCount=>Volatile.Read(ref _openCount);
    public int ActiveWatchers {get{lock(_gate)return _watchers.Count;}}
    public int DisposedWatchers=>Volatile.Read(ref _disposedWatchers);

    public Task<ILiveMonitorOutput> OpenDefaultAsync(CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _openCount);
        if(FailOpen is Exception error)return Task.FromException<ILiveMonitorOutput>(error);
        var output=new FakeLiveMonitorOutput(OutputFormat);
        lock(_gate){Current=output;Opened.Add(output);}
        return Task.FromResult<ILiveMonitorOutput>(output);
    }

    public IDisposable WatchDefaultDeviceChanged(Action changed)
    {
        lock(_gate)_watchers.Add(changed);
        return new WatcherRegistration(this,changed);
    }

    public void TriggerDefaultDeviceChanged()
    {
        Action[] snapshot;
        lock(_gate)snapshot=_watchers.ToArray();
        foreach(var callback in snapshot)callback();
    }

    private sealed class WatcherRegistration(FakeLiveMonitorFactory owner,Action callback):IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if(Interlocked.Exchange(ref _disposed,1)!=0)return;
            lock(owner._gate)owner._watchers.Remove(callback);
            Interlocked.Increment(ref owner._disposedWatchers);
        }
    }
}
