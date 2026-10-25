using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;

namespace PortableRecorder.Desktop;

public enum MonitorMode { Level, Spectrum, Pitch }
public enum RecordingStage { Ready, Recording, Paused, Stopped }
public enum MockScenario { Normal, OutputClip, SourceClipOnly, MissingPitch }
public sealed record InputDevice(string Id, string FriendlyName);
public sealed record CalibrationFile(string Path, string Name);
public sealed record MonitorSnapshot(double RmsDbfs, double SourcePeakDbfs, double OutputPeakDbfs,
    double VuLeft, double VuRight, double? MidiNote, double? FrequencyHz, double? Cents, float[] SpectrumBins);
public readonly record struct PitchPoint(double Time, double? Midi);

public sealed class TransportSession
{
    private readonly Stopwatch _clock = new();
    public RecordingStage Stage { get; private set; }
    public bool Active => Stage is RecordingStage.Recording or RecordingStage.Paused;
    public TimeSpan Elapsed => _clock.Elapsed;
    public void Start() { _clock.Restart(); Stage = RecordingStage.Recording; }
    public void Pause() { if (Stage != RecordingStage.Recording) return; _clock.Stop(); Stage = RecordingStage.Paused; }
    public void Resume() { if (Stage != RecordingStage.Paused) return; _clock.Start(); Stage = RecordingStage.Recording; }
    public void Stop() { _clock.Stop(); Stage = RecordingStage.Stopped; }
    public string DisplayTime
    {
        get
        {
            var t = Elapsed;
            return t.TotalHours >= 1
                ? $"{(long)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}"
                : $"{(long)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
        }
    }
}

public sealed class ShellViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public TransportSession Session { get; } = new();
    public ObservableCollection<InputDevice> Devices { get; } =
        [new("usb", "USB Audio Device"), new("umik", "UMIK-1 · USB"), new("default", "系统默认输入")];
    public ObservableCollection<CalibrationFile> Calibrations { get; } =
        [new("", "不使用校准"), new("sample:7146299", "7146299.txt"), new("sample:7146299-90", "7146299_90deg.txt")];
    public InputDevice SelectedInput { get; set; }
    public CalibrationFile SelectedCalibration { get; set; }
    public MonitorMode Mode { get; set; }
    public MockScenario Scenario { get; set; }
    public double GainDb { get; private set; }
    public double? HeldPeak { get; private set; }
    public string OutputPath { get; set; } = "";
    public string Format { get; set; } = "WAV";
    public string Mp3Quality { get; set; } = "320 kbps";
    public string FlacQuality { get; set; } = "标准 · 等级 5";
    public MonitorSnapshot Snapshot { get; private set; }
    public Queue<PitchPoint> PitchHistory { get; } = new();
    public double MonitorTime { get; private set; }
    public bool CurrentClipping => Snapshot.SourcePeakDbfs >= 0 || Snapshot.OutputPeakDbfs >= 0;
    public ShellViewModel()
    {
        SelectedInput = Devices[0]; SelectedCalibration = Calibrations[0];
        Snapshot = MockMonitorSource.Create(0, 0, MockScenario.Normal);
    }
    public void SetGain(double db)
    {
        if (!double.IsFinite(db)) return;
        GainDb = Math.Round(Math.Clamp(db, -60, 24), 1);
        if (Math.Abs(GainDb) < .05) GainDb = 0;
        Refresh(MonitorTime, false);
        Notify();
    }
    public void ResetPeak() { HeldPeak = null; Notify(); }
    public void Refresh(double seconds, bool appendHistory = true)
    {
        MonitorTime = seconds;
        Snapshot = MockMonitorSource.Create(seconds, GainDb, Scenario);
        HeldPeak = HeldPeak is double old ? Math.Max(old, Snapshot.OutputPeakDbfs) : Snapshot.OutputPeakDbfs;
        if (appendHistory)
        {
            PitchHistory.Enqueue(new(seconds, Snapshot.MidiNote));
            while (PitchHistory.TryPeek(out var p) && p.Time < seconds - 4.1) PitchHistory.Dequeue();
        }
    }
    public void Notify() => PropertyChanged?.Invoke(this, new(null));
}

public static class MockMonitorSource
{
    private static readonly (double Time, double Midi)[] Phrase =
        [(0,47),(2,47),(3.2,53),(4.4,60),(5.8,76),(7.2,79),(8.2,79),(9.5,60),(10.8,38),(12.5,38),(14,45),(15.5,47),(18,47)];
    public static MonitorSnapshot Create(double t, double gain, MockScenario scenario)
    {
        var rms = -32 + 1.1 * Math.Sin(t * .73) + .3 * Math.Sin(t * 2.1);
        var peak = rms + 13.5;
        if (scenario == MockScenario.SourceClipOnly) { rms = -12; peak = 1.2; }
        var outputPeak = peak + gain;
        var phase = t % 18; var index = 0;
        while (index < Phrase.Length - 2 && phase > Phrase[index + 1].Time) index++;
        var a = Phrase[index]; var b = Phrase[index + 1];
        var u = Math.Clamp((phase - a.Time) / (b.Time - a.Time), 0, 1); u = u*u*(3-2*u);
        double? midi = scenario == MockScenario.MissingPitch ? null : a.Midi + (b.Midi-a.Midi)*u + .025*Math.Sin(t*9);
        // UI mock samples are uniformly spaced in log frequency, from 20 Hz to 20 kHz.
        // A future linear FFT must be mapped using its actual Hz values, not this mock index convention.
        var bins = new float[211];
        for (var i=0; i<bins.Length; i++)
        {
            var x=(double)i/(bins.Length-1);
            var amplitude=.14+.11*Math.Exp(-Math.Pow((x-.14)/.12,2))+.48*Math.Exp(-Math.Pow((x-.49)/.24,2))+.16*Math.Exp(-Math.Pow((x-.79)/.12,2));
            amplitude += .018*Math.Sin(x*33+t*.2)+.009*Math.Cos(x*52);
            bins[i]=(float)(-84+amplitude*72+gain);
        }
        var vu = -12 + 2.3*Math.Sin(t*.8) + gain;
        return new(rms+gain,peak,outputPeak,vu,vu,midi,
            midi is double m ? 440*Math.Pow(2,(m-69)/12) : null,
            midi is double n ? (n-Math.Round(n))*100 : null,bins);
    }
}
