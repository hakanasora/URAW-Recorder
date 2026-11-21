using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace PortableRecorder.Audio;

/// <summary>Read-only native loopback monitoring. Never opens a writer or microphone calibration stage.</summary>
public sealed class ListeningService:IAsyncDisposable
{
    private readonly IListeningCaptureFactory _factory;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _commands=new(1,1);
    private ServicePhase _phase=new ServicePhase.Stopped(null);
    private double _gainDb;
    private long _epoch;
    public ListeningService(IListeningCaptureFactory? factory=null):this(factory??new WasapiListeningCaptureFactory(),TimeProvider.System){}
    internal ListeningService(IListeningCaptureFactory factory,TimeProvider clock)
    {_factory=factory;_clock=clock;}
    private Session? CurrentSession=>Volatile.Read(ref _phase) switch
    {
        ServicePhase.Opening opening=>opening.Session,
        ServicePhase.Active active=>active.Session,
        _=>null
    };
    public ListeningStatus Status=>Volatile.Read(ref _phase) switch
    {
        ServicePhase.Stopped stopped=>new(ListeningState.Stopped,stopped.LastSource,null,""),
        ServicePhase.Opening opening=>opening.Session.Status,
        ServicePhase.Active active=>active.Session.Status,
        ServicePhase.Stopping stopping=>stopping.FrozenStatus,
        ServicePhase.Faulted faulted=>faulted.Status,
        ServicePhase.Disposed disposed=>new(ListeningState.Stopped,disposed.LastSource,null,""),
        _=>new(ListeningState.Stopped,null,null,"")
    };
    public AudioSnapshot Latest=>CurrentSession?.Latest??AudioSnapshot.Empty(Volatile.Read(ref _epoch));
    public double GainDb=>Volatile.Read(ref _gainDb);
    public void SetGain(double db)
    {
        if(!double.IsFinite(db))throw new ArgumentOutOfRangeException(nameof(db));
        Volatile.Write(ref _gainDb,Math.Round(Math.Clamp(db,-60,60),1));
    }
    public void ResetPeak()=>CurrentSession?.ResetPeak();

    public async Task StartAsync(ListeningSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if(!Enum.IsDefined(source.Scope))throw new ArgumentOutOfRangeException(nameof(source));
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _phase) is ServicePhase.Disposed,this);
            await StopCoreAsync().ConfigureAwait(false);
            long epoch=Interlocked.Increment(ref _epoch);
            var session=new Session(source,epoch,()=>GainDb,_clock);
            Volatile.Write(ref _phase,new ServicePhase.Opening(session));
            try
            {
                var capture=await _factory.OpenAsync(source,session.Submit,session.Fault).ConfigureAwait(false);
                session.Begin(capture);
                Volatile.Write(ref _phase,new ServicePhase.Active(session));
            }
            catch(Exception error)
            {
                session.Fault(error);
                var failure=AudioFailure.Combine(session.Failure,error);
                try{await session.DisposeAsync().ConfigureAwait(false);}
                catch(Exception cleanupError){failure=AudioFailure.Combine(failure,cleanupError);}
                finally
                {
                    Volatile.Write(ref _phase,new ServicePhase.Faulted(session.Status with{Message=Describe(failure)}));
                }
                ExceptionDispatchInfo.Throw(failure);
            }
        }
        finally{_commands.Release();}
    }

    public async Task StopAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try{await StopCoreAsync().ConfigureAwait(false);}
        finally{_commands.Release();}
    }
    private async Task StopCoreAsync()
    {
        var current=Volatile.Read(ref _phase);
        var session=current switch
        {
            ServicePhase.Opening opening=>opening.Session,
            ServicePhase.Active active=>active.Session,
            _=>null
        };
        var previous=Status;
        var lastSource=session?.Source??previous.Source;
        if(session==null)
        {
            if(current is ServicePhase.Faulted)Volatile.Write(ref _phase,new ServicePhase.Stopped(lastSource));
            return;
        }
        Volatile.Write(ref _phase,new ServicePhase.Stopping(previous,lastSource));
        try{await session.DisposeAsync().ConfigureAwait(false);}
        finally{Volatile.Write(ref _phase,new ServicePhase.Stopped(lastSource));}
    }
    public async ValueTask DisposeAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if(Volatile.Read(ref _phase) is ServicePhase.Disposed)return;
            try{await StopCoreAsync().ConfigureAwait(false);}
            finally
            {
                var lastSource=Volatile.Read(ref _phase) switch
                {
                    ServicePhase.Stopped stopped=>stopped.LastSource,
                    ServicePhase.Stopping stopping=>stopping.LastSource,
                    _=>Status.Source
                };
                Volatile.Write(ref _phase,new ServicePhase.Disposed(lastSource));
            }
        }
        finally{_commands.Release();}
        // Commands arriving after disposal still acquire this gate and observe ServicePhase.Disposed.
    }
    private abstract record ServicePhase
    {
        private ServicePhase(){}
        public sealed record Stopped(ListeningSource? LastSource):ServicePhase;
        public sealed record Opening(Session Session):ServicePhase;
        public sealed record Active(Session Session):ServicePhase;
        public sealed record Stopping(ListeningStatus FrozenStatus,ListeningSource? LastSource):ServicePhase;
        public sealed record Faulted(ListeningStatus Status):ServicePhase;
        public sealed record Disposed(ListeningSource? LastSource):ServicePhase;
    }
    private static string Describe(Exception e)=>e is AggregateException errors
        ?string.Join("；",errors.InnerExceptions.Select(Describe).Distinct(StringComparer.Ordinal))
        :e.HResult switch
    {
        unchecked((int)0x88890004)=>"输出设备已移除或重新配置，请重新选择。",
        unchecked((int)0x80070005)=>"Windows 拒绝访问所选音频源。",
        unchecked((int)0x88890010)=>"Windows 音频服务未运行。",
        _=>e.Message
    };

    private sealed class Session:IAsyncDisposable
    {
        private static readonly TimeSpan PacketTimeout=TimeSpan.FromMilliseconds(500);
        private sealed record Observation(ListeningStatus Status,long LastPacket=0,Exception? Failure=null);
        private readonly record struct QueuedPacket(CapturePacket Packet,long Arrival,bool ResetBefore);
        private readonly Channel<QueuedPacket> _packets=Channel.CreateBounded<QueuedPacket>(new BoundedChannelOptions(64)
            {FullMode=BoundedChannelFullMode.Wait,SingleReader=true,SingleWriter=true,AllowSynchronousContinuations=false});
        private readonly CancellationTokenSource _stop=new();
        private readonly long _epoch;
        private readonly Func<double> _gainDb;
        private readonly TimeProvider _clock;
        private readonly object _cleanupGate=new();
        private readonly object _stopGate=new();
        private Task? _captureCleanup;
        private IListeningCaptureSession? _capture;
        private AudioAnalyzer? _analyzer;
        private Task _worker=Task.CompletedTask,_watchdog=Task.CompletedTask;
        private Observation _observation;
        private long _peakResetRequested,_peakResetApplied;
        private int _stopping,_gapBeforeNextPacket,_analyzerDisposed;
        private bool _stopDisposed;
        public ListeningSource Source {get;}
        public ListeningStatus Status=>Volatile.Read(ref _observation).Status;
        public Exception? Failure=>Volatile.Read(ref _observation).Failure;
        public AudioSnapshot Latest
        {
            get
            {
                var analyzer=Volatile.Read(ref _analyzer);var latest=analyzer?.Latest??AudioSnapshot.Empty(_epoch);
                if(Volatile.Read(ref _peakResetRequested)!=Volatile.Read(ref _peakResetApplied))latest=latest with{HeldPeakDbfs=null};
                return Status.State==ListeningState.Listening?latest:AudioSnapshot.Empty(_epoch) with
                    {HeldPeakDbfs=latest.HeldPeakDbfs,Segment=latest.Segment,SampleRate=latest.SampleRate,Channels=latest.Channels};
            }
        }
        public Session(ListeningSource source,long epoch,Func<double> gainDb,TimeProvider clock)
        {
            Source=source;_epoch=epoch;_gainDb=gainDb;_clock=clock;
            _observation=new(new(ListeningState.Opening,source,null,"正在打开侦听…"));
        }
        public void Begin(IListeningCaptureSession capture)
        {
            _capture=capture;
            AudioFormat? format=null;
            var opening=Volatile.Read(ref _observation);
            if(opening.Status.State==ListeningState.Opening)
            {
                format=capture.Format??throw new InvalidDataException("无法读取侦听音频格式。");
                var waiting=new Observation(new(ListeningState.Waiting,Source,format,"等待音频播放"));
                if(!ReferenceEquals(Interlocked.CompareExchange(ref _observation,waiting,opening),opening))format=null;
            }
            _worker=Task.Run(()=>ProcessPacketsAsync(format));
            if(format!=null)_watchdog=Task.Run(WatchAsync);
        }
        public bool Submit(CapturePacket packet)
        {
            if(Volatile.Read(ref _stopping)!=0||Status.State==ListeningState.Faulted)return false;
            bool resetBefore=Volatile.Read(ref _gapBeforeNextPacket)!=0;
            if(_packets.Writer.TryWrite(new(packet,_clock.GetTimestamp(),resetBefore)))
            {if(resetBefore)Interlocked.Exchange(ref _gapBeforeNextPacket,0);return true;}
            // Display may skip a packet under pressure; it cannot contaminate the next
            // contiguous analysis window, and no recording data is involved here.
            Interlocked.Exchange(ref _gapBeforeNextPacket,1);return false;
        }
        public void Fault(Exception error)
        {
            while(Volatile.Read(ref _stopping)==0)
            {
                var current=Volatile.Read(ref _observation);
                if(current.Status.State==ListeningState.Faulted)return;
                var faulted=current with{Status=new(ListeningState.Faulted,Source,current.Status.Format,Describe(error)),Failure=error};
                if(!ReferenceEquals(Interlocked.CompareExchange(ref _observation,faulted,current),current))continue;
                _packets.Writer.TryComplete();SafeCancelStop();
                return;
            }
        }
        public void ResetPeak()=>Interlocked.Increment(ref _peakResetRequested);
        private void SafeCancelStop()
        {
            lock(_stopGate)
            {
                if(!_stopDisposed)_stop.Cancel();
            }
        }
        private async Task ProcessPacketsAsync(AudioFormat? format)
        {
            long frame=0;long? lastPacket=null;int segment=0;
            try
            {
                if(format==null)
                {
                    _packets.Writer.TryComplete();
                    return;
                }
                Volatile.Write(ref _analyzer,new AudioAnalyzer(format,_epoch));
                await foreach(var item in _packets.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))using(var packet=item.Packet)
                {
                    long requestedReset=Volatile.Read(ref _peakResetRequested);
                    bool resetPeak=requestedReset!=Volatile.Read(ref _peakResetApplied);
                    if(item.ResetBefore||packet.Discontinuity||
                        (lastPacket is long last&&_clock.GetElapsedTime(last,item.Arrival)>PacketTimeout))
                    {
                        var old=_analyzer!;double? peak=resetPeak?null:old.Latest.HeldPeakDbfs;
                        var next=new AudioAnalyzer(format,_epoch,segment:++segment,initialPeak:peak);
                        Volatile.Write(ref _analyzer,next);old.Dispose();
                    }
                    else if(resetPeak)_analyzer!.ResetPeak();
                    if(resetPeak)Volatile.Write(ref _peakResetApplied,requestedReset);
                    float[] samples=ArrayPool<float>.Shared.Rent(checked(packet.Frames*format.Channels));
                    try
                    {
                        var span=samples.AsSpan(0,packet.Frames*format.Channels);
                        PcmDecoder.Decode(packet.Data.AsSpan(0,packet.Length),span,format,packet.Silent);
                        double peak=0,gain=Math.Pow(10,_gainDb()/20);
                        for(int i=0;i<span.Length;i++){peak=Math.Max(peak,Math.Abs(span[i]));span[i]=(float)(span[i]*gain);}
                        frame+=packet.Frames;_analyzer!.Process(span,peak,frame);
                        lastPacket=item.Arrival;
                        PublishPacket(item.Arrival);
                    }
                    finally{ArrayPool<float>.Shared.Return(samples);}
                }
            }
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
            catch(Exception error){Fault(error);}
            finally
            {
                while(_packets.Reader.TryRead(out var item))item.Packet.Dispose();
                DisposeAnalyzer();
                if(Status.State==ListeningState.Faulted)await ReleaseCaptureAsync().ConfigureAwait(false);
            }
        }
        private async Task WatchAsync()
        {
            try
            {
                using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                while(await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                {
                    if(!ListeningSources.IsSameApplication(Source))throw new IOException("所选应用已退出，请重新选择应用。");
                    if(_analyzer?.Failure is Exception error)throw new IOException("音频分析停止。",error);
                    var observed=Volatile.Read(ref _observation);
                    if(observed.Status.State==ListeningState.Listening&&
                        _clock.GetElapsedTime(observed.LastPacket)>PacketTimeout)
                    {
                        var waiting=observed with{Status=observed.Status with{State=ListeningState.Waiting,Message="等待音频播放"}};
                        // The timeout belongs to this observation. A packet or fault supersedes it.
                        Interlocked.CompareExchange(ref _observation,waiting,observed);
                    }
                }
            }
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
            catch(Exception error){Fault(error);await ReleaseCaptureAsync().ConfigureAwait(false);}
        }
        private Task ReleaseCaptureAsync()
        {
            lock(_cleanupGate)
            {
                // A native failure can arrive while OpenAsync is still completing.
                // Do not cache an empty cleanup before Begin hands us the capture.
                if(_capture==null)return Task.CompletedTask;
                return _captureCleanup??=Release();
            }
            async Task Release(){if(_capture!=null)await _capture.DisposeAsync().ConfigureAwait(false);}
        }
        private void PublishPacket(long arrival)
        {
            while(Volatile.Read(ref _stopping)==0)
            {
                var current=Volatile.Read(ref _observation);
                if(current.Status.State==ListeningState.Faulted)return;
                var listening=current.Status.State==ListeningState.Listening?current.Status:
                    current.Status with{State=ListeningState.Listening,Message="正在侦听"};
                if(ReferenceEquals(Interlocked.CompareExchange(ref _observation,new(listening,arrival),current),current))return;
            }
        }
        private void DisposeAnalyzer()
        {
            if(Interlocked.Exchange(ref _analyzerDisposed,1)==0)Volatile.Read(ref _analyzer)?.Dispose();
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stopping,1);SafeCancelStop();_packets.Writer.TryComplete();
            try{await ReleaseCaptureAsync().ConfigureAwait(false);}
            finally
            {
                try{await Task.WhenAll(_worker,_watchdog).ConfigureAwait(false);}
                finally
                {
                    while(_packets.Reader.TryRead(out var item))item.Packet.Dispose();
                    DisposeAnalyzer();
                    lock(_stopGate){_stopDisposed=true;_stop.Dispose();}
                }
            }
        }
    }
}
