using PortableRecorder.Audio;

namespace PortableRecorder.Desktop;

internal sealed record TransportPresentation(bool Active, bool Paused, bool CanStop, bool CanPause,
    bool CanDiscard, string StatusText, int PauseWidth, int StopWidth)
{
    // Service states such as Preparing and Recording share one visual stage. A status
    // refresh must not interrupt a transition that is already running for that stage.
    public static bool NeedsMotionUpdate(RecordingStage? previous,RecordingStage current,bool reset)=>reset||previous!=current;

    public static TransportPresentation From(AudioRunState state, bool busy, bool calibrationReady)
    {
        bool paused = state == AudioRunState.Paused;
        bool active = state is AudioRunState.Preparing or AudioRunState.Recording or AudioRunState.Paused or AudioRunState.Finalizing;
        bool canStop = !busy && (state is AudioRunState.Preparing or AudioRunState.Recording or AudioRunState.Paused ||
            state == AudioRunState.Monitoring && calibrationReady);
        return new(active, paused, canStop,
            !busy && state is AudioRunState.Recording or AudioRunState.Paused,
            !busy && paused,
            state switch { AudioRunState.Paused => "暂停中", AudioRunState.Preparing => "正在准备",
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
        return $"打开时 Windows 输入增益 {db} dB。"+(volume.HardwareVolume
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
    private long _completed;
    private long? _showing;
    public bool TryBegin(long decision)
    {
        if (_showing != null || _completed == decision) return false;
        _showing = decision;
        return true;
    }
    public void Complete(long decision) { if (_showing == decision) { _completed = decision; _showing = null; } }
    public void Abandon(long decision) { if (_showing == decision) _showing = null; }
}
