using PortableRecorder.Desktop;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using PortableRecorder.Audio;

using static TestSupport;

static class PreferencesChecks
{
    public static Task Run(string root,Action<bool,string> Assert)
    {
        {
            string settings=Path.Combine(root,"preferences.json");
            Assert(RecordingPreferences.Load(settings)==RecordingMode.Exclusive,"Missing mode preference uses default");
            RecordingPreferences.Save(RecordingMode.Shared,settings);Assert(RecordingPreferences.Load(settings)==RecordingMode.Shared,"Shared choice survives process reload");
            RecordingPreferences.Save(RecordingMode.Exclusive,settings);Assert(RecordingPreferences.Load(settings)==RecordingMode.Exclusive,"Exclusive choice survives process reload");
            File.WriteAllText(settings,"{broken");Assert(RecordingPreferences.Load(settings)==RecordingMode.Exclusive,"Invalid preference safely falls back");
            Assert(!Directory.GetFiles(root,"*.tmp").Any(),"Atomic preference save cleans temporary file");
        }
        {
            string settings=Path.Combine(root,"output-folder.json");
            Assert(OutputFolderPreferences.Load(out var warning,settings)==""&&warning==null,"New install keeps the default recording folder");
            string folder=Path.Combine(root,"录音 空间");
            OutputFolderPreferences.Save(folder,settings);
            Assert(OutputFolderPreferences.Load(out warning,settings)==folder&&warning==null,"Chosen Unicode output folder survives process reload");
            Assert(!Directory.Exists(folder),"Remembering a folder does not create it");
            using(var locked=new FileStream(settings,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                bool writeFailed=false;try{OutputFolderPreferences.Save(Path.Combine(root,"another"),settings);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){writeFailed=true;}
                Assert(writeFailed&&OutputFolderPreferences.Load(out warning,settings)==folder,"Failed persistence reports failure and preserves the previous folder");
            }
            Assert(!Directory.GetFiles(root,"output-folder.json.*.tmp").Any(),"Failed folder save cleans its private temporary file");
            OutputFolderPreferences.Save("",settings);
            Assert(OutputFolderPreferences.Load(out warning,settings)==""&&warning==null,"Clearing the folder persists the default choice");
            File.WriteAllText(settings,"{broken");
            Assert(OutputFolderPreferences.Load(out warning,settings)==""&&warning!=null,"Corrupt output-folder settings report a warning and use default");
            File.WriteAllText(settings,"{\"OutputFolder\":true}");
            Assert(OutputFolderPreferences.Load(out warning,settings)==""&&warning!=null,"Wrong output-folder value type is rejected");
            File.WriteAllText(settings,"{\"OutputFolder\":\"relative-folder\"}");
            Assert(OutputFolderPreferences.Load(out warning,settings)==""&&warning!=null,"Saved relative folders cannot silently change with working directory");
            bool rejected=false;try{OutputFolderPreferences.Save("relative-folder",settings);}catch(ArgumentException){rejected=true;}
            Assert(rejected,"Manually entered relative folders are not persisted");
            using(var oversized=new FileStream(settings,FileMode.Create))oversized.SetLength(64*1024+1);
            Assert(OutputFolderPreferences.Load(out warning,settings)==""&&warning?.Contains("过大")==true,"Output folder preference reads are bounded");
        }
        return Task.CompletedTask;
    }
}
