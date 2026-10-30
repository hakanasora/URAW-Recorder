namespace PortableRecorder.Audio;

public sealed class AudioEngine:IAsyncDisposable
{
    private readonly ICaptureFactory _factory;
    public CalibrationLibrary Calibrations {get;}
    public InputFormatPreferences InputFormats {get;}
    public CalibrationLibrary.Selection CalibrationSelection=>_device==null?new(false,null):Calibrations.GetSelection(_device.Id);
    public bool CalibrationReadyToRecord=>!CalibrationSelection.Enabled||Calibration.Applied&&_calibrationError==null;
    public AudioFormat? PreferredInputFormat=>_device==null?null:InputFormats.Get(_device.Id);
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
    private readonly Func<string,AudioFormat,RecordingEncoding,IRecordingWriter> _createWriter;
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
    public AudioEngine(ICaptureFactory? factory=null,CalibrationLibrary? calibrationLibrary=null,Func<string,AudioFormat,RecordingEncoding,IRecordingWriter>? writerFactory=null,InputFormatPreferences? inputFormats=null)
    {
        _factory=factory??new WasapiCaptureFactory();
        Calibrations=calibrationLibrary??new CalibrationLibrary();
        InputFormats=inputFormats??new InputFormatPreferences();
        _createWriter=writerFactory??((path,format,encoding)=>encoding.CreateWriter(path,format));
    }
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
    public long RecordingClippedSamples=>(_writer as CompressedAudioWriter)?.ClippedSamples??0;
    public int RecordingRate=>_writer!=null?Status.Capture?.Format.SampleRate??0:_lastRate;
    public TimeSpan RecordedTime=>RecordingRate>0?TimeSpan.FromSeconds(WrittenFrames/(double)RecordingRate):TimeSpan.Zero;
    private void Set(AudioRunState state,string message,string? file=null,bool partial=false)
        =>Volatile.Write(ref _status,new(state,_capture?.Info,message,file,partial));
    public void SetGain(double db)
    {
        if(!double.IsFinite(db))throw new ArgumentOutOfRangeException(nameof(db),"增益必须为有限数值。");
        Interlocked.Exchange(ref _gain,Math.Pow(10,Math.Clamp(db,-60,60)/20));
    }
    public Task<EndpointVolumeSnapshot> QueryEndpointVolumeAsync(AudioDevice device)=>_factory.QueryEndpointVolumeAsync(device);
    /// <summary>Release idle microphone capture while another work mode owns monitoring.</summary>
    public async Task SuspendMonitoringAsync()
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if(_writer!=null)throw new InvalidOperationException("请先结束录制再切换工作模式。");
            RawDecisionPending=false;
            await CloseCapture();
            Interlocked.Increment(ref _epoch);
            Volatile.Write(ref _previousPeak,double.NaN);
            Set(AudioRunState.Stopped,"麦克风监看已暂停。");
        }
        finally{_commands.Release();}
    }
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
            var preferred=exclusive?PreferredInputFormat:null;
            var capture=await _factory.OpenAsync(_device!,exclusive,raw,epoch,pipe.Submit,OpeningFault,preferred);
            try
            {
                if(preferred!=null&&capture.Info.Format!=preferred)throw new NotSupportedException("采集实际格式与所选独占格式不一致，已拒绝录制。");
                var plan=CalibrationPlan.Identity;_calibrationError=null;
                var selection=Calibrations.GetSelection(_device!.Id);
                string? id=selection.Enabled?selection.ProfileId:null;
                if(selection.Enabled&&id==null)_calibrationError="校准尚未生效：请选择校准文件，或禁用校准后录制。";
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
            catch(Exception openingError)
            {
                try{await capture.DisposeAsync();}
                catch(Exception cleanupError){throw AudioFailure.Combine(openingError,cleanupError);}
                throw;
            }
            Trace($"Capture ready: device={_device!.Name}; {capture.Info.Format.Description}; exclusive={exclusive}; RAW={raw}; epoch={epoch}");
            if(capture.Info.StreamDiagnostics is string diagnostics)Trace($"Capture stream: epoch={epoch}; {diagnostics}");
        }
        catch(Exception openingError)
        {
            try{await pipe.DisposeAsync();}
            catch(Exception cleanupError){throw AudioFailure.Combine(openingError,cleanupError);}
            throw;
        }
    }
    private async Task CloseCapture()
    {
        var peak=Latest.HeldPeakDbfs;
        if(peak is double held)Volatile.Write(ref _previousPeak,held);
        Interlocked.Increment(ref _epoch);
        // Detach ownership first: a failing native release cannot leave a valid old snapshot
        // or prevent the analysis worker from being stopped.
        var capture=_capture;
        var pipeline=_pipeline;
        _capture=null;
        _pipeline=null;
        Exception? failure=null;
        if(capture!=null)
            try{await capture.DisposeAsync();}catch(Exception error){failure=error;}
        if(pipeline!=null)
            try{await pipeline.DisposeAsync();}catch(Exception error){failure=AudioFailure.Combine(failure,error);}
        if(failure!=null)
        {
            Trace("Capture cleanup failed: "+failure);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
    public static string DefaultRecordingFolder=>System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),"Portable Recorder");
    public async Task StartRecordingAsync(string directory,RecordingMode mode,RecordingEncoding? encoding=null)
    {
        await _commands.WaitAsync();
        bool preparationStarted=false;
        try
        {
            ThrowIfDisposed();if(_writer!=null)throw new InvalidOperationException("已经在录制。");
            if(_device==null)throw new InvalidOperationException("没有选中的输入设备。");
            if(!Enum.IsDefined(mode))throw new ArgumentOutOfRangeException(nameof(mode));
            preparationStarted=true;
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
            encoding??=new();encoding.Validate(_capture.Info.Format);
            Set(AudioRunState.Preparing,"正在准备录音文件");
            directory=string.IsNullOrWhiteSpace(directory)?DefaultRecordingFolder:System.IO.Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            string stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            for(int suffix=0;;suffix++)
            {
                var path=System.IO.Path.Combine(directory,$"Recording-{stamp}{(suffix==0?"":"-"+suffix)}.{encoding.Extension}");
                try{_writer=_createWriter(path,_capture.Info.Format,encoding);break;}
                catch(IOException error) when(AudioFailure.IsNameCollision(error)&&suffix<99){}
            }
            _lastWritten=0;_lastRate=_capture.Info.Format.SampleRate;_summaryOwned=null;
            _recordingCalibration=new{Version=2,Device=_device,Format=_capture.Info.Format,Encoding=encoding,Calibration=calibration,
                Profile=summaryProfile,
                Processing="raw → frequency correction → software gain → monitoring / selected encoder",LinearPhase=true,
                FftRule="nextpow2(Fs*0.34); taps=7*N/8+1; designFFT=4*N; Hamming",AbsoluteSpl=false};
            await _pipeline!.AttachAsync(_writer);
            Set(AudioRunState.Preparing,"等待首批录音样本",_writer.Path);
        }
        catch(Exception preparationError) when(preparationStarted)
        {
            Exception failure=preparationError;
            if(_writer!=null)
            {
                var writer=_writer;
                _writer=null;
                try{await writer.CompleteAsync();}
                catch(Exception error){failure=AudioFailure.Combine(failure,error);}
            }
            // If preparation failed after exclusive capture opened, release it before restoring shared monitoring.
            if(_capture?.Info.Exclusive==true)
            {
                try{await CloseCapture();}catch(Exception error){failure=AudioFailure.Combine(failure,error);}
                await OpenMonitor();
            }
            else if(_capture!=null)Set(AudioRunState.Monitoring,"共享监看");
            Trace("Recording preparation failed: "+failure);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally{_commands.Release();}
    }
    public async Task PauseAsync(bool pause)
    {
        await _commands.WaitAsync();try
        {
            ThrowIfDisposed();
            if(_writer==null)return;
            if(pause==(Status.State==AudioRunState.Paused))return;
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
            string path=_writer.Path;var auxiliary=_writer.AuxiliaryPaths;bool exclusive=_capture?.Info.Exclusive==true;
            // Close/drain the owned file before deleting; this cannot touch earlier recordings.
            try{await StopWriter(false);}
            catch(Exception finalizationError)
            {
                // An explicitly discarded partial file may still be removed, but its
                // finalization failure remains available for diagnostics.
                Trace("Discarded recording could not be finalized: "+finalizationError);
            }
            Exception? failure=null;
            var ownedPaths=new List<string>{path};
            ownedPaths.AddRange(auxiliary);
            if(_summaryOwned!=null)ownedPaths.Add(_summaryOwned);
            foreach(string owned in ownedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(owned);
                    if(owned==_summaryOwned)_summaryOwned=null;
                }
                catch(Exception error){failure=AudioFailure.Combine(failure,error);}
            }
            _lastWritten=0;
            if(exclusive)
            {
                try{await CloseCapture();}catch(Exception error){failure=AudioFailure.Combine(failure,error);}
                await OpenMonitor();
            }
            if(failure!=null){Set(AudioRunState.Faulted,"无法丢弃文件："+Friendly(failure),path,true);throw new IOException("文件已关闭但未能丢弃："+path,failure);}
            if(_capture!=null)Set(AudioRunState.Monitoring,"已丢弃本次录音");
        }
        finally{_commands.Release();}
    }
    private async Task StopWriter(bool restoreMonitor=true,Exception? interruption=null)
    {
        if(_writer==null)return;
        var writer=_writer;
        Set(AudioRunState.Finalizing,"正在完成录音文件",writer.Path);
        Exception? failure=interruption;
        try{if(_pipeline!=null)await _pipeline.DetachAsync(mathematicalDrain:!restoreMonitor);}
        catch(Exception error)
        {
            failure=AudioFailure.Combine(failure,error);
            // A timed-out normal drain must detach before completing the writer.
            try{if(_pipeline!=null)await _pipeline.DetachAsync(mathematicalDrain:true);}
            catch(Exception cleanupError){failure=AudioFailure.Combine(failure,cleanupError);}
        }
        try{await writer.CompleteAsync();}
        catch(Exception error){failure=AudioFailure.Combine(failure,error);}
        Interlocked.Exchange(ref _lastWritten,writer.WrittenFrames);
        _writer=null;

        // A valid saved file and a failed return to monitoring are different outcomes.
        Exception? monitorFailure=null;
        bool exclusive=_capture?.Info.Exclusive==true;
        if(exclusive&&restoreMonitor)
        {
            try{await CloseCapture();}catch(Exception error){monitorFailure=error;}
            await OpenMonitor();
            if(Status.State==AudioRunState.Faulted)
                monitorFailure=AudioFailure.Combine(monitorFailure,new IOException(Status.Message));
        }
        string? summaryError=await Task.Run(()=>SaveCalibrationSummary(writer,failure));
        if(failure!=null)
        {
            if(monitorFailure!=null)failure=AudioFailure.Combine(failure,monitorFailure);
            Trace("Recording finalization failed: "+failure);
            Set(AudioRunState.Faulted,"录制结束异常："+Friendly(failure)+(summaryError==null?"":"；校准摘要写入失败："+summaryError),writer.Path,true);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        string message="已保存："+System.IO.Path.GetFileName(writer.Path);
        if(writer is CompressedAudioWriter compressed && compressed.ClippedSamples>0)
            message+=$"；压缩文件有 {compressed.ClippedSamples} 个超限样本发生削波，录音未中断。使用浮点 WAV 可保留超限幅值。";
        if(summaryError!=null)message+="；音频已保存，但校准摘要写入失败："+summaryError;
        if(monitorFailure!=null)
        {
            Trace("Recording saved, but monitoring recovery failed: "+monitorFailure);
            Set(AudioRunState.Faulted,message+"；恢复监看失败："+Friendly(monitorFailure),writer.Path);
            throw new IOException("音频已保存，但恢复监看失败："+Friendly(monitorFailure),monitorFailure);
        }
        Set(_capture!=null?AudioRunState.Monitoring:AudioRunState.Stopped,message,writer.Path);
    }
    private string? SaveCalibrationSummary(IRecordingWriter writer,Exception? error)
    {
        string? temp=null;
        try
        {
            if(!File.Exists(writer.Path))return null;
            string sidecar=GetRecordingMetadataPath(writer.Path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sidecar)!);
            temp=sidecar+"."+Guid.NewGuid().ToString("N")+".tmp";
            using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                System.Text.Json.JsonSerializer.Serialize(file,new{Recording=_recordingCalibration,Frames=writer.WrittenFrames,Completed=error==null,Error=error==null?null:Friendly(error)});file.Flush(true);
            }
            File.Move(temp,sidecar,false);_summaryOwned=sidecar;return null;
        }
        catch(Exception e){Trace("Calibration summary failed: "+e);return e.Message;}
        finally
        {
            try{if(temp!=null&&File.Exists(temp))File.Delete(temp);}
            catch(Exception cleanupError){Trace("Calibration summary temporary-file cleanup failed: "+cleanupError);}
        }
    }
    public string GetRecordingMetadataPath(string recordingPath)
    {
        string key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(recordingPath).ToUpperInvariant())));
        return System.IO.Path.Combine(Calibrations.Folder,"RecordingHistory",key+".json");
    }
    public async Task SetCalibrationAsync(string? id)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();if(_writer!=null)throw new InvalidOperationException("录制和暂停期间不能更换校准。");
            if(_device==null)throw new InvalidOperationException("尚未选择输入设备。");
            var old=CalibrationSelection;
            await ChangeCalibration(new(id!=null,id??old.ProfileId),false);
        }
        finally{_commands.Release();}
    }
    public async Task SetCalibrationEnabledAsync(bool enabled)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();if(_writer!=null)throw new InvalidOperationException("录制和暂停期间不能更换校准。");
            if(_device==null)throw new InvalidOperationException("尚未选择输入设备。");
            await ChangeCalibration(CalibrationSelection with{Enabled=enabled},true);
        }
        finally{_commands.Release();}
    }
    private async Task ChangeCalibration(CalibrationLibrary.Selection choice,bool allowUnavailable)
    {
        var old=CalibrationSelection;var plan=CalibrationPlan.Identity;string? error=null;
        if(choice.Enabled)
        {
            if(choice.ProfileId==null)error="校准尚未生效：请选择校准文件，或禁用校准后录制。";
            else try{plan=await Task.Run(()=>CalibrationPlan.Prepare(Calibrations.Read(choice.ProfileId),_capture?.Info.Format.SampleRate??48000));}
            catch(Exception e) when(allowUnavailable){error="所选校准不可用，未应用："+e.Message;}
        }
        Calibrations.Configure(_device!.Id,choice);
        try{if(_pipeline!=null)await _pipeline.ApplyCalibrationAsync(plan);}
        catch{Calibrations.Configure(_device.Id,old);throw;}
        _calibrationError=error;Volatile.Write(ref _previousPeak,double.NaN);
        if(_capture!=null)Set(AudioRunState.Monitoring,error??(choice.Enabled?$"已启用频响校准：{plan.Profile!.Name}":"校准已禁用；共享监看"));
    }
    public async Task<InputFormatOptions> QueryInputFormatsAsync()
    {
        await _commands.WaitAsync();
        try{ThrowIfDisposed();if(_device==null)throw new InvalidOperationException("尚未选择输入设备。");return await _factory.QueryFormatsAsync(_device);}
        finally{_commands.Release();}
    }
    public async Task SetInputFormatAsync(AudioFormat? format)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();if(_writer!=null)throw new InvalidOperationException("录制和暂停期间不能更换输入格式。");
            if(_device==null)throw new InvalidOperationException("尚未选择输入设备。");
            if(format!=null&&!(await _factory.QueryFormatsAsync(_device)).Exclusive.Contains(format))throw new NotSupportedException("设备当前不支持此独占输入格式。");
            InputFormats.Set(_device.Id,format);
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
            if(active)
            {
                _calibrationError=null;Volatile.Write(ref _previousPeak,double.NaN);
                if(_capture!=null)Set(AudioRunState.Monitoring,"已移除当前校准；共享监看未应用校准。");
            }
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
        // The detached task contains its own final catch: driver callbacks must never
        // block capture, and a secondary cleanup fault must not become unobserved.
        _=Task.Run(async()=>
        {
            await _commands.WaitAsync();
            try
            {
                if(_disposed || epoch!=_capture?.Info.Epoch)return;
                string? file=_writer?.Path;bool partial=_writer!=null;
                Exception failure=error;
                try{await StopWriter(false,error);}
                catch(Exception finalizationError){failure=finalizationError;}
                try{await CloseCapture();}
                catch(Exception cleanupError){failure=AudioFailure.Combine(failure,cleanupError);}
                Trace("Capture fault: "+failure);
                if(partial && error is CaptureGapException)
                {
                    await OpenMonitor();
                    if(_capture!=null)Set(AudioRunState.Monitoring,"录制因音频数据不连续而停止，文件可能不完整；已恢复共享监看。 "+Friendly(failure),file,true);
                    else Set(AudioRunState.Faulted,Friendly(failure),file,true);
                }
                else Set(AudioRunState.Faulted,Friendly(failure),file,partial);
            }
            catch(Exception unexpected)
            {
                Trace("Fault handling failed: "+unexpected);
                Set(AudioRunState.Faulted,"音频故障收尾失败："+Friendly(unexpected),Status.FilePath,Status.Partial);
            }
            finally{_commands.Release();}
        });
    }
    private void ThrowIfDisposed(){ObjectDisposedException.ThrowIf(_disposed,this);}
    public static string Friendly(Exception error)=>AudioFailure.Describe(error);
    public async ValueTask DisposeAsync()
    {
        await _commands.WaitAsync();
        try
        {
            if(_disposed)return;
            _disposed=true;
            RawDecisionPending=false;
            Exception? failure=null;
            string? file=_writer?.Path;
            bool partial=false;
            try{await StopWriter(false);}
            catch(Exception error){failure=error;partial=true;}
            try{await CloseCapture();}
            catch(Exception error){failure=AudioFailure.Combine(failure,error);}
            if(failure!=null)
            {
                Trace("Audio service shutdown failed: "+failure);
                Set(AudioRunState.Faulted,"关闭音频服务失败："+Friendly(failure),file,partial);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            else Set(AudioRunState.Stopped,"已关闭音频服务");
        }
        finally{_commands.Release();}
    }
}
