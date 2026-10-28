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
        return Task.CompletedTask;
    }
}
