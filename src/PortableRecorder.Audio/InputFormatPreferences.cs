using System.Text.Json;
namespace PortableRecorder.Audio;

/// <summary>Per-endpoint exclusive capture preference, separate from mode and calibration storage.</summary>
public sealed class InputFormatPreferences
{
    private readonly object _gate=new();
    private readonly string _path;
    private Dictionary<string,AudioFormat> _formats=new();
    public string? Warning {get;private set;}
    public InputFormatPreferences(string? path=null)
    {
        _path=path??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PortableRecorder","input-formats.json");
        try
        {
            if(!File.Exists(_path))return;
            if(new FileInfo(_path).Length>1024*1024)throw new InvalidDataException("输入格式偏好文件过大。");
            _formats=JsonSerializer.Deserialize<Dictionary<string,AudioFormat>>(File.ReadAllText(_path))??throw new InvalidDataException("输入格式偏好为空。");
            if(_formats.Any(p=>string.IsNullOrWhiteSpace(p.Key)||!Valid(p.Value)))throw new InvalidDataException("输入格式偏好无效。");
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {_formats=new();Warning="输入格式偏好不可用，当前使用自动格式："+e.Message;}
    }
    private static bool Valid(AudioFormat? f)=>f!=null&&f.SampleRate is >=8000 and <=384000&&f.Channels is >0 and <=64&&
        f.Bits is 16 or 24 or 32&&f.ValidBits>0&&f.ValidBits<=f.Bits&&Enum.IsDefined(f.Encoding)&&
        (f.Encoding!=PcmEncoding.Float||f.Bits==32&&f.ValidBits==32);
    public AudioFormat? Get(string id){lock(_gate)return _formats.GetValueOrDefault(id);}
    public void Set(string id,AudioFormat? format)
    {
        if(string.IsNullOrWhiteSpace(id)||format!=null&&!Valid(format))throw new InvalidDataException("输入格式偏好无效。");
        lock(_gate)
        {
            var next=new Dictionary<string,AudioFormat>(_formats);if(format==null)next.Remove(id);else next[id]=format;
            CalibrationLibrary.AtomicWrite(_path,JsonSerializer.SerializeToUtf8Bytes(next));_formats=next;Warning=null;
        }
    }
}
