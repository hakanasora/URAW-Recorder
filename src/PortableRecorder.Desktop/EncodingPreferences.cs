using System.Text.Json;
using System.Text.Json.Serialization;
using PortableRecorder.Audio;
namespace PortableRecorder.Desktop;

internal sealed record EncodingPreferences(RecordingCodec Codec=RecordingCodec.Wav,int FlacLevel=5,int Mp3Quality=2)
{
    [JsonIgnore] public RecordingEncoding Encoding=>new(Codec,Codec==RecordingCodec.Mp3?Mp3Quality:FlacLevel);
    private static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PortableRecorder","encoding.json");
    private const int MaximumBytes=64*1024;
    public static EncodingPreferences Load(string? path=null)=>Load(out _,path);
    public static EncodingPreferences Load(out string? warning,string? path=null)
    {
        warning=null;path??=DefaultPath;
        try
        {
            if(!File.Exists(path)&&!Directory.Exists(path))return new();
            var value=JsonSerializer.Deserialize<EncodingPreferences>(AtomicFile.ReadTextBounded(path,MaximumBytes,"编码偏好文件过大。"));
            if(value!=null&&value.IsValid)return value;
            throw new InvalidDataException("编码格式或压缩品质偏好无效。");
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidDataException)
        {warning="编码偏好不可用，当前使用浮点 WAV："+error.Message;}
        return new();
    }
    [JsonIgnore] private bool IsValid=>Enum.IsDefined(Codec)&&FlacLevel is >=0 and <=8&&Mp3Quality is >=0 and <=9;
    public void Save(string? path=null)
    {
        if(!IsValid)throw new InvalidDataException("编码格式或压缩品质偏好无效。");
        AtomicFile.WriteAllBytes(path??DefaultPath,JsonSerializer.SerializeToUtf8Bytes(this),MaximumBytes);
    }
}
