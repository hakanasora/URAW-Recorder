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
            Assert(RecordingPreferences.Load(out var warning,out bool shown,settings)==RecordingMode.Shared&&warning==null&&!shown,"A new install starts in shared mode with the exclusive notice unseen");
            File.WriteAllText(settings,"{\"RecordingMode\":\"Exclusive\",\"ExclusiveNoticeShown\":true}");
            Assert(RecordingPreferences.Load(out warning,out shown,settings)==RecordingMode.Exclusive&&warning==null&&!shown,"Legacy exclusive mode persists and the new idle ownership notice is due");
            RecordingPreferences.Save(RecordingMode.Exclusive,settings,exclusiveMonitoringNoticeShown:true);
            Assert(RecordingPreferences.Load(out warning,out shown,settings)==RecordingMode.Exclusive&&warning==null&&shown,"Exclusive choice and notice survive process reload");
            RecordingPreferences.Save(RecordingMode.Shared,settings,exclusiveMonitoringNoticeShown:shown);
            Assert(RecordingPreferences.Load(out warning,out shown,settings)==RecordingMode.Shared&&warning==null&&shown,"Returning to shared mode preserves the notice across restarts");
            File.WriteAllText(settings,"{broken");Assert(RecordingPreferences.Load(settings)==RecordingMode.Shared,"Invalid preference safely falls back");
            Assert(!Directory.GetFiles(root,"*.tmp").Any(),"Atomic preference save cleans temporary file");
        }
        {
            string settings=Path.Combine(root,"output-folder.json");
            Assert(OutputFolderPreferences.Load(out var warning,settings)==""&&warning==null,"New install keeps the default recording folder");
            string desktopFolder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"URAW Recorder Recordings");
            Assert(OutputFolderPreferences.DisplayFolder("")==desktopFolder&&AudioEngine.DefaultRecordingFolder==desktopFolder,"Empty preference displays the Windows desktop recording folder");
            string folder=Path.Combine(root,"录音 空间");
            OutputFolderPreferences.Save(folder,settings);
            Assert(OutputFolderPreferences.Load(out warning,settings)==folder&&warning==null,"Chosen Unicode output folder survives process reload");
            Assert(OutputFolderPreferences.DisplayFolder(folder)==folder,"Custom folder is displayed unchanged");
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
