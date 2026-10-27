using System.Text.Json;
using PortableRecorder.Audio;
using NAudio.Wave;

static class InputSettingsChecks
{
    public static async Task Run(string root,Action<bool,string> check)
    {
        async Task Reject(Func<Task> operation,string name)
        {bool rejected=false;try{await operation();}catch(InvalidOperationException){rejected=true;}catch(NotSupportedException){rejected=true;}catch(IOException){rejected=true;}check(rejected,name);}
        string directory=Path.Combine(root,"input-settings");Directory.CreateDirectory(directory);
        var library=new CalibrationLibrary(Path.Combine(directory,"calibrations"));
        string source=Path.Combine(directory,"microphone.cal");File.WriteAllText(source,"10 0\n24000 0");var entry=library.Import(source);
        string index=Path.Combine(library.Folder,"calibration-library.json");
        File.WriteAllText(index,JsonSerializer.Serialize(new{Version=1,Files=library.Entries,Devices=new Dictionary<string,string>{{"fake",entry.Id}}}));
        library=new(library.Folder);
        check(library.GetSelection("fake")==new CalibrationLibrary.Selection(true,entry.Id),"V1 selected calibration migrates to enabled without losing binding");
        string preferences=Path.Combine(directory,"input-formats.json");var formats=new InputFormatPreferences(preferences);
        var factory=new FakeFactory();
        await using(var engine=new AudioEngine(factory,library,inputFormats:formats))
        {
            await engine.SelectDeviceAsync(factory.Device);
            check(engine.Calibration.Applied,"Migrated calibration active");
            await engine.SetCalibrationEnabledAsync(false);
            check(!engine.Calibration.Applied&&engine.CalibrationSelection.ProfileId==entry.Id&&!engine.CalibrationSelection.Enabled,"Disable bypasses but remembers selected file");
            check(new CalibrationLibrary(library.Folder).GetSelection("fake")==new CalibrationLibrary.Selection(false,entry.Id),"Enabled flag and remembered file persist independently");
            await engine.SetCalibrationEnabledAsync(true);check(engine.Calibration.Applied&&engine.Calibration.ProfileId==entry.Id,"Enable restores remembered valid profile");
            var options=await engine.QueryInputFormatsAsync();var chosen=options.Exclusive.First(f=>f.SampleRate==96000);
            await engine.SetInputFormatAsync(chosen);
            check(engine.Status.Capture!.Format==options.Shared&&!engine.Status.Capture.Exclusive,"Selecting an exclusive format does not reopen or change shared monitoring");
            check(new InputFormatPreferences(preferences).Get(factory.Device.Id)==chosen,"Exclusive format preference persists per endpoint");
            await engine.StartRecordingAsync(directory,RecordingMode.Shared);
            check(factory.LastRequestedFormat==null&&engine.Status.Capture!.Format==options.Shared,"Shared recording ignores remembered exclusive format");
            await engine.PauseAsync(true);
            await Reject(()=>engine.SetInputFormatAsync(null),"Input format locked during pause");
            await Reject(()=>engine.SetCalibrationEnabledAsync(false),"Enable control locked during pause");
            await Reject(()=>engine.SetCalibrationAsync(entry.Id),"File selector locked during pause");
            await engine.StopRecordingAsync();
            await engine.StartRecordingAsync(directory,RecordingMode.Exclusive);string path=engine.Status.FilePath!;
            check(factory.LastRequestedFormat==chosen&&engine.Status.Capture!.Format==chosen,"Exclusive capture receives exactly the requested rate, container and encoding");
            factory.Push([.25f,-.5f]);
            await engine.StopRecordingAsync();
            using(var wave=new WaveFileReader(path))
            {
                check(wave.WaveFormat.SampleRate==96000&&wave.WaveFormat.BitsPerSample==32,"WAV remains float32 at actual selected capture rate");
                var samples=new float[2];wave.ToSampleProvider().Read(samples);
                check(samples.SequenceEqual(new[]{.25f,-.5f}),"Selected 24-bit PCM capture flows through real decoder into float32 WAV");
            }
            check(engine.Status.Capture!.Format==options.Shared&&!engine.Status.Capture.Exclusive,"Stop restores shared format while retaining exclusive preference");
            await Reject(()=>engine.SetInputFormatAsync(new(12345,1,16,16,PcmEncoding.SignedInteger)),"Unconfirmed formats cannot be saved");
            check(engine.PreferredInputFormat==chosen,"Rejected format leaves previous choice intact");
            factory.RejectPreferred=true;
            await Reject(()=>engine.StartRecordingAsync(directory,RecordingMode.Exclusive),"Explicit format failure never silently chooses another exclusive format");
            check(engine.Status.State==AudioRunState.Monitoring&&!engine.Status.Capture!.Exclusive&&engine.PreferredInputFormat==chosen,"Explicit format failure restores monitoring and preserves choice");
            factory.RejectPreferred=false;
            await engine.SelectDeviceAsync(new("second","Second input"));
            check(engine.PreferredInputFormat==null&&!engine.CalibrationSelection.Enabled,"New endpoint gets automatic format and disabled calibration");
            await engine.SelectDeviceAsync(factory.Device);check(engine.PreferredInputFormat==chosen&&engine.Calibration.Applied,"Returning endpoint restores its own independent choices");
            await engine.SetInputFormatAsync(null);check(new InputFormatPreferences(preferences).Get(factory.Device.Id)==null,"Explicit automatic format clears only format preference");
            await engine.SetCalibrationEnabledAsync(false);await engine.RemoveCalibrationAsync(entry.Id);
            check(engine.CalibrationSelection==new CalibrationLibrary.Selection(false,null),"Removing remembered file clears it without selecting another profile");
        }
        var emptyFactory=new FakeFactory();await using(var engine=new AudioEngine(emptyFactory,new CalibrationLibrary(Path.Combine(directory,"empty")),inputFormats:formats))
        {
            await engine.SelectDeviceAsync(emptyFactory.Device);await engine.SetCalibrationEnabledAsync(true);
            check(engine.CalibrationSelection.Enabled&&engine.CalibrationSelection.ProfileId==null&&!engine.CalibrationReadyToRecord,"Enable without a file remains an explicit incomplete selection");
            await Reject(()=>engine.StartRecordingAsync(directory,RecordingMode.Shared),"Enabled but unconfigured calibration cannot record uncorrected audio silently");
            await engine.SetCalibrationEnabledAsync(false);check(engine.CalibrationReadyToRecord,"Disable restores recording readiness without requiring a file");
            var imported=await engine.ImportCalibrationAsync(source);
            check(!engine.CalibrationSelection.Enabled&&engine.CalibrationSelection.ProfileId==null,"Management import does not enable or select calibration");
            string copy=engine.Calibrations.GetLocalPath(imported.Id);
            check(Path.GetFullPath(copy).StartsWith(Path.GetFullPath(engine.Calibrations.Folder))&&File.Exists(copy),"File tooltip resolves the actual owned calibration path");
            File.WriteAllText(copy,"corrupt cache");
            check(engine.Calibrations.GetLocalPath(imported.Id)==copy,"Path tooltip resolution does not hash or parse file contents");
        }
        File.WriteAllText(preferences,"invalid-json");var damaged=new InputFormatPreferences(preferences);check(damaged.Warning!=null,"Damaged format preferences are reported");
        damaged.Set("fake",null);check(damaged.Warning==null,"Explicit format preference save clears repaired-index warning");
        File.WriteAllText(index,JsonSerializer.Serialize(new{Version=1,Files=Array.Empty<CalibrationLibrary.Entry>(),Devices=new Dictionary<string,string>{{"fake",entry.Id}}}));
        var dangling=new CalibrationLibrary(library.Folder);
        dangling.Configure("fake",new(false,entry.Id));
        check(dangling.GetSelection("fake")==new CalibrationLibrary.Selection(false,null),"Dangling old binding cannot trap user in enabled calibration");
    }
}
