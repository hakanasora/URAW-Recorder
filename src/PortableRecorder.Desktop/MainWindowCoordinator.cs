using PortableRecorder.Audio;

namespace PortableRecorder.Desktop;

internal enum WindowPhase { Starting, Running, CleaningUp, Closed }

internal abstract record ModeContext
{
    private ModeContext(){}
    public sealed record Recording:ModeContext;
    public sealed record Switching(WorkMode Target,bool Committed):ModeContext;
    public sealed record Listening:ModeContext;
}

internal enum ModalKind { Message, RawDecision, ExclusiveNotice, CalibrationManagerDialog }

internal abstract record OverlayState
{
    private OverlayState(){}
    public sealed record None:OverlayState;
    public sealed record Modal(ModalKind Kind):OverlayState;
    public sealed record CalibrationManager(bool InnerDialogOpen,bool InFilePicker):OverlayState;
    public sealed record FolderPicker:OverlayState;
}

internal sealed class MainWindowCoordinator
{
    public WindowPhase Window {get;private set;}=WindowPhase.Starting;
    public WorkMode CommittedMode {get;private set;}=WorkMode.Recording;
    public ModeContext Mode {get;private set;}=new ModeContext.Recording();
    public bool AudioBusy {get;private set;}
    public OverlayState Overlay {get;private set;}=new OverlayState.None();

    public bool IsStarted=>Window!=WindowPhase.Starting;
    public bool IsClosing=>Window is WindowPhase.CleaningUp or WindowPhase.Closed;
    public bool IsModeSwitching=>Mode is ModeContext.Switching;
    public bool CalibrationManagerOpen=>Overlay is OverlayState.CalibrationManager;
    public bool InRecordingInputContext=>Window==WindowPhase.Running&&(Mode is ModeContext.Recording||Mode is ModeContext.Switching{Target:WorkMode.Recording,Committed:true});

    public bool TryMarkStarted()
    {
        if(Window!=WindowPhase.Starting)return false;
        Window=WindowPhase.Running;
        return true;
    }

    public bool TryBeginCleanup()
    {
        if(IsClosing)return false;
        Window=WindowPhase.CleaningUp;
        return true;
    }

    public void MarkClosed()=>Window=WindowPhase.Closed;

    public bool CanEnableWorkModeSelector(bool active)
        =>!IsClosing&&!active&&!AudioBusy&&!IsModeSwitching;

    public bool TryBeginModeSwitch(WorkMode target,bool sessionActive)
    {
        if(Window!=WindowPhase.Running||sessionActive||AudioBusy||Overlay is not OverlayState.None)return false;
        if(Mode is not (ModeContext.Recording or ModeContext.Listening)||target==CommittedMode)return false;
        Mode=new ModeContext.Switching(target,Committed:false);
        return true;
    }

    public void CommitModeSwitch(WorkMode target)
    {
        CommittedMode=target;
        Mode=new ModeContext.Switching(target,Committed:true);
    }

    public void EndModeSwitch()
        =>Mode=CommittedMode==WorkMode.Listening?new ModeContext.Listening():new ModeContext.Recording();

    public bool TryBeginAudioCommand(bool modeInitialization)
    {
        if(Window!=WindowPhase.Running||AudioBusy)return false;
        if(modeInitialization)
        {
            if(Mode is not ModeContext.Switching{Committed:true})return false;
            AudioBusy=true;
            return true;
        }
        if(Mode is ModeContext.Switching)return false;
        AudioBusy=true;
        return true;
    }

    public void EndAudioCommand()=>AudioBusy=false;

    public bool CanRefreshDevices(bool modeInitialization)
    {
        if(Window!=WindowPhase.Running||AudioBusy)return false;
        return modeInitialization
            ?Mode is ModeContext.Switching{Target:WorkMode.Recording,Committed:true}
            :Mode is ModeContext.Recording&&Overlay is OverlayState.None;
    }

    public bool CanRefreshListeningSources(bool modeInitialization,bool listeningActive,bool refreshInFlight)
    {
        if(Window!=WindowPhase.Running||AudioBusy||listeningActive||refreshInFlight)return false;
        return modeInitialization
            ?Mode is ModeContext.Switching{Target:WorkMode.Listening,Committed:true}
            :Mode is ModeContext.Listening&&Overlay is OverlayState.None;
    }

    public bool CanAlignWindowsGain(RecordingMode recordMode)
        =>Window==WindowPhase.Running&&recordMode==RecordingMode.Exclusive&&Mode is ModeContext.Recording&&!AudioBusy&&Overlay is OverlayState.None;

    public bool CanEnqueueExclusiveNotice()
        =>Window==WindowPhase.Running&&Mode is ModeContext.Recording&&Overlay is OverlayState.None;

    public bool CanOpenRawDecision()
        =>Window==WindowPhase.Running&&Mode is ModeContext.Recording&&!AudioBusy&&Overlay is OverlayState.None;

    public bool TryBeginCalibrationManager(bool recordingActive,bool dialogsBusy=false)
    {
        if(dialogsBusy||Window!=WindowPhase.Running||recordingActive||Mode is not ModeContext.Recording||Overlay is not OverlayState.None)return false;
        Overlay=new OverlayState.CalibrationManager(InnerDialogOpen:false,InFilePicker:false);
        return true;
    }

    public void EndCalibrationManager()
    {
        if(Overlay is OverlayState.CalibrationManager)Overlay=new OverlayState.None();
    }

    public bool TryBeginFilePicker(bool recordingActive,out bool openedInsideManager)
    {
        openedInsideManager=false;
        if(Window!=WindowPhase.Running||recordingActive||Mode is not ModeContext.Recording)return false;
        if(Overlay is OverlayState.CalibrationManager{InnerDialogOpen:false,InFilePicker:false})
        {
            openedInsideManager=true;
            Overlay=new OverlayState.CalibrationManager(InnerDialogOpen:false,InFilePicker:true);
            return true;
        }
        if(Overlay is OverlayState.None)
        {
            Overlay=new OverlayState.CalibrationManager(InnerDialogOpen:false,InFilePicker:true);
            return true;
        }
        return false;
    }

    public void EndFilePicker(bool openedInsideManager)
    {
        if(Overlay is OverlayState.CalibrationManager{InFilePicker:true})
            Overlay=openedInsideManager?new OverlayState.CalibrationManager(InnerDialogOpen:false,InFilePicker:false):new OverlayState.None();
    }

    public bool TryBeginFolderPicker(bool recordingActive)
    {
        if(Window!=WindowPhase.Running||recordingActive||Mode is not ModeContext.Recording||Overlay is not OverlayState.None)return false;
        Overlay=new OverlayState.FolderPicker();
        return true;
    }

    public void EndFolderPicker()
    {
        if(Overlay is OverlayState.FolderPicker)Overlay=new OverlayState.None();
    }

    public bool CanEnterModal(ModalKind kind,bool duringClose)
    {
        if(duringClose)return kind==ModalKind.Message&&Window==WindowPhase.CleaningUp;
        if(Window!=WindowPhase.Running)return false;
        return kind switch
        {
            ModalKind.CalibrationManagerDialog=>Overlay is OverlayState.CalibrationManager{InnerDialogOpen:false,InFilePicker:false},
            ModalKind.ExclusiveNotice=>Mode is ModeContext.Recording&&Overlay is OverlayState.None,
            ModalKind.RawDecision=>Mode is ModeContext.Recording&&!AudioBusy&&Overlay is OverlayState.None,
            ModalKind.Message=>Overlay is OverlayState.None or OverlayState.Modal or OverlayState.CalibrationManager{InnerDialogOpen:false,InFilePicker:false},
            _=>false
        };
    }

    public bool TryEnterModal(ModalKind kind,bool duringClose,out OverlayState? previousOverlay)
    {
        previousOverlay=null;
        if(!CanEnterModal(kind,duringClose))return false;
        previousOverlay=Overlay;
        Overlay=kind==ModalKind.CalibrationManagerDialog
            ?new OverlayState.CalibrationManager(InnerDialogOpen:true,InFilePicker:false)
            :new OverlayState.Modal(kind);
        return true;
    }

    public void ExitModal(OverlayState? previousOverlay)
    {
        if(previousOverlay==null)return;
        Overlay=previousOverlay switch
        {
            OverlayState.CalibrationManager manager=>manager with{InnerDialogOpen=false},
            _=>new OverlayState.None()
        };
    }
}
