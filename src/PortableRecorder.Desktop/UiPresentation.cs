using PortableRecorder.Audio;

namespace PortableRecorder.Desktop;

internal enum WorkMode { Recording, Listening }

internal sealed class WorkModeTransition
{
    public WorkMode? Target { get; private set; }

    public bool TryBegin(WorkMode current, WorkMode target, bool blocked)
    {
        if (blocked || Target != null || current == target) return false;
        Target = target;
        return true;
    }

    public async Task ApplyAsync(Func<Task> stopCurrent, Func<bool> isClosing,
        Action<WorkMode> commit, Func<WorkMode, Task> refresh)
    {
        try
        {
            if (Target is not WorkMode target || isClosing()) return;
            await stopCurrent();
            if (isClosing()) return;
            commit(target);
            await refresh(target);
        }
        finally
        {
            End();
        }
    }

    public void End() => Target = null;
}

internal sealed record TransportPresentation(bool Active, bool Paused, bool CanStop, bool CanPause,
    bool CanDiscard, string StatusText, int PauseWidth, int StopWidth)
{
    // Service states such as Preparing and Recording share one visual stage. A status
    // refresh must not interrupt a transition that is already running for that stage.
    public static bool NeedsMotionUpdate(RecordingStage? previous,RecordingStage current,bool reset)=>reset||previous!=current;

    public static TransportPresentation From(AudioRunState state, bool busy, bool calibrationReady)
    {
        bool paused = state == AudioRunState.Paused;
        bool active = state is AudioRunState.Preparing or AudioRunState.Recording or AudioRunState.Pausing or AudioRunState.Paused or AudioRunState.Finalizing;
        bool canStop = !busy && (state is AudioRunState.Preparing or AudioRunState.Recording or AudioRunState.Pausing or AudioRunState.Paused ||
            state == AudioRunState.Monitoring && calibrationReady);
        return new(active, paused, canStop,
            !busy && state is AudioRunState.Recording or AudioRunState.Paused,
            !busy && paused,
            state switch { AudioRunState.Pausing => "正在暂停", AudioRunState.Paused => "暂停中", AudioRunState.Preparing => "正在准备",
                AudioRunState.Finalizing => "正在保存", _ => "录制中" },
            active ? paused ? 100 : 42 : 0, active && paused ? 42 : 100);
    }
}

internal static class InputFormatPresentation
{
    public static string? AlignmentUnavailable(EndpointVolumeSnapshot? volume)
    {
        if(volume==null||!float.IsFinite(volume.Decibels))return "Windows 输入增益不可用";
        if(volume.Muted)return "Windows 输入已静音，无法对齐";
        if(volume.HardwareVolume)return "设备使用硬件音量，无法按软件增益对齐";
        if(volume.Decibels is <-60 or >60)return "Windows 增益超出软件增益范围";
        return null;
    }
    public static string FormatGain(float db)=>db.ToString("+0.0;−0.0;0.0",System.Globalization.CultureInfo.InvariantCulture)+" dB";
    public static string EndpointGain(EndpointVolumeSnapshot? volume,bool exclusive)
    {
        if(volume==null)return "Windows 输入增益未知；共享与独占电平可能不同。";
        string db=volume.Decibels.ToString("+0.0;-0.0;0.0",System.Globalization.CultureInfo.InvariantCulture);
        return $"当前 Windows 输入增益 {db} dB。"+(volume.HardwareVolume
            ?"设备提供硬件音量控制，共享与独占均可能受其影响。"
            :exclusive?"独占已绕过此软件增益；软件增益旋钮仍生效。":"共享受此软件增益影响，独占绕过它；切换时电平可能变化。");
    }
    public static bool CanSelect(RecordingMode mode, bool hasDevice, bool querying, bool hasFormats,
        bool busy, bool recording) => mode == RecordingMode.Exclusive && hasDevice && !querying &&
        hasFormats && !busy && !recording;

    public static string Name(AudioFormat format) =>
        $"{format.Channels} 声道 · {(format.Encoding == PcmEncoding.Float ? "float32" : format.ValidBits == format.Bits ? $"{format.ValidBits} bit" : $"{format.ValidBits}/{format.Bits} bit")} · {format.SampleRate / 1000d:0.###} kHz";
}

/// <summary>A failed ShowAsync must not consume the device's pending consent request.</summary>
internal sealed class RawDecisionPrompt
{
    private abstract record PromptState
    {
        private PromptState() { }
        public sealed record Idle(long? LastCompletedId) : PromptState;
        public sealed record Showing(long DecisionId, long? LastCompletedId) : PromptState;
    }

    private PromptState _state = new PromptState.Idle(null);

    public bool TryBegin(long decision)
    {
        if (_state is PromptState.Showing || (_state is PromptState.Idle idle && idle.LastCompletedId == decision))
            return false;
        _state = new PromptState.Showing(decision, (_state as PromptState.Idle)?.LastCompletedId);
        return true;
    }

    public void Complete(long decision)
    {
        if (_state is PromptState.Showing showing && showing.DecisionId == decision)
            _state = new PromptState.Idle(decision);
    }

    public void Abandon(long decision)
    {
        if (_state is PromptState.Showing showing && showing.DecisionId == decision)
            _state = new PromptState.Idle(showing.LastCompletedId);
    }
}
