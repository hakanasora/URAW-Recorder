namespace PortableRecorder.Audio;

public sealed partial class AudioEngine
{
    private sealed record CaptureBinding(AudioDevice Device,IAudioCaptureService Capture,ProcessingPipeline Pipeline,Task<Exception> NativeFault,bool NormalConsent);
    private sealed record RecordingOutcome(string? FilePath,bool Partial,string Message,long? OwnerEpoch=null);

    private abstract record EnginePhase
    {
        private EnginePhase(){}
        public sealed record Stopped(AudioDevice? Device,bool NormalConsent,string Message,RecordingOutcome? LastOutcome=null):EnginePhase;
        public sealed record Suspended(AudioDevice? Device,bool NormalConsent):EnginePhase;
        public sealed record Opening(AudioDevice Device,bool NormalConsent,RecordingOutcome? LastOutcome=null):EnginePhase;
        public sealed record AwaitingRawConsent(AudioDevice Device,long DecisionId,string Reason,RecordingOutcome? LastOutcome=null):EnginePhase;
        public sealed record Monitoring(CaptureBinding Binding,string BaseMessage,RecordingOutcome? LastOutcome=null):EnginePhase;
        public sealed record RecordingSession(CaptureBinding Binding,IRecordingWriter Writer,RecordingSubPhase SubPhase):EnginePhase;
        public sealed record Faulted(AudioDevice? Device,bool NormalConsent,string Message,CaptureBinding? ActiveBinding=null,RecordingOutcome? LastOutcome=null):EnginePhase;
        public sealed record Disposed(string Message,AudioStatus? FinalStatus=null):EnginePhase;
    }

    private abstract record RecordingSubPhase
    {
        private RecordingSubPhase(){}
        public sealed record Preparing(string FilePath,string Message):RecordingSubPhase;
        public sealed record Active(string FilePath):RecordingSubPhase;
        public sealed record Pausing(string FilePath,TaskCompletionSource<Exception?> Completion,bool Delayed):RecordingSubPhase;
        public sealed record Paused(string FilePath):RecordingSubPhase;
        public sealed record Finalizing(string FilePath):RecordingSubPhase;
    }

    private enum StopMode { StopAndRestoreMonitor, DiscardAndRestoreMonitor, Shutdown, FaultAbort }

    private static AudioDevice? PhaseDevice(EnginePhase phase)=>phase switch
    {
        EnginePhase.Stopped s=>s.Device,
        EnginePhase.Suspended s=>s.Device,
        EnginePhase.Opening o=>o.Device,
        EnginePhase.AwaitingRawConsent a=>a.Device,
        EnginePhase.Monitoring m=>m.Binding.Device,
        EnginePhase.RecordingSession r=>r.Binding.Device,
        EnginePhase.Faulted f=>f.Device,
        _=>null
    };

    private static bool PhaseNormalConsent(EnginePhase phase)=>phase switch
    {
        EnginePhase.Stopped s=>s.NormalConsent,
        EnginePhase.Suspended s=>s.NormalConsent,
        EnginePhase.Opening o=>o.NormalConsent,
        EnginePhase.Monitoring m=>m.Binding.NormalConsent,
        EnginePhase.RecordingSession r=>r.Binding.NormalConsent,
        EnginePhase.Faulted f=>f.NormalConsent,
        _=>false
    };

    private static CaptureBinding? PhaseBinding(EnginePhase phase)=>phase switch
    {
        EnginePhase.Monitoring m=>m.Binding,
        EnginePhase.RecordingSession r=>r.Binding,
        EnginePhase.Faulted f=>f.ActiveBinding,
        _=>null
    };

    private static IRecordingWriter? PhaseWriter(EnginePhase phase)=>
        phase is EnginePhase.RecordingSession session?session.Writer:null;

    private static AudioStatus ProjectStatus(EnginePhase phase)=>phase switch
    {
        EnginePhase.Stopped s=>new(AudioRunState.Stopped,null,s.Message,s.LastOutcome?.FilePath,s.LastOutcome?.Partial??false),
        EnginePhase.Suspended=>new(AudioRunState.Stopped,null,"麦克风监看已暂停。"),
        EnginePhase.Opening o=>new(AudioRunState.Opening,null,"正在打开输入设备",o.LastOutcome?.FilePath,o.LastOutcome?.Partial??false),
        EnginePhase.AwaitingRawConsent a=>new(
            AudioRunState.Stopped,
            null,
            a.LastOutcome is { } saved?$"{saved.Message}；尚未获得共享 RAW 输入，请确认是否允许普通共享监看":a.Reason,
            a.LastOutcome?.FilePath,
            a.LastOutcome?.Partial??false),
        EnginePhase.Monitoring m=>ProjectMonitoring(m),
        EnginePhase.RecordingSession r=>ProjectRecording(r),
        EnginePhase.Faulted f=>new(AudioRunState.Faulted,null,f.Message,f.LastOutcome?.FilePath,f.LastOutcome?.Partial??false),
        EnginePhase.Disposed d=>d.FinalStatus??new(AudioRunState.Stopped,null,d.Message),
        _=>new(AudioRunState.Stopped,null,"")
    };

    private static AudioStatus ProjectMonitoring(EnginePhase.Monitoring monitoring)
    {
        int recovered=monitoring.Binding.Pipeline.RecoveredDiscontinuities;
        string message=recovered>0?monitoring.BaseMessage+$"；已从 {recovered} 次短暂数据不连续恢复监看。":monitoring.BaseMessage;
        return new(AudioRunState.Monitoring,monitoring.Binding.Capture.Info,message,monitoring.LastOutcome?.FilePath,monitoring.LastOutcome?.Partial??false);
    }

    private static AudioStatus ProjectRecording(EnginePhase.RecordingSession session)=>session.SubPhase switch
    {
        RecordingSubPhase.Preparing p=>session.Writer.WrittenFrames>0
            ?new(AudioRunState.Recording,session.Binding.Capture.Info,"录制中",session.Writer.Path)
            :new(AudioRunState.Preparing,session.Binding.Capture.Info,p.Message,session.Writer.Path),
        RecordingSubPhase.Active a=>new(AudioRunState.Recording,session.Binding.Capture.Info,"录制中",a.FilePath),
        RecordingSubPhase.Pausing p=>new(AudioRunState.Pausing,session.Binding.Capture.Info,
            p.Delayed?"仍在等待输入以完成暂停，可停止录制。":"正在暂停",p.FilePath){PauseTailDelayed=p.Delayed},
        RecordingSubPhase.Paused p=>new(AudioRunState.Paused,session.Binding.Capture.Info,"暂停中",p.FilePath),
        RecordingSubPhase.Finalizing f=>new(AudioRunState.Finalizing,session.Binding.Capture.Info,"正在完成录音文件",f.FilePath),
        _=>new(AudioRunState.Recording,session.Binding.Capture.Info,"录制中",session.Writer.Path)
    };
}
