namespace PortableRecorder.Audio;

public sealed class AudioEngine:IAsyncDisposable
{
    private readonly ICaptureFactory _factory;
    private readonly ICalibrationStage _calibration;
    private readonly Func<string,AudioFormat,IRecordingWriter> _createWriter;
    private readonly SemaphoreSlim _commands=new(1,1);
    private IAudioCaptureService? _capture;
    private ProcessingPipeline? _pipeline;
    private IRecordingWriter? _writer;
    private AudioDevice? _device;
    private AudioStatus _status=new(AudioRunState.Stopped,null,"正在枚举输入设备");
    private double _gain=1;
    private bool _normalConsent,_disposed;
    private long _epoch,_decision,_lastWritten;
    private int _lastRate;
    private double _previousPeak=double.NaN;
    public AudioEngine(ICaptureFactory? factory=null,ICalibrationStage? calibration=null,Func<string,AudioFormat,IRecordingWriter>? writerFactory=null)
    {_factory=factory??new WasapiCaptureFactory();_calibration=calibration??new IdentityCalibration();_createWriter=writerFactory??((path,format)=>new FloatWaveWriter(path,format));}
    public IReadOnlyList<AudioDevice> EnumerateDevices()=>_factory.Enumerate();
    public AudioStatus Status
    {
        get
        {
            var status=Volatile.Read(ref _status);
            return status.State==AudioRunState.Preparing && (_writer?.WrittenFrames??0)>0?
                status with{State=AudioRunState.Recording,Message="录制中"}:status;
        }
    }
    public long RawDecisionId=>Interlocked.Read(ref _decision);
    public bool RawDecisionPending {get;private set;}
    public AudioSnapshot Latest
    {
        get
        {
            var snapshot=_pipeline?.Latest??AudioSnapshot.Empty(Interlocked.Read(ref _epoch));
            double previous=Volatile.Read(ref _previousPeak);
            return snapshot.Valid && !double.IsNaN(previous)?snapshot with
                {HeldPeakDbfs=snapshot.HeldPeakDbfs is double current?Math.Max(current,previous):previous}:snapshot;
        }
    }
    public long WrittenFrames=>_writer?.WrittenFrames??Interlocked.Read(ref _lastWritten);
    public int RecordingRate=>_writer!=null?Status.Capture?.Format.SampleRate??0:_lastRate;
    public TimeSpan RecordedTime=>RecordingRate>0?TimeSpan.FromSeconds(WrittenFrames/(double)RecordingRate):TimeSpan.Zero;
    private void Set(AudioRunState state,string message,string? file=null,bool partial=false)
        =>Volatile.Write(ref _status,new(state,_capture?.Info,message,file,partial));
    public void SetGain(double db)=>Interlocked.Exchange(ref _gain,Math.Pow(10,Math.Clamp(db,-60,24)/20));
    public async Task SelectDeviceAsync(AudioDevice device)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();if(_writer!=null)throw new InvalidOperationException("录制期间不能更换输入设备。");
            bool changed=_device?.Id!=device.Id;_device=device;if(changed)_normalConsent=false;
            RawDecisionPending=false;await CloseCapture();await OpenMonitor();
        }
        finally{_commands.Release();}
    }
    public async Task DecideRawFallbackAsync(bool allow)
    {
        await _commands.WaitAsync();
        try
        {
            if(_disposed || !RawDecisionPending)return;RawDecisionPending=false;
            if(allow){_normalConsent=true;await OpenMonitor();}
            else Set(AudioRunState.Stopped,"监看已停止；尚未获得 RAW 输入。");
        }
        finally{_commands.Release();}
    }
    private async Task OpenMonitor()
    {
        if(_device==null || _disposed)return;
        try
        {
            await OpenCapture(false,!_normalConsent);
            Set(AudioRunState.Monitoring,_normalConsent?"共享监看 · 普通输入（仅本次许可）":"共享监看 · RAW 请求已接受");
        }
        catch(RawUnavailableException error)
        {
            RawDecisionPending=true;Interlocked.Increment(ref _decision);Set(AudioRunState.Stopped,error.Message);
        }
        catch(Exception error){Set(AudioRunState.Faulted,Friendly(error));}
    }
    private async Task OpenCapture(bool exclusive,bool raw)
    {
        Set(AudioRunState.Opening,"正在打开输入设备");long epoch=Interlocked.Increment(ref _epoch);
        var pipe=new ProcessingPipeline(()=>Volatile.Read(ref _gain),_calibration,error=>ReportFault(epoch,error));
        try
        {
            var capture=await _factory.OpenAsync(_device!,exclusive,raw,epoch,pipe.Submit,error=>ReportFault(epoch,error));
            pipe.Configure(capture.Info);_pipeline=pipe;_capture=capture;
        }
        catch{await pipe.DisposeAsync();throw;}
    }
    private async Task CloseCapture()
    {
        var peak=Latest.HeldPeakDbfs;
        if(peak is double held)Volatile.Write(ref _previousPeak,held);
        Interlocked.Increment(ref _epoch);
        if(_capture!=null){var old=_capture;_capture=null;await old.DisposeAsync();}
        if(_pipeline!=null){var old=_pipeline;_pipeline=null;await old.DisposeAsync();}
    }
    public async Task StartRecordingAsync(string directory,RecordingMode mode)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();if(_writer!=null)throw new InvalidOperationException("已经在录制。");
            if(_device==null)throw new InvalidOperationException("没有选中的输入设备。");
            RawDecisionPending=false;
            bool exclusive=mode==RecordingMode.Exclusive;
            if(_capture==null || exclusive || !_capture.Info.Raw)
            {
                await CloseCapture();
                try{await OpenCapture(exclusive,true);}
                catch(Exception error){await OpenMonitor();throw new IOException("录制输入打开失败："+Friendly(error),error);}
            }
            if(!_capture!.Info.Raw)throw new InvalidOperationException("正式录制必须接受 RAW 请求。");
            Set(AudioRunState.Preparing,"正在准备 WAV 文件");
            directory=string.IsNullOrWhiteSpace(directory)?System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),"Portable Recorder"):System.IO.Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            string stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            for(int suffix=0;;suffix++)
            {
                var path=System.IO.Path.Combine(directory,$"Recording-{stamp}{(suffix==0?"":"-"+suffix)}.wav");
                try{_writer=_createWriter(path,_capture.Info.Format);break;}
                catch(IOException) when(File.Exists(path) && suffix<99){}
            }
            _lastWritten=0;_lastRate=_capture.Info.Format.SampleRate;
            await _pipeline!.AttachAsync(_writer);
            Set(AudioRunState.Preparing,"等待首批录音样本",_writer.Path);
        }
        catch
        {
            if(_writer!=null){try{await _writer.CompleteAsync();}catch{} _writer=null;}
            // If preparation failed after exclusive capture opened, release it before restoring shared monitoring.
            if(_capture?.Info.Exclusive==true){await CloseCapture();await OpenMonitor();}
            else if(_capture!=null)Set(AudioRunState.Monitoring,"共享监看");
            throw;
        }
        finally{_commands.Release();}
    }
    public async Task PauseAsync(bool pause)
    {
        await _commands.WaitAsync();try
        {
            if(_writer==null)return;
            await _pipeline!.PauseAsync(pause);
            Set(pause?AudioRunState.Paused:AudioRunState.Recording,pause?"暂停中":"录制中",_writer.Path);
        }finally{_commands.Release();}
    }
    public async Task StopRecordingAsync()
    {
        await _commands.WaitAsync();try{await StopWriter();}finally{_commands.Release();}
    }
    private async Task StopWriter(bool restoreMonitor=true)
    {
        if(_writer==null)return;
        var writer=_writer;Set(AudioRunState.Finalizing,"正在完成 WAV 文件",writer.Path);
        Exception? failure=null;
        try{if(_pipeline!=null)await _pipeline.DetachAsync();}catch(Exception e){failure=e;}
        try{await writer.CompleteAsync();}catch(Exception e){failure=e;}
        Interlocked.Exchange(ref _lastWritten,writer.WrittenFrames);_writer=null;
        bool exclusive=_capture?.Info.Exclusive==true;
        if(exclusive && restoreMonitor){await CloseCapture();await OpenMonitor();}
        if(failure!=null){Set(AudioRunState.Faulted,"录制结束异常："+Friendly(failure),writer.Path,true);throw failure;}
        else Set(_capture!=null?AudioRunState.Monitoring:AudioRunState.Stopped,"已保存："+System.IO.Path.GetFileName(writer.Path),writer.Path);
    }
    public async Task ResetPeakAsync()
    {
        await _commands.WaitAsync();
        try{if(_pipeline!=null)await _pipeline.ResetPeakAsync();Volatile.Write(ref _previousPeak,double.NaN);}
        finally{_commands.Release();}
    }
    private void ReportFault(long epoch,Exception error)
    {
        _=Task.Run(async()=>
        {
            await _commands.WaitAsync();
            try
            {
                if(_disposed || epoch!=_capture?.Info.Epoch)return;
                string? file=_writer?.Path;bool partial=_writer!=null;
                try{await StopWriter(false);}catch{}
                await CloseCapture();Set(AudioRunState.Faulted,Friendly(error),file,partial);
            }
            finally{_commands.Release();}
        });
    }
    private void ThrowIfDisposed(){ObjectDisposedException.ThrowIf(_disposed,this);}
    public static string Friendly(Exception error)=>error.HResult switch
    {
        unchecked((int)0x8889000A)=>"输入设备忙，可能正被其他程序独占。",
        unchecked((int)0x8889000E)=>"Windows 或驱动已禁止此设备的独占模式。",
        unchecked((int)0x88890004)=>"输入设备已断开或被系统重新配置。",
        unchecked((int)0x88890010)=>"Windows 音频服务未运行。",
        unchecked((int)0x80070005)=>"无法访问麦克风，请检查 Windows 麦克风隐私权限。",
        _=>error.Message
    };
    public async ValueTask DisposeAsync()
    {
        await _commands.WaitAsync();
        try
        {
            if(_disposed)return;_disposed=true;RawDecisionPending=false;
            try{await StopWriter(false);}finally{await CloseCapture();}
            Set(AudioRunState.Stopped,"已关闭音频服务");
        }
        finally{_commands.Release();}
    }
}
