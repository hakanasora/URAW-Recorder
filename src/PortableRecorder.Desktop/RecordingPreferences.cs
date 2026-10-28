using System.Text.Json;
using PortableRecorder.Audio;
namespace PortableRecorder.Desktop;

internal static class RecordingPreferences
{
    internal static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PortableRecorder","preferences.json");
    private const int MaximumBytes=64*1024;
    public static RecordingMode Load(string? path=null)=>Load(out _,path);
    public static RecordingMode Load(out string? warning,string? path=null)
    {
        warning=null;path??=DefaultPath;
        try
        {
            if(!File.Exists(path)&&!Directory.Exists(path))return RecordingMode.Exclusive;
            using var json=JsonDocument.Parse(AtomicFile.ReadTextBounded(path,MaximumBytes,"录制模式偏好文件过大。"));
            if(json.RootElement.TryGetProperty("RecordingMode",out var mode) && Enum.TryParse<RecordingMode>(mode.GetString(),out var value) && Enum.IsDefined(value))return value;
            throw new InvalidDataException("录制模式偏好缺失或无效。");
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or InvalidDataException)
        {warning="录制模式偏好不可用，当前使用独占："+error.Message;}
        return RecordingMode.Exclusive;
    }
    public static void Save(RecordingMode mode,string? path=null)
    {
        if(!Enum.IsDefined(mode))throw new ArgumentOutOfRangeException(nameof(mode));
        AtomicFile.WriteAllBytes(path??DefaultPath,JsonSerializer.SerializeToUtf8Bytes(new{RecordingMode=mode.ToString()}),MaximumBytes);
    }
}
