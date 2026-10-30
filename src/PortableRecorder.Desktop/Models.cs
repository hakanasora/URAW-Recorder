using System.Collections.ObjectModel;
using System.ComponentModel;
using PortableRecorder.Audio;
namespace PortableRecorder.Desktop;
public enum MonitorMode { Level, Spectrum, Pitch }
public enum RecordingStage { Ready, Recording, Paused, Stopped }
public sealed record InputDevice(string Id,string FriendlyName);
public sealed record CalibrationFile(string Path,string Name,string? Id=null);
public sealed record MonitorSnapshot(bool Valid,double RmsDbfs,double SourcePeakDbfs,double OutputPeakDbfs,
    double VuLeft,double VuRight,double? MidiNote,double? FrequencyHz,double? Cents,SpectrumFrame? Spectrum);
public readonly record struct PitchPoint(double Time,double? Midi);
public sealed class TransportSession
{
    public RecordingStage Stage {get;private set;}
    public bool Active=>Stage is RecordingStage.Recording or RecordingStage.Paused;
    public TimeSpan Elapsed {get;private set;}
    public void Update(AudioRunState state,TimeSpan elapsed)
    {
        Stage=state switch {AudioRunState.Preparing or AudioRunState.Recording or AudioRunState.Finalizing=>RecordingStage.Recording,
            AudioRunState.Paused=>RecordingStage.Paused,_=>elapsed>TimeSpan.Zero?RecordingStage.Stopped:RecordingStage.Ready};
        Elapsed=elapsed;
    }
    public string DisplayTime=>Elapsed.TotalHours>=1
        ? $"{(long)Elapsed.TotalHours:00}:{Elapsed.Minutes:00}:{Elapsed.Seconds:00}.{Elapsed.Milliseconds/10:00}"
        : $"{(long)Elapsed.TotalMinutes:00}:{Elapsed.Seconds:00}.{Elapsed.Milliseconds/10:00}";
}
public sealed class ShellViewModel:INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public AudioEngine Audio {get;}
    public ListeningService Listener {get;}=new();
    public bool IsListening {get;private set;}
    private double _recordGain,_listenGain;
    public AudioSnapshot CurrentAudioSnapshot
    {
        get{var source=IsListening?Listener.Latest:Audio.Latest;return IsListening?source with{Epoch=source.Epoch|long.MinValue}:source;}
    }
    public TransportSession Session {get;}=new();
    public ObservableCollection<InputDevice> Devices {get;}=[];
    public ObservableCollection<CalibrationFile> Calibrations {get;}=[];
    public InputDevice SelectedInput {get;set;}=new("","未选择输入设备");
    public CalibrationFile? SelectedCalibration {get;set;}
    public MonitorMode Mode {get;set;}
    public RecordingMode RecordMode {get;set;}
    internal EncodingPreferences EncodingSettings {get;}
    internal string? ModePreferenceWarning {get;}
    internal string? EncodingPreferenceWarning {get;}
    public double GainDb {get;private set;}
    public double? HeldPeak {get;private set;}
    public RecordingEncoding Encoding {get;set;}
    public MonitorSnapshot Snapshot {get;private set;}=new(false,double.NaN,double.NaN,double.NaN,
        double.NegativeInfinity,double.NegativeInfinity,null,null,null,null);
    public Queue<PitchPoint> PitchHistory {get;}=new();
    public double MonitorTime {get;private set;}
    private long _epoch=-1,_pitchFrame=-1;
    private int _segment=-1;
    private long _calibrationLibraryRevision=-1;
    public ShellViewModel(AudioEngine? audio=null)
    {
        Audio=audio??new AudioEngine();
        RecordMode=RecordingPreferences.Load(out var modeWarning);ModePreferenceWarning=modeWarning;
        EncodingSettings=EncodingPreferences.Load(out var encodingWarning);EncodingPreferenceWarning=encodingWarning;
        Encoding=EncodingSettings.Encoding;RefreshCalibrations();
    }
    public void RefreshCalibrations()
    {
        var snapshot=Audio.Calibrations.Snapshot;
        if(snapshot.Revision!=_calibrationLibraryRevision)
        {
            var entries=snapshot.Entries.ToDictionary(e=>e.Id,StringComparer.OrdinalIgnoreCase);
            for(int i=Calibrations.Count-1;i>=0;i--)if(Calibrations[i].Id is not string id||!entries.ContainsKey(id))Calibrations.RemoveAt(i);
            var existing=Calibrations.Where(c=>c.Id!=null).Select(c=>c.Id!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in snapshot.Entries)
                if(existing.Add(entry.Id))Calibrations.Add(new(snapshot.GetLocalPath(entry.Id),entry.Name,entry.Id));
            _calibrationLibraryRevision=snapshot.Revision;
        }
        string? selected=Audio.SelectedDevice is { } device?snapshot.GetSelection(device.Id).ProfileId:null;
        SelectedCalibration=Calibrations.FirstOrDefault(c=>string.Equals(c.Id,selected,StringComparison.OrdinalIgnoreCase));
    }
    public void SetGain(double db)
    {
        if(!double.IsFinite(db))return;
        GainDb=Math.Round(Math.Clamp(db,-60,60),1);if(Math.Abs(GainDb)<.05)GainDb=0;
        if(IsListening){_listenGain=GainDb;Listener.SetGain(GainDb);}
        else{_recordGain=GainDb;Audio.SetGain(GainDb);}Notify();
    }
    public void SetWorkMode(bool listening)
    {
        IsListening=listening;GainDb=listening?_listenGain:_recordGain;
        PitchHistory.Clear();_epoch=-1;_pitchFrame=-1;_segment=-1;
        Refresh();Notify();
    }
    public async Task ResetPeakAsync(){if(IsListening)Listener.ResetPeak();else await Audio.ResetPeakAsync();Refresh();Notify();}
    public void Refresh()
    {
        var source=CurrentAudioSnapshot;
        if(source.Epoch!=_epoch || source.Segment!=_segment){PitchHistory.Clear();_pitchFrame=-1;_epoch=source.Epoch;_segment=source.Segment;}
        MonitorTime=source.Time;HeldPeak=source.HeldPeakDbfs;
        double? hz=source.Valid?source.FrequencyHz:null;
        double? midi=hz is double f?69+12*Math.Log2(f/440):null;
        Snapshot=new(source.Valid,source.RmsDbfs,source.SourcePeakDbfs,source.OutputPeakDbfs,source.VuLeft,source.VuRight,
            midi,hz,midi is double n?(n-Math.Round(n))*100:null,source.Valid?source.Spectrum:null);
        if(!source.Valid)PitchHistory.Clear();
        else if(source.PitchFramePosition!=_pitchFrame)
        {
            _pitchFrame=source.PitchFramePosition;
            PitchHistory.Enqueue(new(source.PitchFramePosition/(double)source.SampleRate,midi));
            while(PitchHistory.TryPeek(out var point) && point.Time<MonitorTime-4.1)PitchHistory.Dequeue();
        }
        Session.Update(Audio.Status.State,Audio.RecordedTime);
    }
    public void Notify()=>PropertyChanged?.Invoke(this,new(null));
}
