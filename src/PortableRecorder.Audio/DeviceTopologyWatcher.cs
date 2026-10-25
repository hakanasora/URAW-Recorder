using NAudio.CoreAudioApi;
namespace PortableRecorder.Audio;

// Native notifications only enqueue work; never enumerate or dispose streams on the COM callback.
public sealed class DeviceTopologyWatcher:IDisposable
{
    private readonly MMDeviceEnumerator _enumerator=new();
    private readonly MMDeviceNotificationClient _notifications;
    public DeviceTopologyWatcher(Action changed)
    {
        try{_notifications=_enumerator.CreateNotificationClient(useSynchronizationContext:false);}
        catch{_enumerator.Dispose();throw;}
        _notifications.DeviceAdded+=(_,_)=>changed();
        _notifications.DeviceRemoved+=(_,_)=>changed();
        _notifications.DeviceStateChanged+=(_,_)=>changed();
        _notifications.DefaultDeviceChanged+=(_,_)=>changed();
    }
    public void Dispose(){_notifications.Dispose();_enumerator.Dispose();}
}
