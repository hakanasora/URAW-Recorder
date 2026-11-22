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
        await CalibrationRemovalWriteFailure(directory,check);
        await CalibrationCaptureFailureAfterSave(directory,source,check);
        await ConfiguredExclusiveMonitoring(directory,check);
    }

    private static async Task ConfiguredExclusiveMonitoring(string directory,Action<bool,string> check)
    {
        var factory=new FakeFactory{DenySharedRaw=true};
        var formats=new InputFormatPreferences(Path.Combine(directory,"exclusive-monitor-formats.json"));
        var options=await factory.QueryFormatsAsync(factory.Device);
        var preferred=options.Exclusive.Single(f=>f.SampleRate==96000);
        formats.Set(factory.Device.Id,preferred);
        await using var engine=new AudioEngine(factory,new CalibrationLibrary(Path.Combine(directory,"exclusive-monitor-calibrations")),
            inputFormats:formats,recordingMode:RecordingMode.Exclusive);
        await engine.ReconcileDevicesAsync([factory.Device]);
        var capture=factory.Current!;
        check(engine.Status.Capture is {Exclusive:true,Raw:true} startup&&startup.Format==preferred&&!engine.RawDecisionPending,
            "Saved exclusive mode opens the chosen RAW format before recording");
        factory.Push([.75f]);
        await TestSupport.Until(()=>engine.Latest.Valid,"exclusive idle meter receives samples");
        check(!engine.LiveMonitor.Enabled,"Exclusive meter receives samples with playback switched off");

        await engine.StartConfiguredRecordingAsync(directory);
        string path=engine.Status.FilePath!;
        factory.Push([.25f]);
        await TestSupport.Until(()=>engine.WrittenFrames==1,"exclusive recording receives its first sample");
        await engine.PauseAsync(true);
        long beforePause=engine.Latest.FramePosition;
        factory.Push([.5f]);
        await TestSupport.Until(()=>engine.Latest.FramePosition>beforePause,"exclusive pause keeps meter running");
        check(engine.Status.Capture?.Epoch==capture.Info.Epoch&&engine.WrittenFrames==1,"Pause keeps the capture and excludes paused audio");
        await engine.PauseAsync(false);
        factory.Push([-.25f]);
        await TestSupport.Until(()=>engine.WrittenFrames==2,"exclusive recording resumes on the same stream");
        await engine.StopRecordingAsync();
        check(engine.Status.State==AudioRunState.Monitoring&&engine.Status.Capture==capture.Info&&!capture.Closed&&
            engine.Status.FilePath==path&&!engine.Status.Partial&&!engine.RawDecisionPending,"Stop saves and keeps the original exclusive monitor");
        using(var wave=new WaveFileReader(path))
        {
            var samples=new float[2];wave.ToSampleProvider().Read(samples);
            check(wave.WaveFormat.SampleRate==preferred.SampleRate&&samples.SequenceEqual(new[]{.25f,-.25f}),
                "The file uses the exclusive monitor format and contains just the recording intervals");
        }
        await engine.StartConfiguredRecordingAsync(directory);
        string discarded=engine.Status.FilePath!;
        await engine.PauseAsync(true);
        await engine.DiscardRecordingAsync();
        check(!File.Exists(discarded)&&engine.Status.Capture==capture.Info&&!capture.Closed&&engine.WrittenFrames==0,
            "Discard deletes the recording and keeps the exclusive monitor");

        await engine.SetInputFormatAsync(null);
        var auto=factory.Current!;
        check(capture.Closed&&auto.Info.Exclusive&&engine.Status.Capture?.Format==options.Shared,
            "Selecting automatic reopens the exclusive monitor immediately");
        factory.RejectPreferred=true;
        await engine.SetInputFormatAsync(preferred);
        check(auto.Closed&&engine.Status.State==AudioRunState.Faulted&&engine.Status.Capture==null&&
            !engine.RawDecisionPending&&engine.PreferredInputFormat==preferred&&engine.Status.Message.Contains("独占监看"),
            "Rejected format leaves exclusive selected and reports missing capture without RAW fallback");
        factory.RejectPreferred=false;
        await engine.ReconcileDevicesAsync([factory.Device],manualRefresh:true);
        check(engine.Status.Capture is {Exclusive:true} retried&&retried.Format==preferred,
            "Manual refresh retries the same exclusive format");

        await engine.StartConfiguredRecordingAsync(directory);
        string interrupted=engine.Status.FilePath!;
        var beforeGap=factory.Current!;
        factory.Push([.125f]);
        await TestSupport.Until(()=>engine.WrittenFrames==1,"exclusive recording has data before a capture gap");
        factory.Push([.125f],gap:true);
        try{await engine.StopRecordingAsync();}catch(CaptureGapException){}
        await TestSupport.Until(()=>engine.Status.State==AudioRunState.Monitoring,"exclusive monitoring recovers after a recording gap");
        check(beforeGap.Closed&&engine.Status.Capture is {Exclusive:true} afterGap&&afterGap.Format==preferred&&
            engine.Status.Partial&&engine.Status.FilePath==interrupted&&!engine.RawDecisionPending,
            "Recording gap preserves the partial recording and reopens the selected exclusive format");

        var second=new AudioDevice("second","Second device");
        await engine.ReconcileDevicesAsync([factory.Device,second]);
        check(engine.Status.Capture is {Exclusive:true} handedOff&&handedOff.Device.Id==second.Id&&handedOff.Format==options.Shared,
            "New device is monitored exclusively with its own automatic format");
        await engine.SuspendMonitoringAsync();
        check(factory.Current!.Closed&&engine.Status.Capture==null,"Listening releases the exclusive microphone");
        await engine.ReconcileDevicesAsync([factory.Device,second],manualRefresh:true);
        check(engine.Status.Capture is {Exclusive:true} returned&&returned.Device.Id==second.Id,
            "Returning to recording restores the exclusive monitor");
        await engine.SetRecordingModeAsync(RecordingMode.Shared);
        check(engine.PendingRawConsent!=null&&factory.Current!.Closed,"Selecting shared releases exclusive ownership and follows shared RAW consent");
        var consent=engine.PendingRawConsent!;
        int attempts=0;
        factory.BeforeOpen=_=>{attempts++;throw new RawUnavailableException("Exclusive RAW unavailable");};
        await engine.SetRecordingModeAsync(RecordingMode.Exclusive);
        check(attempts==1&&engine.Status.State==AudioRunState.Faulted&&!engine.RawDecisionPending&&
            !await engine.DecideRawFallbackAsync(consent.DecisionId,true),"Exclusive RAW failure does not prompt for ordinary shared input or accept stale consent");
    }

    private static async Task CalibrationRemovalWriteFailure(string directory,Action<bool,string> check)
    {
        var library=new CalibrationLibrary(Path.Combine(directory,"calibration-write-failure"));
        var factory=new FakeFactory();
        string source=Path.Combine(directory,"boost.cal");
        File.WriteAllText(source,"1 -6\n24000 -6");
        var entry=library.Import(source);
        library.Bind(factory.Device.Id,entry.Id);
        string index=Path.Combine(library.Folder,"calibration-library.json");
        string backup=index+".bak",copy=library.GetLocalPath(entry.Id);
        var catalog=library.Snapshot;
        await using var engine=new AudioEngine(factory,library);
        await engine.SelectDeviceAsync(factory.Device);
        factory.Push(TestSupport.Sine(48000,32768,1000,.75));
        await TestSupport.Until(()=>engine.Latest.Spectrum!=null,"active calibration has spectrum history");
        var before=engine.Latest;
        File.Move(index,backup);
        Directory.CreateDirectory(index);
        try
        {
            bool failed=false;
            try{await engine.RemoveCalibrationAsync(entry.Id);}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException){failed=true;}
            check(failed,"Active calibration removal reports a failed catalog replace");
            check(ReferenceEquals(library.Snapshot,catalog)&&File.Exists(copy)&&engine.CalibrationSelection==new CalibrationLibrary.Selection(true,entry.Id),
                "Failed catalog replace preserves selection and owned profile");
            check(engine.Calibration.Applied&&engine.Calibration.ProfileId==entry.Id&&engine.Latest==before&&engine.Status.State==AudioRunState.Monitoring,
                "Failed catalog replace preserves live calibration revision, segment, meter and spectrum history");
        }
        finally
        {
            Directory.Delete(index);
            File.Move(backup,index);
        }
        check(new CalibrationLibrary(library.Folder).GetSelection(factory.Device.Id)==new CalibrationLibrary.Selection(true,entry.Id),
            "Failed catalog replace leaves the durable selection intact");
        factory.Push([.125f]);
        await TestSupport.Until(()=>engine.Latest.FramePosition>before.FramePosition,"buffered convolution output continues after failed catalog save");
        check(engine.Latest.FramePosition==before.FramePosition+1&&engine.Latest.HeldPeakDbfs==before.HeldPeakDbfs,
            "Monitoring keeps its delayed frame clock and held peak after failed catalog save");
        await engine.RemoveCalibrationAsync(entry.Id);
        check(!engine.Calibration.Applied&&engine.Calibration.Revision==before.CalibrationRevision+1
            &&engine.CalibrationSelection==new CalibrationLibrary.Selection(false,null)&&!File.Exists(copy)&&File.Exists(source),
            "Active removal persists and switches once, deleting only the library copy");
    }

    private static async Task CalibrationCaptureFailureAfterSave(string directory,string source,Action<bool,string> check)
    {
        var library=new CalibrationLibrary(Path.Combine(directory,"calibration-capture-failure"));
        var factory=new FakeFactory();
        var entry=library.Import(source);
        library.Bind(factory.Device.Id,entry.Id);
        string copy=library.GetLocalPath(entry.Id);
        await using var engine=new AudioEngine(factory,library);
        await engine.SelectDeviceAsync(factory.Device);
        var capture=factory.Current!;
        using var reachedPacket=new ManualResetEventSlim();
        using var releasePacket=new ManualResetEventSlim();
        engine.Diagnostic+=message=>
        {
            if(!message.StartsWith("Capture discontinuity:",StringComparison.Ordinal))return;
            reachedPacket.Set();
            releasePacket.Wait();
        };
        // Hold the worker immediately before decoding an invalid packet. The catalog write can finish independently.
        factory.Push([float.NaN],gap:true);
        var nativeFault=new IOException("Injected native capture fault");
        Task? removal=null;
        try
        {
            await TestSupport.Until(()=>reachedPacket.IsSet,"packet worker held before injected PCM failure");
            removal=engine.RemoveCalibrationAsync(entry.Id);
            await TestSupport.Until(()=>library.Entries.Count==0,"catalog removal persisted while packet worker held");
            capture.TriggerFault(nativeFault);
        }
        finally {releasePacket.Set();}
        IOException? failure=null;
        try{await removal!;}
        catch(IOException error){failure=error;}
        check(failure?.InnerException is AggregateException {InnerExceptions: [var first,InvalidDataException]}
            &&ReferenceEquals(first,nativeFault)&&failure.Message.Contains("校准更改已保存",StringComparison.Ordinal),
            "Failure after durable calibration removal reports saved settings, original capture fault, and later processing fault");
        check(capture.Closed&&engine.Status.State==AudioRunState.Faulted&&!engine.Latest.Valid&&!engine.Calibration.Applied,
            "Processing failure after save closes live capture and clears stale analysis");
        check(new CalibrationLibrary(library.Folder).Entries.Count==0&&!File.Exists(copy),
            "Failure after durable removal retains the saved catalog and copy deletion");
        await engine.SelectDeviceAsync(factory.Device);
        check(engine.Status.State==AudioRunState.Monitoring&&!engine.CalibrationSelection.Enabled&&!engine.Calibration.Applied,
            "Reopened monitor uses the saved calibration removal");
    }
}
