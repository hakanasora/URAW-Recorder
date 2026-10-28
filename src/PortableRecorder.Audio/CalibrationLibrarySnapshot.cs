using System.Collections.Frozen;

namespace PortableRecorder.Audio;

/// <summary>A versioned memory-only view; UI reads never wait for catalog disk operations.</summary>
public sealed class CalibrationLibrarySnapshot
{
    private readonly FrozenDictionary<string,string> _devices;
    private readonly FrozenDictionary<string,bool> _activation;
    private readonly string _folder;
    public long Revision {get;}
    public IReadOnlyList<CalibrationLibrary.Entry> Entries {get;}
    public string? Warning {get;}

    internal CalibrationLibrarySnapshot(long revision,IEnumerable<CalibrationLibrary.Entry> entries,
        IReadOnlyDictionary<string,string> devices,IReadOnlyDictionary<string,bool> activation,string? warning,string folder)
    {
        Revision=revision;Entries=Array.AsReadOnly(entries.ToArray());
        _devices=devices.ToFrozenDictionary();_activation=activation.ToFrozenDictionary();Warning=warning;_folder=folder;
    }

    public CalibrationLibrary.Selection GetSelection(string deviceId)=>
        new(_activation.GetValueOrDefault(deviceId),_devices.GetValueOrDefault(deviceId));
    public string GetLocalPath(string id)
    {
        if(!Entries.Any(entry=>entry.Id==id))throw new InvalidDataException("校准项目不存在。");
        return CalibrationLibrary.ProfilePath(_folder,id);
    }
}
