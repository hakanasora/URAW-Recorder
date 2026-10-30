using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;

namespace PortableRecorder.Audio;

/// <summary>Read-only native loopback monitoring. Never opens a writer or microphone calibration stage.</summary>
public sealed class ListeningService:IAsyncDisposable
{
    private readonly IListeningCaptureFactory _factory;
    private readonly SemaphoreSlim _commands=new(1,1);
    private Session? _session;
    private ListeningStatus _status=new(ListeningState.Stopped,null,null,"");
    private double _gainDb;
    private long _epoch;
    private bool _disposed;
    public ListeningService(IListeningCaptureFactory? factory=null)=>_factory=factory??new WasapiListeningCaptureFactory();
    public ListeningStatus Status=>Volatile.Read(ref _session)?.Status??Volatile.Read(ref _status);
    public AudioSnapshot Latest=>Volatile.Read(ref _session)?.Latest??AudioSnapshot.Empty(Volatile.Read(ref _epoch));
    public double GainDb=>Volatile.Read(ref _gainDb);
    public void SetGain(double db)
    {
        if(!double.IsFinite(db))throw new ArgumentOutOfRangeException(nameof(db));
        Volatile.Write(ref _gainDb,Math.Round(Math.Clamp(db,-60,60),1));
    }
    public void ResetPeak()=>Volatile.Read(ref _session)?.ResetPeak();

    public async Task StartAsync(ListeningSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if(!Enum.IsDefined(source.Scope))throw new ArgumentOutOfRangeException(nameof(source));
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            await StopCoreAsync().ConfigureAwait(false);
            long epoch=Interlocked.Increment(ref _epoch);
            Volatile.Write(ref _status,new(ListeningState.Opening,source,null,"正在打开侦听…"));
            var session=new Session(source,epoch,()=>GainDb);
            Volatile.Write(ref _session,session);
            try
            {
                var capture=await _factory.OpenAsync(source,session.Submit,session.Fault).ConfigureAwait(false);
                session.Begin(capture);
            }
            catch(Exception error)
            {
                session.Fault(error);
                await session.DisposeAsync().ConfigureAwait(false);
                Volatile.Write(ref _session,null);
                Volatile.Write(ref _status,new(ListeningState.Faulted,source,null,Describe(error)));
                throw;
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
        var session=Interlocked.Exchange(ref _session,null);
        var previous=Status;
        try{if(session!=null)await session.DisposeAsync().ConfigureAwait(false);}
        finally{Volatile.Write(ref _status,new(ListeningState.Stopped,session?.Source??previous.Source,null,""));}
    }
    public async ValueTask DisposeAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try{if(_disposed)return;_disposed=true;await StopCoreAsync().ConfigureAwait(false);}
        finally{_commands.Release();}
        // Commands arriving after disposal still acquire this gate and observe _disposed.
    }
    private static string Describe(Exception e)=>e.HResult switch
    {
        unchecked((int)0x88890004)=>"输出设备已移除或重新配置，请重新选择。",
        unchecked((int)0x80070005)=>"Windows 拒绝访问所选音频源。",
        unchecked((int)0x88890010)=>"Windows 音频服务未运行。",
        _=>e.Message
    };

    private sealed class Session:IAsyncDisposable
    {
        private readonly record struct QueuedPacket(CapturePacket Packet,long Arrival,bool ResetBefore);
        private readonly Channel<QueuedPacket> _packets=Channel.CreateBounded<QueuedPacket>(new BoundedChannelOptions(64)
            {FullMode=BoundedChannelFullMode.Wait,SingleReader=true,SingleWriter=true,AllowSynchronousContinuations=false});
        private readonly CancellationTokenSource _stop=new();
        private readonly long _epoch;
        private readonly Func<double> _gainDb;
        private readonly object _cleanupGate=new();
        private Task? _captureCleanup;
        private IListeningCaptureSession? _capture;
        private AudioAnalyzer? _analyzer;
        private Task _worker=Task.CompletedTask,_watchdog=Task.CompletedTask;
        private ListeningStatus _status;
        private Exception? _failure;
        private long _lastPacket;
        private int _stopping,_gapBeforeNextPacket,_peakReset,_analyzerDisposed;
        public ListeningSource Source {get;}
        public ListeningStatus Status=>Volatile.Read(ref _status);
        public AudioSnapshot Latest
        {
            get
            {
                var analyzer=Volatile.Read(ref _analyzer);var latest=analyzer?.Latest??AudioSnapshot.Empty(_epoch);
                if(Volatile.Read(ref _peakReset)!=0)latest=latest with{HeldPeakDbfs=null};
                return Status.State==ListeningState.Listening?latest:AudioSnapshot.Empty(_epoch) with
                    {HeldPeakDbfs=latest.HeldPeakDbfs,Segment=latest.Segment,SampleRate=latest.SampleRate,Channels=latest.Channels};
            }
        }
        public Session(ListeningSource source,long epoch,Func<double> gainDb)
        {Source=source;_epoch=epoch;_gainDb=gainDb;_status=new(ListeningState.Opening,source,null,"正在打开侦听…");}
        public void Begin(IListeningCaptureSession capture)
        {
            _capture=capture;
            if(Volatile.Read(ref _failure)!=null){_packets.Writer.TryComplete();_ = ReleaseCaptureAsync();return;}
            SetOperationalState(ListeningState.Waiting,"等待音频播放");
            _worker=Task.Run(ProcessPacketsAsync);_watchdog=Task.Run(WatchAsync);
        }
        public bool Submit(CapturePacket packet)
        {
            if(Volatile.Read(ref _stopping)!=0||Volatile.Read(ref _failure)!=null)return false;
            bool resetBefore=Volatile.Read(ref _gapBeforeNextPacket)!=0;
            if(_packets.Writer.TryWrite(new(packet,Stopwatch.GetTimestamp(),resetBefore)))
            {if(resetBefore)Interlocked.Exchange(ref _gapBeforeNextPacket,0);return true;}
            // Display may skip a packet under pressure; it cannot contaminate the next
            // contiguous analysis window, and no recording data is involved here.
            Interlocked.Exchange(ref _gapBeforeNextPacket,1);return false;
        }
        public void Fault(Exception error)
        {
            if(Volatile.Read(ref _stopping)!=0)return;
            if(Interlocked.CompareExchange(ref _failure,error,null)!=null)return;
            Volatile.Write(ref _status,new(ListeningState.Faulted,Source,_capture?.Format,Describe(error)));
            _packets.Writer.TryComplete();_stop.Cancel();
        }
        public void ResetPeak()=>Interlocked.Exchange(ref _peakReset,1);
        private async Task ProcessPacketsAsync()
        {
            long frame=0;int segment=0;
            try
            {
                var format=_capture!.Format;
                Volatile.Write(ref _analyzer,new AudioAnalyzer(format,_epoch));
                await foreach(var item in _packets.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))using(var packet=item.Packet)
                {
                    long now=item.Arrival,last=Volatile.Read(ref _lastPacket);
                    bool resetPeak=Interlocked.Exchange(ref _peakReset,0)!=0;
                    if(item.ResetBefore||packet.Discontinuity||
                        (last!=0&&Stopwatch.GetElapsedTime(last,now).TotalMilliseconds>500))
                    {
                        var old=_analyzer!;double? peak=resetPeak?null:old.Latest.HeldPeakDbfs;
                        var next=new AudioAnalyzer(format,_epoch,segment:++segment,initialPeak:peak);
                        Volatile.Write(ref _analyzer,next);old.Dispose();
                    }
                    if(resetPeak)_analyzer!.ResetPeak();
                    float[] samples=ArrayPool<float>.Shared.Rent(checked(packet.Frames*format.Channels));
                    try
                    {
                        var span=samples.AsSpan(0,packet.Frames*format.Channels);
                        PcmDecoder.Decode(packet.Data.AsSpan(0,packet.Length),span,format,packet.Silent);
                        double peak=0,gain=Math.Pow(10,_gainDb()/20);
                        for(int i=0;i<span.Length;i++){peak=Math.Max(peak,Math.Abs(span[i]));span[i]=(float)(span[i]*gain);}
                        frame+=packet.Frames;_analyzer!.Process(span,peak,frame);
                        Volatile.Write(ref _lastPacket,now);
                        SetOperationalState(ListeningState.Listening,"正在侦听");
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
                if(Volatile.Read(ref _failure)!=null)await ReleaseCaptureAsync().ConfigureAwait(false);
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
                    long last=Volatile.Read(ref _lastPacket);
                    if(last==0||Stopwatch.GetElapsedTime(last).TotalMilliseconds>500)
                        SetOperationalState(ListeningState.Waiting,"等待音频播放");
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
        private void SetOperationalState(ListeningState state,string message)
        {
            var current=Volatile.Read(ref _status);
            if(Volatile.Read(ref _failure)!=null||Volatile.Read(ref _stopping)!=0)return;
            Interlocked.CompareExchange(ref _status,new(state,Source,_capture?.Format,message),current);
        }
        private void DisposeAnalyzer()
        {
            if(Interlocked.Exchange(ref _analyzerDisposed,1)==0)Volatile.Read(ref _analyzer)?.Dispose();
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stopping,1);_stop.Cancel();_packets.Writer.TryComplete();
            try{await ReleaseCaptureAsync().ConfigureAwait(false);}
            finally
            {
                try{await Task.WhenAll(_worker,_watchdog).ConfigureAwait(false);}
                finally
                {
                    while(_packets.Reader.TryRead(out var item))item.Packet.Dispose();
                    DisposeAnalyzer();_stop.Dispose();
                }
            }
        }
    }
}
