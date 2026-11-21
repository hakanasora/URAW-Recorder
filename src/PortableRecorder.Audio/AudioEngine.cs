using System.Runtime.ExceptionServices;

namespace PortableRecorder.Audio;

public sealed partial class AudioEngine:IAsyncDisposable
{
    private readonly ICaptureFactory _factory;
    public CalibrationLibrary Calibrations {get;}
    public InputFormatPreferences InputFormats {get;}
    private readonly RecordingStore _recordings;
    private readonly LiveMonitorController _liveMonitor;
    private readonly Func<string,AudioFormat,RecordingEncoding,IRecordingWriter> _createWriter;
    private readonly SemaphoreSlim _commands=new(1,1);
    private EnginePhase _phase=new EnginePhase.Stopped(null,false,"正在枚举输入设备");
    private string? _calibrationError;
    private double _gain=1,_previousPeak=double.NaN;
    private long _epoch,_decision,_lastWritten;
    private int _lastRate;
    private HashSet<string> _knownDevices=[];

    public event Action<string>? Diagnostic;
    public event Action<string>? LiveMonitorFaulted;
    private void Trace(string message){try{Diagnostic?.Invoke(message);}catch{/* Diagnostics cannot stop capture. */}}

    public AudioEngine(ICaptureFactory? factory=null,CalibrationLibrary? calibrationLibrary=null,Func<string,AudioFormat,RecordingEncoding,IRecordingWriter>? writerFactory=null,InputFormatPreferences? inputFormats=null,ILiveMonitorFactory? liveMonitorFactory=null)
    {
        _factory=factory??new WasapiCaptureFactory();
        Calibrations=calibrationLibrary??new CalibrationLibrary();
        InputFormats=inputFormats??new InputFormatPreferences();
        _recordings=new(Calibrations,Trace);
        _createWriter=writerFactory??((path,format,encoding)=>encoding.CreateWriter(path,format));
        _liveMonitor=new(liveMonitorFactory??new WasapiLiveMonitorFactory(),Trace);
        _liveMonitor.Faulted+=OnLiveMonitorFaulted;
    }

    private EnginePhase CurrentPhase=>Volatile.Read(ref _phase);
    private void SetPhase(EnginePhase next)=>Volatile.Write(ref _phase,next);

    public AudioDevice? SelectedDevice=>PhaseDevice(CurrentPhase);
    public CalibrationLibrary.Selection CalibrationSelection=>SelectedDevice is { } device?Calibrations.GetSelection(device.Id):new(false,null);
    public bool CalibrationReadyToRecord=>!CalibrationSelection.Enabled||Calibration.Applied&&Volatile.Read(ref _calibrationError)==null;
    public AudioFormat? PreferredInputFormat=>SelectedDevice is { } device?InputFormats.Get(device.Id):null;

    public CalibrationStatus Calibration
    {
        get
        {
            var status=PhaseBinding(CurrentPhase)?.Pipeline.Calibration??CalibrationStatus.Off();
            string? warning=Volatile.Read(ref _calibrationError)??Calibrations.Warning;
            return warning==null?status:status with{Notice=warning+"\n"+status.Notice};
        }
    }

    public RawConsentRequest? PendingRawConsent=>CurrentPhase is EnginePhase.AwaitingRawConsent consent
        ?new(consent.DecisionId,consent.Device,consent.Reason):null;
    public long RawDecisionId=>Interlocked.Read(ref _decision);
    public bool RawDecisionPending=>CurrentPhase is EnginePhase.AwaitingRawConsent;

    public AudioStatus Status=>ProjectStatus(CurrentPhase);

    public AudioSnapshot Latest
    {
        get
        {
            var snapshot=PhaseBinding(CurrentPhase)?.Pipeline.Latest??AudioSnapshot.Empty(Interlocked.Read(ref _epoch));
            double previous=Volatile.Read(ref _previousPeak);
            return snapshot.Valid && !double.IsNaN(previous)?snapshot with
                {HeldPeakDbfs=snapshot.HeldPeakDbfs is double current?Math.Max(current,previous):previous}:snapshot;
        }
    }

    public long WrittenFrames=>PhaseWriter(CurrentPhase)?.WrittenFrames??Interlocked.Read(ref _lastWritten);
    public long RecordingUnconfirmedDiscontinuities=>PhaseBinding(CurrentPhase)?.Pipeline.RecordingUnconfirmedDiscontinuities??0;
    public long RecordingClippedSamples=>(PhaseWriter(CurrentPhase) as CompressedAudioWriter)?.ClippedSamples??0;
    public int RecordingRate=>CurrentPhase is EnginePhase.RecordingSession session
        ?session.Binding.Capture.Info.Format.SampleRate
        :Volatile.Read(ref _lastRate);
    public TimeSpan RecordedTime=>RecordingRate>0?TimeSpan.FromSeconds(WrittenFrames/(double)RecordingRate):TimeSpan.Zero;
    public IReadOnlyList<AudioDevice> EnumerateDevices()=>_factory.Enumerate();

    public LiveMonitorStatus LiveMonitor=>_liveMonitor.Status;

    public void SetGain(double db)
    {
        if(!double.IsFinite(db))throw new ArgumentOutOfRangeException(nameof(db),"增益必须为有限数值。");
        Interlocked.Exchange(ref _gain,Math.Pow(10,Math.Clamp(db,-60,60)/20));
    }
    public Task SetLiveMonitorEnabledAsync(bool enabled)
    {
        if(enabled)ThrowIfDisposed();
        return _liveMonitor.SetEnabledAsync(enabled);
    }
    public void SetLiveMonitorGain(double db)=>_liveMonitor.SetGain(db);
    private void OnLiveMonitorFaulted(string reason)
    {
        Trace("Live monitor faulted: "+reason);
        try{LiveMonitorFaulted?.Invoke(reason);}
        catch(Exception error){Trace("Live monitor fault callback failed: "+error);}
    }
    public Task<EndpointVolumeSnapshot> QueryEndpointVolumeAsync(AudioDevice device)=>_factory.QueryEndpointVolumeAsync(device);

    /// <summary>Release idle microphone capture while another work mode owns monitoring.</summary>
    public async Task SuspendMonitoringAsync()
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            var phase=CurrentPhase;
            if(phase is EnginePhase.RecordingSession)throw new InvalidOperationException("请先结束录制再切换工作模式。");
            var device=PhaseDevice(phase);
            bool normalConsent=PhaseNormalConsent(phase);
            try{await _liveMonitor.SetEnabledAsync(false);}
            catch(Exception error){Trace("Live monitor suspend failed: "+error);}
            try{await CloseCapture();}
            finally
            {
                Interlocked.Increment(ref _epoch);
                Interlocked.Exchange(ref _lastWritten,0);
                Volatile.Write(ref _lastRate,0);
                Volatile.Write(ref _previousPeak,double.NaN);
                SetPhase(new EnginePhase.Suspended(device,normalConsent));
            }
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
            var phase=CurrentPhase;
            var oldDevice=PhaseDevice(phase);
            var current=devices.FirstOrDefault(d=>d.Id==oldDevice?.Id);
            // Recording never changes source mid-file. A removed source is finalized, then monitoring recovers.
            if(phase is EnginePhase.RecordingSession && current!=null)return;
            RecordingOutcome? interrupted=null;
            if(phase is EnginePhase.RecordingSession)
            {
                try{await StopWriter(StopMode.FaultAbort,new IOException("录制输入设备已移除。"));}
                catch
                {
                    var interruptedStatus=Status;
                    if(interruptedStatus.FilePath!=null)interrupted=new(interruptedStatus.FilePath,interruptedStatus.Partial,interruptedStatus.Message);
                    else interrupted=new(null,true,interruptedStatus.Message);
                }
            }
            var chosen=(!manualRefresh?added.FirstOrDefault():null)??current??devices.FirstOrDefault(d=>d.IsDefault)??devices.FirstOrDefault();
            if(phase is EnginePhase.Suspended && !manualRefresh && interrupted==null)
            {
                bool suspendedConsent=ResolveDeviceConsent(oldDevice,PhaseNormalConsent(phase),chosen);
                SetPhase(new EnginePhase.Suspended(chosen,suspendedConsent));
                return;
            }
            if(chosen?.Id==oldDevice?.Id && phase is EnginePhase.Monitoring && !manualRefresh && interrupted==null)return;
            await CloseForDeviceHandoff(chosen);
            bool normalConsent=ResolveDeviceConsent(oldDevice,PhaseNormalConsent(phase),chosen);
            if(chosen==null)
            {
                string message="没有可用输入设备；插入设备后将自动恢复监看。";
                if(interrupted!=null)message=interrupted.Message+"；"+message;
                var outcome=interrupted is {FilePath.Length:>0}?interrupted with{Message=message}:null;
                SetPhase(new EnginePhase.Stopped(null,false,message,outcome));
                return;
            }
            var monitorOutcome=interrupted is {FilePath.Length:>0}?interrupted:null;
            await OpenMonitor(chosen,normalConsent,monitorOutcome);
            if(interrupted!=null)ApplyInterruptionBanner(interrupted.Message);
        }
        finally{_commands.Release();}
    }

    private void ApplyInterruptionBanner(string prefixMessage)
    {
        switch(CurrentPhase)
        {
            case EnginePhase.Monitoring monitoring:
                SetPhase(monitoring with{BaseMessage=prefixMessage+"；已恢复共享监看。"});
                break;
            case EnginePhase.Faulted faulted:
                SetPhase(faulted with{Message=prefixMessage+"；恢复监看失败："+faulted.Message});
                break;
        }
    }

    public async Task SelectDeviceAsync(AudioDevice device)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            var phase=CurrentPhase;
            if(phase is EnginePhase.RecordingSession)throw new InvalidOperationException("录制期间不能更换输入设备。");
            var oldDevice=PhaseDevice(phase);
            bool oldConsent=PhaseNormalConsent(phase);
            await CloseForDeviceHandoff(oldDevice);
            bool normalConsent=ResolveDeviceConsent(oldDevice,oldConsent,device);
            await OpenMonitor(device,normalConsent);
        }
        finally{_commands.Release();}
    }

    private async Task CloseForDeviceHandoff(AudioDevice? selectedOnFailure)
    {
        var phase=CurrentPhase;
        var previous=ProjectStatus(phase);
        var oldDevice=PhaseDevice(phase);
        bool oldConsent=PhaseNormalConsent(phase);
        try{await CloseCapture();}
        catch(Exception error)
        {
            bool nextConsent=ResolveDeviceConsent(oldDevice,oldConsent,selectedOnFailure);
            string message="关闭原输入采集失败："+Friendly(error);
            if(previous.FilePath!=null)message=previous.Message+"；"+message;
            RecordingOutcome? outcome=previous.FilePath!=null?new(previous.FilePath,previous.Partial,message):null;
            SetPhase(new EnginePhase.Faulted(selectedOnFailure,nextConsent,message,null,outcome));
            throw;
        }
    }

    private bool ResolveDeviceConsent(AudioDevice? oldDevice,bool oldConsent,AudioDevice? nextDevice)
    {
        if(oldDevice?.Id==nextDevice?.Id)return oldConsent;
        Volatile.Write(ref _calibrationError,null);
        Volatile.Write(ref _previousPeak,double.NaN);
        Interlocked.Exchange(ref _lastWritten,0);
        Volatile.Write(ref _lastRate,0);
        return false;
    }

    public async Task<bool> DecideRawFallbackAsync(long decisionId,bool allow)
    {
        await _commands.WaitAsync();
        try
        {
            if(CurrentPhase is not EnginePhase.AwaitingRawConsent consent||consent.DecisionId!=decisionId)return false;
            if(allow)await OpenMonitor(consent.Device,normalConsent:true,consent.LastOutcome,includeBannerWithOutcome:true);
            else
            {
                string message=consent.LastOutcome is { } saved
                    ?$"{saved.Message}；监看已停止（未获 RAW 输入）"
                    :"监看已停止；尚未获得 RAW 输入。";
                SetPhase(new EnginePhase.Stopped(consent.Device,false,message,consent.LastOutcome));
            }
            return true;
        }
        finally{_commands.Release();}
    }

    private string MonitorBanner(bool normalConsent)
    {
        string message=normalConsent?"共享监看 · 普通输入（仅本次许可）":"共享监看 · RAW 请求已接受";
        string? warning=Volatile.Read(ref _calibrationError)??Calibrations.Warning;
        return warning==null?message:message+"；"+warning;
    }

    private async Task OpenMonitor(AudioDevice device,bool normalConsent,RecordingOutcome? outcome=null,bool includeBannerWithOutcome=false)
    {
        if(CurrentPhase is EnginePhase.Disposed)return;
        try
        {
            var binding=await OpenCapture(device,exclusive:false,raw:!normalConsent,normalConsent,outcome);
            string baseMessage=outcome==null
                ?MonitorBanner(normalConsent)
                :includeBannerWithOutcome?$"{outcome.Message}；{MonitorBanner(normalConsent)}":outcome.Message;
            SetPhase(new EnginePhase.Monitoring(binding,baseMessage,outcome));
        }
        catch(RawUnavailableException error)
        {
            long decisionId=Interlocked.Increment(ref _decision);
            SetPhase(new EnginePhase.AwaitingRawConsent(device,decisionId,error.Message,outcome));
        }
        catch(Exception error)
        {
            SetPhase(new EnginePhase.Faulted(device,normalConsent,Friendly(error),null,outcome));
        }
    }

    private async Task<CaptureBinding> OpenCapture(AudioDevice device,bool exclusive,bool raw,bool normalConsent,RecordingOutcome? outcome=null)
    {
        SetPhase(new EnginePhase.Opening(device,normalConsent,outcome));
        long epoch=Interlocked.Increment(ref _epoch);
        Exception? openingFault=null;
        void PipelineFault(Exception error)
        {
            if(Interlocked.CompareExchange(ref openingFault,error,null)==null)ReportFault(epoch,error);
        }
        var nativeFault=new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipe=new ProcessingPipeline(()=>Volatile.Read(ref _gain),PipelineFault,Trace,_liveMonitor.Feed);
        void NativeFault(Exception error)
        {
            if(pipe.Failure is Exception processingError){PipelineFault(processingError);return;}
            if(Interlocked.CompareExchange(ref openingFault,error,null)!=null)return;
            nativeFault.TrySetResult(error);
            ReportFault(epoch,error);
        }
        try
        {
            var preferred=exclusive?InputFormats.Get(device.Id):null;
            var capture=await _factory.OpenAsync(device,exclusive,raw,epoch,pipe.Submit,NativeFault,preferred);
            try
            {
                if(preferred!=null&&capture.Info.Format!=preferred)throw new NotSupportedException("采集实际格式与所选独占格式不一致，已拒绝录制。");
                var plan=CalibrationPlan.Identity;string? calibrationError=null;
                var selection=Calibrations.GetSelection(device.Id);
                string? id=selection.Enabled?selection.ProfileId:null;
                if(selection.Enabled&&id==null)calibrationError="校准尚未生效：请选择校准文件，或禁用校准后录制。";
                if(id!=null)
                {
                    try{plan=await Task.Run(()=>CalibrationPlan.Prepare(Calibrations.Read(id),capture.Info.Format.SampleRate));}
                    catch(Exception e){calibrationError="所选校准不可用，已停用："+e.Message;Trace(calibrationError);}
                }
                Volatile.Write(ref _calibrationError,calibrationError);
                if(Volatile.Read(ref openingFault) is Exception earlyError)throw new IOException("准备校准时采集已停止："+earlyError.Message,earlyError);
                pipe.Configure(capture.Info,plan);await pipe.Ready;
                if(Volatile.Read(ref openingFault) is Exception initializationError)throw new IOException("初始化校准处理失败："+initializationError.Message,initializationError);
                if(Volatile.Read(ref openingFault) is Exception racedError)ReportFault(epoch,racedError);
            }
            catch(Exception openingError)
            {
                try{await capture.DisposeAsync();}
                catch(Exception cleanupError){throw AudioFailure.Combine(openingError,cleanupError);}
                throw;
            }
            Trace($"Capture ready: device={device.Name}; {capture.Info.Format.Description}; exclusive={exclusive}; RAW={raw}; epoch={epoch}");
            if(capture.Info.StreamDiagnostics is string diagnostics)Trace($"Capture stream: epoch={epoch}; {diagnostics}");
            return new(device,capture,pipe,nativeFault.Task,normalConsent);
        }
        catch(Exception openingError)
        {
            try{await pipe.DisposeAsync();}
            catch(Exception cleanupError){throw AudioFailure.Combine(openingError,cleanupError);}
            throw;
        }
    }

    private async Task CloseCapture(bool preservePeak=true)
    {
        if(!preservePeak)Volatile.Write(ref _previousPeak,double.NaN);
        else if(Latest.HeldPeakDbfs is double held)Volatile.Write(ref _previousPeak,held);
        Interlocked.Increment(ref _epoch);
        // Detach ownership first: a failing native release cannot leave a valid old snapshot
        // or prevent the analysis worker from being stopped.
        var phase=CurrentPhase;
        var binding=PhaseBinding(phase);
        if(binding==null)return;
        switch(phase)
        {
            case EnginePhase.Monitoring m:
                SetPhase(new EnginePhase.Stopped(m.Binding.Device,m.Binding.NormalConsent,m.BaseMessage,m.LastOutcome));
                break;
            case EnginePhase.Faulted f:
                SetPhase(f with{ActiveBinding=null});
                break;
        }
        Exception? failure=null;
        try{await binding.Capture.DisposeAsync();}catch(Exception error){failure=error;}
        try{await binding.Pipeline.DisposeAsync();}catch(Exception error){failure=AudioFailure.Combine(failure,error);}
        if(failure!=null)
        {
            Trace("Capture cleanup failed: "+failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public static string DefaultRecordingFolder=>RecordingStore.DefaultFolder;

    public async Task StartRecordingAsync(string directory,RecordingMode mode,RecordingEncoding? encoding=null)
    {
        await _commands.WaitAsync();
        CaptureBinding? binding=null;
        IRecordingWriter? writer=null;
        AudioDevice? device=null;
        bool normalConsent=false;
        bool preparationStarted=false;
        try
        {
            ThrowIfDisposed();
            var phase=CurrentPhase;
            if(phase is EnginePhase.RecordingSession)throw new InvalidOperationException("已经在录制。");
            device=PhaseDevice(phase)??throw new InvalidOperationException("没有选中的输入设备。");
            if(!Enum.IsDefined(mode))throw new ArgumentOutOfRangeException(nameof(mode));
            preparationStarted=true;
            normalConsent=PhaseNormalConsent(phase);
            bool exclusive=mode==RecordingMode.Exclusive;
            binding=phase is EnginePhase.Monitoring m?m.Binding:null;
            if(binding==null||exclusive||!binding.Capture.Info.Raw)
            {
                binding=null;
                await CloseCapture();
                try{binding=await OpenCapture(device,exclusive,raw:true,normalConsent);}
                catch(Exception error){await OpenMonitor(device,normalConsent);throw new IOException("录制输入打开失败："+Friendly(error),error);}
                SetPhase(new EnginePhase.Monitoring(binding,MonitorBanner(normalConsent)));
            }
            if(Volatile.Read(ref _calibrationError) is string calError)throw new InvalidOperationException(calError+" 请重新选择校准或明确关闭校准后录制。");
            if(!binding.Capture.Info.Raw)throw new InvalidOperationException("正式录制必须接受 RAW 请求。");
            var calibration=Calibration;
            var summaryProfile=calibration.ProfileId==null?null:await Task.Run(()=>Calibrations.Read(calibration.ProfileId));
            encoding??=new();encoding.Validate(binding.Capture.Info.Format);
            SetPhase(new EnginePhase.Monitoring(binding,"正在准备录音文件"));
            writer=_recordings.CreateWriter(directory,device,binding.Capture.Info.Format,encoding,calibration,summaryProfile,_createWriter);
            Interlocked.Exchange(ref _lastWritten,0);
            Volatile.Write(ref _lastRate,binding.Capture.Info.Format.SampleRate);
            SetPhase(new EnginePhase.RecordingSession(binding,writer,new RecordingSubPhase.Preparing(writer.Path,"正在准备录音文件")));
            await binding.Pipeline.AttachAsync(writer);
            Volatile.Write(ref _previousPeak,double.NaN);
            SetPhase(new EnginePhase.RecordingSession(binding,writer,new RecordingSubPhase.Preparing(writer.Path,"等待首批录音样本")));
        }
        catch(Exception preparationError) when(preparationStarted)
        {
            Exception failure=preparationError;
            if(writer!=null)
            {
                try{await writer.CompleteAsync();}
                catch(Exception error){failure=AudioFailure.Combine(failure,error);}
            }
            // If preparation failed after exclusive capture opened, release it before restoring shared monitoring.
            if(binding?.Capture.Info.Exclusive==true)
            {
                try{await CloseCapture();}catch(Exception error){failure=AudioFailure.Combine(failure,error);}
                await OpenMonitor(device!,normalConsent);
            }
            else if(binding!=null)SetPhase(new EnginePhase.Monitoring(binding,MonitorBanner(binding.NormalConsent)));
            Trace("Recording preparation failed: "+failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally{_commands.Release();}
    }

    public async Task RequestPauseAsync()=>_ = await BeginPauseAsync();

    private async Task<Task<Exception?>> BeginPauseAsync()
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if(CurrentPhase is not EnginePhase.RecordingSession session||session.SubPhase is RecordingSubPhase.Paused)
                return Task.FromResult<Exception?>(null);
            if(session.SubPhase is RecordingSubPhase.Pausing existing)return existing.Completion.Task;
            var capture=session.Binding.Capture as IPauseTailCapture;
            capture?.SetPauseTailPending(true);
            Task<Exception?> boundary;
            try{boundary=await session.Binding.Pipeline.RequestPauseAsync();}
            catch{capture?.SetPauseTailPending(false);throw;}
            if(boundary.IsCompletedSuccessfully&&boundary.Result==null&&session.Binding.Pipeline.Failure==null)
            {
                capture?.SetPauseTailPending(false);
                SetPhase(session with{SubPhase=new RecordingSubPhase.Paused(session.Writer.Path)});
                return boundary;
            }
            var completion=new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            SetPhase(session with{SubPhase=new RecordingSubPhase.Pausing(session.Writer.Path,completion,Delayed:false)});
            _=ObservePauseAsync(boundary,completion);
            return completion.Task;
        }
        finally{_commands.Release();}
    }

    private async Task ObservePauseAsync(Task<Exception?> boundary,TaskCompletionSource<Exception?> completion)
    {
        Exception? error;
        try{error=await boundary.WaitAsync(ProcessingPipeline.BoundaryWait);}
        catch(TimeoutException)
        {
            await _commands.WaitAsync();
            try
            {
                if(CurrentPhase is EnginePhase.RecordingSession session&&
                   session.SubPhase is RecordingSubPhase.Pausing pausing&&
                   ReferenceEquals(pausing.Completion,completion)&&!boundary.IsCompleted)
                {
                    SetPhase(session with{SubPhase=pausing with{Delayed=true}});
                }
            }
            finally{_commands.Release();}
            error=await boundary;
        }
        await _commands.WaitAsync();
        try
        {
            if(CurrentPhase is EnginePhase.RecordingSession session&&
               session.SubPhase is RecordingSubPhase.Pausing pausing&&
               ReferenceEquals(pausing.Completion,completion))
            {
                (session.Binding.Capture as IPauseTailCapture)?.SetPauseTailPending(false);
                error??=session.Binding.Pipeline.Failure;
                if(error==null)SetPhase(session with{SubPhase=new RecordingSubPhase.Paused(session.Writer.Path)});
            }
        }
        finally{_commands.Release();}
        completion.TrySetResult(error);
    }

    public async Task PauseAsync(bool pause)
    {
        if(pause)
        {
            var completion=await BeginPauseAsync();
            if(await completion is Exception error)ExceptionDispatchInfo.Capture(error).Throw();
            return;
        }
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if(CurrentPhase is not EnginePhase.RecordingSession{SubPhase:RecordingSubPhase.Paused} session)return;
            await session.Binding.Pipeline.ResumeAsync();
            SetPhase(session with{SubPhase=new RecordingSubPhase.Active(session.Writer.Path)});
        }
        finally{_commands.Release();}
    }

    public async Task StopRecordingAsync()
    {
        await _commands.WaitAsync();
        try{await StopWriter(StopMode.StopAndRestoreMonitor);}
        finally{_commands.Release();}
    }

    public async Task DiscardRecordingAsync()
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if(CurrentPhase is not EnginePhase.RecordingSession{SubPhase:RecordingSubPhase.Paused} session)
                throw new InvalidOperationException("请先暂停，再丢弃本次录音。");
            var writer=session.Writer;
            var binding=session.Binding;
            string path=writer.Path;var auxiliary=writer.AuxiliaryPaths;bool exclusive=binding.Capture.Info.Exclusive;
            // Close/drain the owned file before deleting; this cannot touch earlier recordings.
            try{await StopWriter(StopMode.DiscardAndRestoreMonitor);}
            catch(Exception finalizationError)
            {
                // An explicitly discarded partial file may still be removed, but its
                // finalization failure remains available for diagnostics.
                Trace("Discarded recording could not be finalized: "+finalizationError);
            }
            Exception? failure=null;
            var ownedPaths=new List<string>{path};
            ownedPaths.AddRange(auxiliary);
            if(_recordings.SummaryOwned is string summaryOwned)ownedPaths.Add(summaryOwned);
            foreach(string owned in ownedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(owned);
                    _recordings.ClearOwnedSummaryIfMatch(owned);
                }
                catch(Exception error){failure=AudioFailure.Combine(failure,error);}
            }
            _lastWritten=0;
            if(exclusive)
            {
                try{await CloseCapture();}catch(Exception error){failure=AudioFailure.Combine(failure,error);}
                await OpenMonitor(binding.Device,binding.NormalConsent);
            }
            if(failure!=null)
            {
                SetPhase(new EnginePhase.Faulted(binding.Device,binding.NormalConsent,"无法丢弃文件："+Friendly(failure),
                    CurrentPhase is EnginePhase.Monitoring m?m.Binding:null,new(path,true,"")));
                throw new IOException("文件已关闭但未能丢弃："+path,failure);
            }
            if(CurrentPhase is EnginePhase.Monitoring monitoring)
                SetPhase(monitoring with{BaseMessage="已丢弃本次录音",LastOutcome=null});
        }
        finally{_commands.Release();}
    }

    private async Task StopWriter(StopMode mode,Exception? interruption=null)
    {
        if(CurrentPhase is not EnginePhase.RecordingSession session)return;
        var binding=session.Binding;
        var writer=session.Writer;
        var pauseCompletion=(session.SubPhase as RecordingSubPhase.Pausing)?.Completion;
        if(interruption!=null)pauseCompletion?.TrySetResult(interruption);
        SetPhase(session with{SubPhase=new RecordingSubPhase.Finalizing(writer.Path)});
        bool restoreMonitor=mode==StopMode.StopAndRestoreMonitor;
        Exception? failure=interruption;
        try{await binding.Pipeline.DetachAsync(mathematicalDrain:!restoreMonitor,resetPeak:restoreMonitor,binding.NativeFault);}
        catch(Exception error){failure=AudioFailure.Combine(failure,error);}
        finally{(binding.Capture as IPauseTailCapture)?.SetPauseTailPending(false);}
        try{await writer.CompleteAsync();}
        catch(Exception error){failure=AudioFailure.Combine(failure,error);}
        // Native interruption up to writer completion belongs to the recording.
        // Later failures belong to monitoring, even while persisting the recording summary.
        var nativeError=binding.NativeFault is {IsCompletedSuccessfully:true}?binding.NativeFault.Result:null;
        if(nativeError!=null)
        {
            if(!ReferenceEquals(interruption,nativeError))failure=failure==null?nativeError:AudioFailure.Combine(nativeError,failure);
            pauseCompletion?.TrySetResult(nativeError);
        }
        long unconfirmedDiscontinuities=binding.Pipeline.RecordingUnconfirmedDiscontinuities;
        Interlocked.Exchange(ref _lastWritten,writer.WrittenFrames);

        if(mode==StopMode.DiscardAndRestoreMonitor)
        {
            SetPhase(new EnginePhase.Monitoring(binding,"正在丢弃录音"));
            if(failure!=null)ExceptionDispatchInfo.Capture(failure).Throw();
            return;
        }

        string? summaryError=await Task.Run(()=>_recordings.SaveCalibrationSummary(writer,failure,unconfirmedDiscontinuities));
        bool exclusive=binding.Capture.Info.Exclusive;
        if(failure!=null)
        {
            bool captureGap=failure is CaptureGapException||binding.Pipeline.Failure is CaptureGapException;
            if(restoreMonitor&&captureGap)
            {
                try{await CloseCapture(preservePeak:false);}
                catch(Exception cleanupError){failure=AudioFailure.Combine(failure,cleanupError);}
                Trace("Recording finalization failed with capture gap: "+failure);
                await RecoverAfterCaptureGapAsync(binding.Device,binding.NormalConsent,writer.Path,Friendly(failure));
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            if(restoreMonitor&&(exclusive||nativeError!=null))
            {
                try{await CloseCapture(preservePeak:false);}
                catch(Exception cleanupError){failure=AudioFailure.Combine(failure,cleanupError);}
            }
            Trace("Recording finalization failed: "+failure);
            CaptureBinding? retained=!restoreMonitor||(!exclusive&&nativeError==null)?binding:null;
            SetPhase(new EnginePhase.Faulted(binding.Device,binding.NormalConsent,
                "录制结束异常："+Friendly(failure)+(summaryError==null?"":"；校准摘要写入失败："+summaryError),
                retained,new(writer.Path,true,"录制结束异常："+Friendly(failure),binding.Capture.Info.Epoch)));
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        string savedMessage="已保存："+System.IO.Path.GetFileName(writer.Path);
        if(writer is CompressedAudioWriter compressed&&compressed.ClippedSamples>0)
            savedMessage+=$"；压缩文件有 {compressed.ClippedSamples} 个超限样本发生削波，录音未中断。使用浮点 WAV 可保留超限幅值。";
        if(summaryError!=null)savedMessage+="；音频已保存，但校准摘要写入失败："+summaryError;
        var outcome=new RecordingOutcome(writer.Path,false,savedMessage,binding.Capture.Info.Epoch);

        if(mode==StopMode.Shutdown)
        {
            SetPhase(new EnginePhase.Monitoring(binding,savedMessage,outcome));
            return;
        }

        if(exclusive&&restoreMonitor)
        {
            Exception? monitorFailure=null;
            try{await CloseCapture(preservePeak:false);}catch(Exception error){monitorFailure=error;}
            if(binding.NativeFault is {IsCompletedSuccessfully:true} lateNative)
                monitorFailure=monitorFailure==null?lateNative.Result:AudioFailure.Combine(lateNative.Result,monitorFailure);
            await OpenMonitor(binding.Device,binding.NormalConsent,outcome);
            switch(CurrentPhase)
            {
                case EnginePhase.Faulted faulted:
                    monitorFailure=AudioFailure.Combine(monitorFailure,new IOException(faulted.Message));
                    break;
                case EnginePhase.Monitoring monitor when monitorFailure!=null:
                    Trace("Recording saved, but monitoring recovery failed: "+monitorFailure);
                    SetPhase(new EnginePhase.Faulted(binding.Device,binding.NormalConsent,
                        savedMessage+"；恢复监看失败："+Friendly(monitorFailure),monitor.Binding,outcome));
                    throw new IOException("音频已保存，但恢复监看失败："+Friendly(monitorFailure),monitorFailure);
                case EnginePhase.AwaitingRawConsent when monitorFailure==null:
                    return;
            }
            if(monitorFailure!=null)
            {
                Trace("Recording saved, but monitoring recovery failed: "+monitorFailure);
                SetPhase(new EnginePhase.Faulted(binding.Device,binding.NormalConsent,
                    savedMessage+"；恢复监看失败："+Friendly(monitorFailure),null,outcome));
                throw new IOException("音频已保存，但恢复监看失败："+Friendly(monitorFailure),monitorFailure);
            }
            return;
        }

        SetPhase(new EnginePhase.Monitoring(binding,savedMessage,outcome));
    }

    public string GetRecordingMetadataPath(string recordingPath)=>_recordings.GetMetadataPath(recordingPath);

    private async Task RecoverAfterCaptureGapAsync(AudioDevice device,bool normalConsent,string? filePath,string prefixMessage)
    {
        var gapOutcome=new RecordingOutcome(filePath,true,prefixMessage);
        await OpenMonitor(device,normalConsent,gapOutcome);
        ApplyInterruptionBanner(prefixMessage);
    }

    public async Task SetCalibrationAsync(string? id)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            var phase=CurrentPhase;
            if(phase is EnginePhase.RecordingSession)throw new InvalidOperationException("录制和暂停期间不能更换校准。");
            var device=PhaseDevice(phase)??throw new InvalidOperationException("尚未选择输入设备。");
            var old=CalibrationSelection;
            await ChangeCalibration(device,new(id!=null,id??old.ProfileId),false);
        }
        finally{_commands.Release();}
    }

    public async Task SetCalibrationEnabledAsync(bool enabled)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            var phase=CurrentPhase;
            if(phase is EnginePhase.RecordingSession)throw new InvalidOperationException("录制和暂停期间不能更换校准。");
            var device=PhaseDevice(phase)??throw new InvalidOperationException("尚未选择输入设备。");
            await ChangeCalibration(device,CalibrationSelection with{Enabled=enabled},true);
        }
        finally{_commands.Release();}
    }

    private async Task ChangeCalibration(AudioDevice device,CalibrationLibrary.Selection choice,bool allowUnavailable)
    {
        var binding=PhaseBinding(CurrentPhase);
        var plan=CalibrationPlan.Identity;string? error=null;
        if(choice.Enabled)
        {
            if(choice.ProfileId==null)error="校准尚未生效：请选择校准文件，或禁用校准后录制。";
            else try{plan=await Task.Run(()=>CalibrationPlan.Prepare(Calibrations.Read(choice.ProfileId),binding?.Capture.Info.Format.SampleRate??48000));}
            catch(Exception e) when(allowUnavailable){error="所选校准不可用，未应用："+e.Message;}
        }
        await PersistCalibrationChange(device,plan,()=>Calibrations.Configure(device.Id,choice),error);
        if(CurrentPhase is EnginePhase.Monitoring monitoring)
        {
            string message=error??(choice.Enabled?$"已启用频响校准：{plan.Profile!.Name}":"校准已禁用；共享监看");
            SetPhase(monitoring with{BaseMessage=message,LastOutcome=null});
        }
    }

    private async Task PersistCalibrationChange(AudioDevice device,CalibrationPlan plan,Action persist,string? calibrationError)
    {
        var phase=CurrentPhase;
        var binding=PhaseBinding(phase);
        var pipeline=binding?.Pipeline;
        var nativeFault=binding?.NativeFault;
        bool saved=false;
        void Save()
        {
            if(nativeFault is {IsCompletedSuccessfully:true})throw nativeFault.Result;
            persist();
            saved=true;
        }
        try
        {
            if(pipeline==null)await Task.Run(Save);
            else await pipeline.ChangeCalibrationAsync(plan,Save);
            if(nativeFault is {IsCompletedSuccessfully:true})throw nativeFault.Result;
            if(pipeline?.Failure is Exception processingFailure)throw processingFailure;
        }
        catch(Exception error) when(saved)
        {
            // A failed capture has no live calibration to restore. Reopening uses the saved catalog.
            var cause=nativeFault is {IsCompletedSuccessfully:true}?AudioFailure.Combine(nativeFault.Result,error):error;
            Exception failure=new IOException("校准更改已保存，但监看未能应用。",cause);
            try{await CloseCapture(preservePeak:false);}
            catch(Exception cleanupError){failure=AudioFailure.Combine(failure,cleanupError);}
            Trace("Capture fault after saving calibration: "+failure);
            SetPhase(new EnginePhase.Faulted(device,PhaseNormalConsent(phase),Friendly(failure)));
            throw failure;
        }
        finally
        {
            if(saved)
            {
                Volatile.Write(ref _calibrationError,calibrationError);
                Volatile.Write(ref _previousPeak,double.NaN);
            }
        }
    }

    public async Task<InputFormatOptions> QueryInputFormatsAsync()
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            var device=SelectedDevice??throw new InvalidOperationException("尚未选择输入设备。");
            return await _factory.QueryFormatsAsync(device);
        }
        finally{_commands.Release();}
    }

    public async Task SetInputFormatAsync(AudioFormat? format)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            var phase=CurrentPhase;
            if(phase is EnginePhase.RecordingSession)throw new InvalidOperationException("录制和暂停期间不能更换输入格式。");
            var device=PhaseDevice(phase)??throw new InvalidOperationException("尚未选择输入设备。");
            if(format!=null&&!(await _factory.QueryFormatsAsync(device)).Exclusive.Contains(format))
                throw new NotSupportedException("设备当前不支持此独占输入格式。");
            InputFormats.Set(device.Id,format);
        }
        finally{_commands.Release();}
    }

    public async Task<CalibrationLibrary.Entry> ImportCalibrationAsync(string path)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if(CurrentPhase is EnginePhase.RecordingSession)throw new InvalidOperationException("录制和暂停期间不能导入校准。");
            return await Task.Run(()=>Calibrations.Import(path));
        }
        finally{_commands.Release();}
    }

    public async Task RemoveCalibrationAsync(string id)
    {
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            var phase=CurrentPhase;
            if(phase is EnginePhase.RecordingSession)throw new InvalidOperationException("录制和暂停期间不能移除校准。");
            var device=PhaseDevice(phase);
            bool active=device!=null&&Calibrations.SelectedId(device.Id)==id;
            if(active)
            {
                await PersistCalibrationChange(device!,CalibrationPlan.Identity,()=>Calibrations.Remove(id),null);
                if(CurrentPhase is EnginePhase.Monitoring monitoring)
                    SetPhase(monitoring with{BaseMessage="已移除当前校准；共享监看未应用校准。",LastOutcome=null});
            }
            else await Task.Run(()=>Calibrations.Remove(id));
        }
        finally{_commands.Release();}
    }

    public async Task ResetPeakAsync()
    {
        await _commands.WaitAsync();
        try
        {
            if(PhaseBinding(CurrentPhase)?.Pipeline is { } pipeline)await pipeline.ResetPeakAsync();
            Volatile.Write(ref _previousPeak,double.NaN);
        }
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
                var phase=CurrentPhase;
                if(phase is EnginePhase.Disposed)return;
                var binding=PhaseBinding(phase);
                if(binding==null||epoch!=binding.Capture.Info.Epoch)return;
                var first=binding.NativeFault is {IsCompletedSuccessfully:true} nativeFault
                    ?nativeFault.Result
                    :binding.Pipeline.Failure??error;
                var previousStatus=ProjectStatus(phase);
                RecordingOutcome? preserved=phase switch
                {
                    EnginePhase.RecordingSession r=>new(r.Writer.Path,true,""),
                    EnginePhase.Monitoring m when m.LastOutcome?.OwnerEpoch==epoch=>m.LastOutcome,
                    EnginePhase.Faulted f when f.LastOutcome?.OwnerEpoch==epoch=>f.LastOutcome,
                    _=>null
                };
                bool partial=phase is EnginePhase.RecordingSession||(preserved?.Partial??false);
                bool finalizedRecording=preserved!=null&&phase is not EnginePhase.RecordingSession;
                Exception failure=first;
                try{await StopWriter(StopMode.FaultAbort,first);}
                catch(Exception finalizationError){failure=finalizationError;}
                try{await CloseCapture();}
                catch(Exception cleanupError){failure=AudioFailure.Combine(failure,cleanupError);}
                Trace("Capture fault: "+failure);
                if(partial&&first is CaptureGapException&&preserved!=null)
                    await RecoverAfterCaptureGapAsync(binding.Device,binding.NormalConsent,preserved.FilePath,Friendly(failure));
                else
                {
                    string message=finalizedRecording?previousStatus.Message+"；监看失败："+Friendly(failure):Friendly(failure);
                    SetPhase(new EnginePhase.Faulted(binding.Device,binding.NormalConsent,message,null,preserved));
                }
            }
            catch(Exception unexpected)
            {
                Trace("Fault handling failed: "+unexpected);
                var status=Status;
                var phase=CurrentPhase;
                RecordingOutcome? fallback=status.FilePath!=null?new(status.FilePath,status.Partial,""):null;
                SetPhase(new EnginePhase.Faulted(PhaseDevice(phase),PhaseNormalConsent(phase),"音频故障收尾失败："+Friendly(unexpected),null,fallback));
            }
            finally{_commands.Release();}
        });
    }

    private void ThrowIfDisposed(){ObjectDisposedException.ThrowIf(CurrentPhase is EnginePhase.Disposed,this);}
    public static string Friendly(Exception error)=>AudioFailure.Describe(error);

    public async ValueTask DisposeAsync()
    {
        await _commands.WaitAsync();
        try
        {
            var phase=CurrentPhase;
            if(phase is EnginePhase.Disposed)return;
            Exception? failure=null;
            string? file=PhaseWriter(phase)?.Path;
            bool partial=false;
            try{await StopWriter(StopMode.Shutdown);}
            catch(Exception error){failure=error;partial=true;}
            try{await CloseCapture();}
            catch(Exception error){failure=AudioFailure.Combine(failure,error);}
            _liveMonitor.Faulted-=OnLiveMonitorFaulted;
            try{await _liveMonitor.DisposeAsync();}
            catch(Exception error){Trace("Live monitor shutdown failed: "+error);}
            if(failure!=null)
            {
                Trace("Audio service shutdown failed: "+failure);
                var status=new AudioStatus(AudioRunState.Faulted,null,"关闭音频服务失败："+Friendly(failure),file,partial);
                SetPhase(new EnginePhase.Disposed(status.Message,status));
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            SetPhase(new EnginePhase.Disposed("已关闭音频服务"));
        }
        finally{_commands.Release();}
    }
}
