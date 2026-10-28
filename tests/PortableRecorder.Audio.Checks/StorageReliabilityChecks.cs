using System.Text;
using System.Text.Json;
using PortableRecorder.Audio;
using PortableRecorder.Desktop;

static class StorageReliabilityChecks
{
    public static Task Run(string root,Action<bool,string> check)
    {
        string folder=Path.Combine(root,"storage-reliability");Directory.CreateDirectory(folder);
        string source=Path.Combine(folder,"source.cal");File.WriteAllText(source,"10 0\n20000 0");
        var library=new CalibrationLibrary(Path.Combine(folder,"calibrations"));var entry=library.Import(source);
        string index=Path.Combine(library.Folder,"calibration-library.json");
        File.WriteAllText(index,JsonSerializer.Serialize(new{Version=1,Files=library.Entries,Devices=new Dictionary<string,string>{{"device",entry.Id}}}));
        check(new CalibrationLibrary(library.Folder).GetSelection("device")==new CalibrationLibrary.Selection(true,entry.Id),"V1 legacy binding migrates to enabled");
        string legacy=File.ReadAllText(index);File.WriteAllText(index,legacy,Encoding.Unicode);
        check(new CalibrationLibrary(library.Folder).GetSelection("device").Enabled,"Bounded catalog reader preserves legacy BOM text decoding");
        foreach(bool explicitlyNull in new[]{false,true})
        {
            string json=JsonSerializer.Serialize(new{Version=2,Files=library.Entries,Devices=new Dictionary<string,string>{{"device",entry.Id}}});
            if(explicitlyNull)json=json[..^1]+",\"Activation\":null}";
            File.WriteAllText(index,json);var damaged=new CalibrationLibrary(library.Folder);
            check(damaged.Warning!=null&&!damaged.GetSelection("device").Enabled,"V2 missing/null activation cannot re-enable remembered calibration");
        }
        string orphanFolder=Path.Combine(folder,"orphan");Directory.CreateDirectory(orphanFolder);
        string id=CalibrationProfile.Parse(Encoding.UTF8.GetBytes(File.ReadAllText(source)),"source.cal").Id;
        using(var oversized=new FileStream(Path.Combine(orphanFolder,id+".cal"),FileMode.CreateNew))oversized.SetLength(CalibrationProfile.MaximumBytes+1);
        bool bounded=false;try{new CalibrationLibrary(orphanFolder).Import(source);}catch(InvalidDataException error){bounded=error.Message.Contains("2 MiB");}
        check(bounded,"Orphan cache uses the same 2 MiB bound as ordinary calibration reads");
        SnapshotChecks(folder,source,check);
        PreferenceChecks(folder,check);
        AtomicWriteChecks(folder,check);
        string blockedCatalog=Path.Combine(folder,"directory-catalog");
        Directory.CreateDirectory(Path.Combine(blockedCatalog,"calibration-library.json"));
        check(new CalibrationLibrary(blockedCatalog).Warning!=null,"Directory at catalog path is an error, not a new installation");
        string blockedFormats=Path.Combine(folder,"directory-formats.json");
        Directory.CreateDirectory(blockedFormats);
        check(new InputFormatPreferences(blockedFormats).Warning!=null,"Directory at format preference path reports an explicit warning");
        return Task.CompletedTask;
    }

    private static void SnapshotChecks(string folder,string source,Action<bool,string> check)
    {
        var library=new CalibrationLibrary(Path.Combine(folder,"snapshots"));var initial=library.Snapshot;
        var entry=library.Import(source);var imported=library.Snapshot;
        check(imported.Revision>initial.Revision&&initial.Entries.Count==0&&imported.Entries.Count==1,"Catalog publishes new immutable entry snapshot after successful import");
        library.Configure("device",new(true,entry.Id));var configured=library.Snapshot;
        check(configured.Revision>imported.Revision&&!imported.GetSelection("device").Enabled&&configured.GetSelection("device").Enabled,"Old snapshots remain unchanged after selection publication");
        // Deliberately hold the writer lock: UI metadata reads must remain entirely memory-only.
        var gate=typeof(CalibrationLibrary).GetField("_gate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(library)!;
        Task<bool> read;
        lock(gate)
        {
            read=Task.Run(()=>library.Snapshot.Revision==configured.Revision&&library.Entries.Count==1&&library.GetSelection("device").Enabled&&library.GetLocalPath(entry.Id).EndsWith(".cal"));
            check(read.Wait(TimeSpan.FromSeconds(2))&&read.Result,"Catalog UI snapshot, entries, selection and paths never wait on the disk writer lock");
        }
        string index=Path.Combine(library.Folder,"calibration-library.json");
        using(var locked=new FileStream(index,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            bool failed=false;try{library.Configure("device",new(false,entry.Id));}catch(Exception error) when(error is IOException or UnauthorizedAccessException){failed=true;}
            check(failed&&ReferenceEquals(library.Snapshot,configured)&&library.GetSelection("device").Enabled,"Failed persistence cannot publish uncommitted catalog state");
        }
        using(var locked=new FileStream(library.GetLocalPath(entry.Id),FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            library.Remove(entry.Id);var removed=library.Snapshot;
            check(removed.Revision>configured.Revision&&removed.Warning!=null&&removed.Entries.Count==0&&!removed.GetSelection("device").Enabled,"Cleanup warning is visible in a new snapshot while completed removal stays committed");
            check(configured.GetLocalPath(entry.Id)==Path.Combine(library.Folder,entry.Id+".cal"),"Captured snapshot can project its own entry path after concurrent removal");
        }
    }

    private static void PreferenceChecks(string folder,Action<bool,string> check)
    {
        string modePath=Path.Combine(folder,"mode.json"),encodingPath=Path.Combine(folder,"encoding.json"),formatsPath=Path.Combine(folder,"formats.json");
        check(RecordingPreferences.Load(out var modeWarning,modePath)==RecordingMode.Exclusive&&modeWarning==null,"New install uses default mode without corruption warning");
        check(EncodingPreferences.Load(out var encodingWarning,encodingPath)==new EncodingPreferences()&&encodingWarning==null,"New install uses WAV without corruption warning");
        RecordingPreferences.Save(RecordingMode.Shared,modePath);
        check(RecordingPreferences.Load(out modeWarning,modePath)==RecordingMode.Shared&&modeWarning==null,"Recording mode JSON remains compatible");
        File.WriteAllText(modePath,File.ReadAllText(modePath),new UTF8Encoding(true));
        check(RecordingPreferences.Load(modePath)==RecordingMode.Shared,"Bounded mode reader preserves UTF-8 BOM compatibility");
        var original=new EncodingPreferences(RecordingCodec.Mp3,8,2);original.Save(encodingPath);
        using(var json=JsonDocument.Parse(File.ReadAllText(encodingPath)))
            check(!json.RootElement.TryGetProperty("Encoding",out _)&&json.RootElement.GetProperty("FlacLevel").GetInt32()==8,"Encoding serialization excludes derived recording configuration");
        File.WriteAllText(encodingPath,"{\"Codec\":2,\"FlacLevel\":8,\"Mp3Quality\":2,\"Encoding\":{\"Codec\":2,\"Quality\":2}}");
        check(EncodingPreferences.Load(encodingPath)==original,"Old encoding JSON with redundant derived property still loads");
        File.WriteAllText(encodingPath,File.ReadAllText(encodingPath),Encoding.Unicode);
        check(EncodingPreferences.Load(encodingPath)==original,"Bounded encoding reader preserves legacy BOM text decoding");
        foreach(string path in new[]{modePath,encodingPath,formatsPath})
        {
            using(var oversized=new FileStream(path,FileMode.Create))oversized.SetLength((path==formatsPath?1024*1024:64*1024)+1);
            if(path==modePath)check(RecordingPreferences.Load(out modeWarning,path)==RecordingMode.Exclusive&&modeWarning?.Contains("过大")==true,"Mode preference size bound is explicit");
            else if(path==encodingPath)check(EncodingPreferences.Load(out encodingWarning,path)==new EncodingPreferences()&&encodingWarning?.Contains("过大")==true,"Encoding preference size bound is explicit");
            else check(new InputFormatPreferences(path).Warning?.Contains("过大")==true,"Input format preference uses shared bounded reader");
        }
        File.WriteAllText(modePath,"{\"RecordingMode\":true}");
        check(RecordingPreferences.Load(out modeWarning,modePath)==RecordingMode.Exclusive&&modeWarning!=null,"Invalid JSON value kind reports a mode warning");
        File.WriteAllText(encodingPath,"{\"Codec\":9,\"FlacLevel\":5,\"Mp3Quality\":2}");
        check(EncodingPreferences.Load(out encodingWarning,encodingPath)==new EncodingPreferences()&&encodingWarning!=null,"Invalid encoding enum reports a warning");
        var preferences=new InputFormatPreferences(formatsPath);preferences.Set("device",new(48000,1,32,32,PcmEncoding.Float));
        check(preferences.Warning==null&&new InputFormatPreferences(formatsPath).Get("device")?.SampleRate==48000,"Successful format save repairs preference warning and survives reload");
    }

    private static void AtomicWriteChecks(string folder,Action<bool,string> check)
    {
        string path=Path.Combine(folder,"atomic.json");AtomicFile.WriteAllBytes(path,"original"u8,64);
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            Exception? failure=null;try{AtomicFile.WriteAllBytes(path,"replacement"u8,64);}catch(Exception error) when(error is IOException or UnauthorizedAccessException){failure=error;}
            check(failure!=null&&File.ReadAllText(path)=="original","Failed atomic rename preserves original file and reports the I/O cause");
        }
        check(!Directory.GetFiles(folder,"atomic.json.*.tmp").Any(),"Failed atomic write cleans its owned temporary file");
        bool bounded=false;try{AtomicFile.WriteAllBytes(path,new byte[65],64);}catch(InvalidDataException){bounded=true;}
        check(bounded&&File.ReadAllText(path)=="original","Oversized preference write fails before replacing original");
        AtomicFile.WriteAllBytes(path,"replacement"u8,64);
        check(File.ReadAllText(path)=="replacement","Successful atomic replacement publishes complete new contents");
    }
}
