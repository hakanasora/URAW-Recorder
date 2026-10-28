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
    private CalibrationLibrarySnapshot _snapshot=new(0,[],new Dictionary<string,string>(),new Dictionary<string,bool>(),null,"");
    public string Folder {get;}
    public CalibrationLibrarySnapshot Snapshot=>Volatile.Read(ref _snapshot);
    public string? Warning=>Snapshot.Warning;
    public CalibrationLibrary(string? folder=null)
    {
        Folder=folder??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PortableRecorder","Calibrations");
        PublishSnapshot();
        try
        {
            string index=Path.Combine(Folder,"calibration-library.json");
            if(File.Exists(index)||Directory.Exists(index))
            {
                var doc=JsonSerializer.Deserialize<Catalog>(AtomicFile.ReadTextBounded(index,8*1024*1024,"校准库索引过大。"))??throw new InvalidDataException("校准库索引为空。");
                _catalog=ValidateAndUpgrade(doc);
                PublishSnapshot();
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {PublishSnapshot("校准库索引不可用，已停用自动校准："+e.Message);}
    }
    public IReadOnlyList<Entry> Entries=>Snapshot.Entries;
    public Selection GetSelection(string deviceId)=>Snapshot.GetSelection(deviceId);
    public string? SelectedId(string deviceId){var choice=GetSelection(deviceId);return choice.Enabled?choice.ProfileId:null;}
    public string GetLocalPath(string id)
        =>Snapshot.GetLocalPath(id);
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
            if(!File.Exists(copy))AtomicFile.WriteAllBytes(copy,bytes,CalibrationProfile.MaximumBytes);
            else if(!ReadBounded(copy).AsSpan().SequenceEqual(bytes))throw new InvalidDataException("校准缓存指纹冲突。");
            var entry=new Entry(profile.Id,profile.Name);var next=new Catalog(2,[.._catalog.Files,entry],new(_catalog.Devices),new(_catalog.Activation!));
            Commit(next);return entry;
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
            var next=new Catalog(2,[.._catalog.Files],bindings,activation);Commit(next);
        }
    }
    public void Remove(string id)
    {
        lock(_gate)
        {
            var activation=new Dictionary<string,bool>(_catalog.Activation!);
            foreach(var binding in _catalog.Devices.Where(p=>p.Value==id))activation[binding.Key]=false;
            var next=new Catalog(2,_catalog.Files.Where(f=>f.Id!=id).ToList(),_catalog.Devices.Where(p=>p.Value!=id).ToDictionary(),activation);
            Commit(next);
            // Only the hash-named application copy can be removed, never the imported original.
            try{File.Delete(FilePath(id));}catch(Exception e) when(e is IOException or UnauthorizedAccessException){PublishSnapshot("校准项目已移除，但应用副本清理失败："+e.Message);}
        }
    }
    private string FilePath(string id)=>ProfilePath(Folder,id);
    internal static string ProfilePath(string folder,string id)=>ValidId(id)?Path.Combine(folder,id+".cal"):throw new InvalidDataException("无效的校准指纹。");
    private static bool ValidId(string id)=>id!=null&&id.Length==64&&id.All(Uri.IsHexDigit);
    private static Catalog ValidateAndUpgrade(Catalog catalog)
    {
        if(catalog.Version is not(1 or 2)||catalog.Files==null||catalog.Devices==null)
            throw new InvalidDataException("校准库索引格式无效。");
        if(catalog.Version==2&&catalog.Activation==null)
            throw new InvalidDataException("V2 校准库索引缺少启用状态。");
        if(catalog.Files.Any(entry=>entry==null||!ValidId(entry.Id)||string.IsNullOrWhiteSpace(entry.Name))||
            catalog.Files.Select(entry=>entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=catalog.Files.Count)
            throw new InvalidDataException("校准库文件项目无效或重复。");
        if(catalog.Devices.Any(binding=>string.IsNullOrWhiteSpace(binding.Key)||!ValidId(binding.Value))||
            catalog.Activation?.Keys.Any(string.IsNullOrWhiteSpace)==true)
            throw new InvalidDataException("校准库设备绑定无效。");
        // Only V1 lacks independent activation. V2 must never infer it from a remembered file.
        return catalog with{Version=2,Activation=catalog.Version==1?catalog.Devices.ToDictionary(binding=>binding.Key,_=>true):catalog.Activation};
    }
    private void Commit(Catalog next)
    {
        AtomicFile.WriteAllBytes(Path.Combine(Folder,"calibration-library.json"),JsonSerializer.SerializeToUtf8Bytes(next),8*1024*1024);
        _catalog=next;PublishSnapshot();
    }
    private void PublishSnapshot(string? warning=null)=>Volatile.Write(ref _snapshot,
        new CalibrationLibrarySnapshot(Snapshot.Revision+1,_catalog.Files,_catalog.Devices,_catalog.Activation!,warning,Folder));
    private static byte[] ReadBounded(string path)=>AtomicFile.ReadBounded(path,CalibrationProfile.MaximumBytes,"校准文件超过 2 MiB。");
}
