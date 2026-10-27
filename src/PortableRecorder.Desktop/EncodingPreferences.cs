using System.Text.Json;
using PortableRecorder.Audio;
namespace PortableRecorder.Desktop;

internal sealed record EncodingPreferences(RecordingCodec Codec=RecordingCodec.Wav,int FlacLevel=5,int Mp3Quality=2)
{
    public RecordingEncoding Encoding=>new(Codec,Codec==RecordingCodec.Mp3?Mp3Quality:FlacLevel);
    private static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PortableRecorder","encoding.json");
    public static EncodingPreferences Load(string? path=null)
    {
        try
        {
            var value=JsonSerializer.Deserialize<EncodingPreferences>(File.ReadAllText(path??DefaultPath));
            if(value!=null&&Enum.IsDefined(value.Codec)&&value.FlacLevel is >=0 and <=8&&value.Mp3Quality is >=0 and <=9)return value;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException){}
        return new();
    }
    public void Save(string? path=null)
    {
        path??=DefaultPath;Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(this));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
