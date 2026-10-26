namespace PortableRecorder.Audio;

public sealed class AudioEngine:IAsyncDisposable
{
    private readonly ICaptureFactory _factory;
    public CalibrationLibrary Calibrations {get;}
    private string? _calibrationError; private string? _summaryOwned; private object? _recordingCalibration;
    public CalibrationStatus Calibration
    {
        get
        {
            var status=_pipeline?.Calibration??CalibrationStatus.Off();
            string? warning=_calibrationError??Calibrations.Warning;
            return warning==null?status:status with{Notice=warning+"\n"+status.Notice};
        }
    }
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
    private HashSet<string> _knownDevices=[];
    public AudioDevice? SelectedDevice=>_device;
    public event Action<string>? Diagnostic;
    private void Trace(string message){try{Diagnostic?.Invoke(message);}catch{/* Diagnostics cannot stop capture. */}}
    public AudioEngine(ICaptureFactory? factory=null,CalibrationLibrary? calibrationLibrary=null,Func<string,AudioFormat,IRecordingWriter>? writerFactory=null)
    {_factory=factory??new WasapiCaptureFactory();Calibrations=calibrationLibrary??new CalibrationLibrary();_createWriter=writerFactory??((path,format)=>new FloatWaveWriter(path,format));}
    public IReadOnlyList<AudioDevice> EnumerateDevices()=>_factory.Enumerate();
    public AudioStatus Status
    {
        get
        {
            var status=Volatile.Read(ref _status);
            int recovered=_pipeline?.RecoveredDiscontinuities??0;
            if(status.State==AudioRunState.Monitoring && recovered>0)
                return status with{Message=status.Message+$"；已从 {recovered} 次短暂数据不连续恢复监看。"};
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
    public async Task ReconcileDevicesAsync(IReadOnlyList<AudioDevice> devices,bool manualRefresh=false)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            var added=devices.Where(d=>!_knownDevices.Contains(d.Id)).ToArray();
            _knownDevices=devices.Select(d=>d.Id).ToHashSet();
            var current=devices.FirstOrDefault(d=>d.Id==_device?.Id);
            // Recording never changes source mid-file. A removed source is finalized, then monitoring recovers.
            if(_writer!=null && current!=null)return;
            bool lostRecording=_writer!=null;
            string? endedPath=_writer?.Path;Exception? endingError=null;
            if(lostRecording){try{await StopWriter(false,new IOException("录制输入设备已移除。"));}catch(Exception error){endingError=error;}}
            var chosen=(!manualRefresh?added.FirstOrDefault():null)??current??devices.FirstOrDefault(d=>d.IsDefault)??devices.FirstOrDefault();
            bool changed=chosen?.Id!=_device?.Id;
            if(!changed && _capture!=null && !manualRefresh && !lostRecording)return;
            await CloseCapture();
            if(changed){_normalConsent=false;Volatile.Write(ref _previousPeak,double.NaN);}_device=chosen;RawDecisionPending=false;
            if(chosen==null)Set(AudioRunState.Stopped,"没有可用输入设备；插入设备后将自动恢复监看。",endedPath,endingError!=null);
            else
            {
                await OpenMonitor();
                if(lostRecording && _capture!=null)
                    Set(AudioRunState.Monitoring,endingError==null?"输入设备已断开，本次录音已结束；已切换共享监看。":"输入设备断开，录音收尾异常；已恢复共享监看。",endedPath,endingError!=null);
            }
        }
        finally{_commands.Release();}
    }
    public async Task SelectDeviceAsync(AudioDevice device)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();if(_writer!=null)throw new InvalidOperationException("录制期间不能更换输入设备。");
            bool changed=_device?.Id!=device.Id;_device=device;if(changed)_normalConsent=false;
            RawDecisionPending=false;await CloseCapture();if(changed)Volatile.Write(ref _previousPeak,double.NaN);await OpenMonitor();
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
            string message=_normalConsent?"共享监看 · 普通输入（仅本次许可）":"共享监看 · RAW 请求已接受";
            string? calibrationWarning=_calibrationError??Calibrations.Warning;
            Set(AudioRunState.Monitoring,message+(calibrationWarning==null?"":"；"+calibrationWarning));
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
        Exception? openingFault=null;
        void OpeningFault(Exception error){Interlocked.CompareExchange(ref openingFault,error,null);ReportFault(epoch,error);}
        var pipe=new ProcessingPipeline(()=>Volatile.Read(ref _gain),OpeningFault,Trace);
        try
        {
            var capture=await _factory.OpenAsync(_device!,exclusive,raw,epoch,pipe.Submit,OpeningFault);
            try
            {
                var plan=CalibrationPlan.Identity;_calibrationError=null;
                string? id=Calibrations.SelectedId(_device!.Id);
                if(id!=null)
                {
                    try{plan=await Task.Run(()=>CalibrationPlan.Prepare(Calibrations.Read(id),capture.Info.Format.SampleRate));}
                    catch(Exception e){_calibrationError="所选校准不可用，已停用："+e.Message;Trace(_calibrationError);}
                }
                if(Volatile.Read(ref openingFault) is Exception earlyError)throw new IOException("准备校准时采集已停止："+earlyError.Message,earlyError);
                pipe.Configure(capture.Info,plan);await pipe.Ready;
                if(Volatile.Read(ref openingFault) is Exception initializationError)throw new IOException("初始化校准处理失败："+initializationError.Message,initializationError);
                _pipeline=pipe;_capture=capture;
                if(Volatile.Read(ref openingFault) is Exception racedError)ReportFault(epoch,racedError);
            }
            catch{await capture.DisposeAsync();throw;}
            Trace($"Capture ready: device={_device!.Name}; {capture.Info.Format.Description}; exclusive={exclusive}; RAW={raw}; epoch={epoch}");
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
            if(_calibrationError!=null)throw new InvalidOperationException(_calibrationError+" 请重新选择校准或明确关闭校准后录制。");
            if(!_capture!.Info.Raw)throw new InvalidOperationException("正式录制必须接受 RAW 请求。");
            var calibration=Calibration;
            var summaryProfile=calibration.ProfileId==null?null:await Task.Run(()=>Calibrations.Read(calibration.ProfileId));
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
            _lastWritten=0;_lastRate=_capture.Info.Format.SampleRate;_summaryOwned=null;
            _recordingCalibration=new{Version=1,Device=_device,Format=_capture.Info.Format,Calibration=calibration,
                Profile=summaryProfile,
                Processing="raw → frequency correction → software gain → monitoring / float WAV",LinearPhase=true,
                FftRule="nextpow2(Fs*0.34); taps=7*N/8+1; designFFT=4*N; Hamming",AbsoluteSpl=false};
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
    public async Task DiscardRecordingAsync()
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if(_writer==null || Status.State!=AudioRunState.Paused)throw new InvalidOperationException("请先暂停，再丢弃本次录音。");
            string path=_writer.Path;bool exclusive=_capture?.Info.Exclusive==true;
            // Close/drain the owned file before deleting; this cannot touch earlier recordings.
            try{await StopWriter(false);}catch{/* An explicitly discarded partial file may also be removed. */}
            Exception? failure=null;try{File.Delete(path);if(_summaryOwned!=null){File.Delete(_summaryOwned);_summaryOwned=null;}}catch(Exception error){failure=error;}
            _lastWritten=0;
            if(exclusive){await CloseCapture();await OpenMonitor();}
            if(failure!=null){Set(AudioRunState.Faulted,"无法丢弃文件："+Friendly(failure),path,true);throw new IOException("文件已关闭但未能丢弃："+path,failure);}
            if(_capture!=null)Set(AudioRunState.Monitoring,"已丢弃本次录音");
        }
        finally{_commands.Release();}
    }
    private async Task StopWriter(bool restoreMonitor=true,Exception? interruption=null)
    {
        if(_writer==null)return;
        var writer=_writer;Set(AudioRunState.Finalizing,"正在完成 WAV 文件",writer.Path);
        Exception? failure=interruption;
        try{if(_pipeline!=null)await _pipeline.DetachAsync(mathematicalDrain:!restoreMonitor);}
        catch(Exception e)
        {
            failure=e;
            // A timed-out normal drain must detach before completing the writer.
            try{if(_pipeline!=null)await _pipeline.DetachAsync(mathematicalDrain:true);}catch{}
        }
        try{await writer.CompleteAsync();}catch(Exception e){failure=e;}
        Interlocked.Exchange(ref _lastWritten,writer.WrittenFrames);_writer=null;
        bool exclusive=_capture?.Info.Exclusive==true;
        if(exclusive && restoreMonitor){await CloseCapture();await OpenMonitor();}
        string? summaryError=await Task.Run(()=>SaveCalibrationSummary(writer,failure));
        if(failure!=null)
        {
            Set(AudioRunState.Faulted,"录制结束异常："+Friendly(failure)+(summaryError==null?"":"；校准摘要写入失败："+summaryError),writer.Path,true);
            throw failure;
        }
        string message="已保存："+System.IO.Path.GetFileName(writer.Path);
        if(summaryError!=null)message+="；音频已保存，但校准摘要写入失败："+summaryError;
        Set(_capture!=null?AudioRunState.Monitoring:AudioRunState.Stopped,message,writer.Path);
    }
    private string? SaveCalibrationSummary(IRecordingWriter writer,Exception? error)
    {
        string? temp=null;
        try
        {
            if(!File.Exists(writer.Path))return null;
            string sidecar=writer.Path+".calibration.json";
            temp=sidecar+"."+Guid.NewGuid().ToString("N")+".tmp";
            using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                System.Text.Json.JsonSerializer.Serialize(file,new{Recording=_recordingCalibration,Frames=writer.WrittenFrames,Completed=error==null,Error=error?.Message});file.Flush(true);
            }
            File.Move(temp,sidecar,false);_summaryOwned=sidecar;return null;
        }
        catch(Exception e){Trace("Calibration summary failed: "+e);return e.Message;}
        finally{try{if(temp!=null&&File.Exists(temp))File.Delete(temp);}catch{/* Report the primary summary error. */}}
    }
    public async Task SetCalibrationAsync(string? id)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();if(_writer!=null)throw new InvalidOperationException("录制和暂停期间不能更换校准。");
            if(_device==null)throw new InvalidOperationException("尚未选择输入设备。");
            var oldId=Calibrations.SelectedId(_device.Id);
            var plan=id==null?CalibrationPlan.Identity:await Task.Run(()=>CalibrationPlan.Prepare(Calibrations.Read(id),_capture?.Info.Format.SampleRate??48000));
            // Persist first. A persistence failure cannot silently change the live processor.
            Calibrations.Bind(_device.Id,id);
            try{if(_pipeline!=null)await _pipeline.ApplyCalibrationAsync(plan);}
            catch{Calibrations.Bind(_device.Id,oldId);throw;}
            _calibrationError=null;Volatile.Write(ref _previousPeak,double.NaN);
            if(_capture!=null)Set(AudioRunState.Monitoring,id==null?"校准已关闭；共享监看":$"已启用频响校准：{plan.Profile!.Name}");
        }
        finally{_commands.Release();}
    }
    public async Task<CalibrationLibrary.Entry> ImportCalibrationAsync(string path)
    {
        await _commands.WaitAsync();try{ThrowIfDisposed();if(_writer!=null)throw new InvalidOperationException("录制和暂停期间不能导入校准。");return await Task.Run(()=>Calibrations.Import(path));}finally{_commands.Release();}
    }
    public async Task RemoveCalibrationAsync(string id)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();if(_writer!=null)throw new InvalidOperationException("录制和暂停期间不能移除校准。");
            bool active=_device!=null&&Calibrations.SelectedId(_device.Id)==id;
            var old=active?_pipeline?.Plan:null;
            if(active&&_pipeline!=null)await _pipeline.ApplyCalibrationAsync(CalibrationPlan.Identity);
            try{Calibrations.Remove(id);}
            catch{if(old!=null&&_pipeline!=null)await _pipeline.ApplyCalibrationAsync(old);throw;}
            if(active){_calibrationError=null;Volatile.Write(ref _previousPeak,double.NaN);}
        }
        finally{_commands.Release();}
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
                try{await StopWriter(false,error);}catch{}
                await CloseCapture();
                Trace("Capture fault: "+error);
                if(partial && error is CaptureGapException)
                {
                    await OpenMonitor();
                    if(_capture!=null)Set(AudioRunState.Monitoring,"录制因音频数据不连续而停止，文件可能不完整；已恢复共享监看。",file,true);
                    else Set(AudioRunState.Faulted,Friendly(error),file,true);
                }
                else Set(AudioRunState.Faulted,Friendly(error),file,partial);
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
