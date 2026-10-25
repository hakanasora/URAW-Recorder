using System.Text.Json;
using PortableRecorder.Audio;
namespace PortableRecorder.Desktop;

internal static class RecordingPreferences
{
    internal static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PortableRecorder","preferences.json");
    public static RecordingMode Load(string? path=null)
    {
        try
        {
            using var json=JsonDocument.Parse(File.ReadAllText(path??DefaultPath));
            if(json.RootElement.TryGetProperty("RecordingMode",out var mode) && Enum.TryParse<RecordingMode>(mode.GetString(),out var value) && Enum.IsDefined(value))return value;
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException){}
        return RecordingMode.Exclusive;
    }
    public static void Save(RecordingMode mode,string? path=null)
    {
        path??=DefaultPath;Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(new{RecordingMode=mode.ToString()}));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
