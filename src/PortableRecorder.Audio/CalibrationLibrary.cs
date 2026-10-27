using System.Security.Cryptography;
using System.Text.Json;

namespace PortableRecorder.Audio;

public sealed class CalibrationLibrary
{
    public sealed record Entry(string Id,string Name);
    public sealed record Selection(bool Enabled,string? ProfileId);
    private sealed record Catalog(int Version,List<Entry> Files,Dictionary<string,string> Devices,Dictionary<string,bool>? Activation=null);
    private readonly object _gate=new();
    private Catalog _catalog=new(2,[],new(),new());
    public string Folder {get;}
    public string? Warning {get;private set;}
    public CalibrationLibrary(string? folder=null)
    {
        Folder=folder??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PortableRecorder","Calibrations");
        try
        {
            string index=Path.Combine(Folder,"calibration-library.json");
            if(File.Exists(index))
            {
                if(new FileInfo(index).Length>8*1024*1024)throw new InvalidDataException("校准库索引过大。");
                var doc=JsonSerializer.Deserialize<Catalog>(File.ReadAllText(index))??throw new InvalidDataException("校准库索引为空。");
                if(doc.Version is not(1 or 2)||doc.Files==null||doc.Devices==null||doc.Files.Any(f=>f==null||!ValidId(f.Id)||string.IsNullOrWhiteSpace(f.Name))||doc.Files.Select(f=>f.Id).Distinct().Count()!=doc.Files.Count||doc.Devices.Any(v=>string.IsNullOrWhiteSpace(v.Key)||!ValidId(v.Value))||doc.Activation?.Keys.Any(string.IsNullOrWhiteSpace)==true)throw new InvalidDataException("校准库索引格式无效。");
                // Existing selected profiles migrate to enabled, without modifying the old file until a user change.
                _catalog=doc with{Version=2,Activation=doc.Activation??doc.Devices.ToDictionary(p=>p.Key,_=>true)};
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {Warning="校准库索引不可用，已停用自动校准："+e.Message;}
    }
    public IReadOnlyList<Entry> Entries {get{lock(_gate)return _catalog.Files.ToArray();}}
    public Selection GetSelection(string deviceId)
    {lock(_gate)return new(_catalog.Activation!.GetValueOrDefault(deviceId),_catalog.Devices.GetValueOrDefault(deviceId));}
    public string? SelectedId(string deviceId){var choice=GetSelection(deviceId);return choice.Enabled?choice.ProfileId:null;}
    public string GetLocalPath(string id)
    {lock(_gate){if(!_catalog.Files.Any(e=>e.Id==id))throw new InvalidDataException("校准项目不存在。");return FilePath(id);}}
    public CalibrationProfile Read(string id)
    {
        lock(_gate)
        {
            var entry=_catalog.Files.FirstOrDefault(e=>e.Id==id)??throw new InvalidDataException("校准项目不存在。");
            string path=FilePath(id);byte[] bytes=ReadBounded(path);
            if(!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),id,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("校准副本指纹不匹配，已阻止启用。");
            return CalibrationProfile.Parse(bytes,entry.Name);
        }
    }
    public Entry Import(string path)
    {
        byte[] bytes=ReadBounded(path);var profile=CalibrationProfile.Parse(bytes,Path.GetFileName(path));
        lock(_gate)
        {
            var old=_catalog.Files.FirstOrDefault(e=>e.Id==profile.Id);if(old!=null){_ = Read(old.Id);return old;}
            Directory.CreateDirectory(Folder);string copy=FilePath(profile.Id);
            if(!File.Exists(copy))AtomicWrite(copy,bytes);
            else if(!File.ReadAllBytes(copy).AsSpan().SequenceEqual(bytes))throw new InvalidDataException("校准缓存指纹冲突。");
            var entry=new Entry(profile.Id,profile.Name);var next=new Catalog(2,[.._catalog.Files,entry],new(_catalog.Devices),new(_catalog.Activation!));
            Save(next);_catalog=next;return entry;
        }
    }
    public void Bind(string deviceId,string? id)
        =>Configure(deviceId,new(id!=null,id));
    public void Configure(string deviceId,Selection selection)
    {
        if(string.IsNullOrWhiteSpace(deviceId))throw new InvalidOperationException("尚未选择输入设备。");
        lock(_gate)
        {
            string? id=selection.ProfileId;
            if(id!=null&& !_catalog.Files.Any(e=>e.Id==id))
            {
                if(selection.Enabled)throw new InvalidDataException("校准项目不存在。");
                id=null; // A damaged dangling binding must never prevent explicitly disabling correction.
            }
            var bindings=new Dictionary<string,string>(_catalog.Devices);
            if(id==null)bindings.Remove(deviceId);else bindings[deviceId]=id;
            var activation=new Dictionary<string,bool>(_catalog.Activation!){[deviceId]=selection.Enabled};
            var next=new Catalog(2,[.._catalog.Files],bindings,activation);Save(next);_catalog=next;
        }
    }
    public void Remove(string id)
    {
        lock(_gate)
        {
            var activation=new Dictionary<string,bool>(_catalog.Activation!);
            foreach(var binding in _catalog.Devices.Where(p=>p.Value==id))activation[binding.Key]=false;
            var next=new Catalog(2,_catalog.Files.Where(f=>f.Id!=id).ToList(),_catalog.Devices.Where(p=>p.Value!=id).ToDictionary(),activation);
            Save(next);_catalog=next;
            // Only the hash-named application copy can be removed, never the imported original.
            try{File.Delete(FilePath(id));}catch(Exception e) when(e is IOException or UnauthorizedAccessException){Warning="校准项目已移除，但应用副本清理失败："+e.Message;}
        }
    }
    private string FilePath(string id)=>ValidId(id)?Path.Combine(Folder,id+".cal"):throw new InvalidDataException("无效的校准指纹。");
    private static bool ValidId(string id)=>id!=null&&id.Length==64&&id.All(Uri.IsHexDigit);
    private void Save(Catalog next)
    {AtomicWrite(Path.Combine(Folder,"calibration-library.json"),JsonSerializer.SerializeToUtf8Bytes(next));Warning=null;}
    internal static void AtomicWrite(string path,byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private static byte[] ReadBounded(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length>CalibrationProfile.MaximumBytes)throw new InvalidDataException("校准文件超过 2 MiB。");
        var data=new byte[(int)stream.Length];stream.ReadExactly(data);return data;
    }
}
