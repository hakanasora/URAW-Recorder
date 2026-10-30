using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PortableRecorder.Audio;

public enum ListeningScope { SystemBeforeEffects, SystemEndpoint, Application }
public enum ListeningState { Stopped, Opening, Listening, Waiting, Faulted }
public sealed record ListeningSource(ListeningScope Scope,string Name,string? EndpointId=null,
    int ProcessId=0,long ProcessStartTicks=0,string? ExecutablePath=null);
public sealed record ListeningStatus(ListeningState State,ListeningSource? Source,AudioFormat? Format,string Message);

// This boundary also permits deterministic tests without opening a render endpoint.
public interface IListeningCaptureSession:IAsyncDisposable { AudioFormat Format {get;} }
public interface IListeningCaptureFactory
{
    Task<IListeningCaptureSession> OpenAsync(ListeningSource source,Func<CapturePacket,bool> submit,Action<Exception> fault);
}

public static class ListeningSources
{
    public static IReadOnlyList<AudioDevice> EnumerateEndpoints()
    {
        using var enumerator=new MMDeviceEnumerator();string? defaultId=null;
        try{using var d=enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);defaultId=d.ID;}
        catch(COMException) { /* No default output is a valid empty-device state. */ }
        using var devices=enumerator.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active);
        var result=new List<AudioDevice>();
        foreach(var device in devices)using(device)result.Add(new(device.ID,device.FriendlyName,device.ID==defaultId));
        return result.OrderByDescending(d=>d.IsDefault).ThenBy(d=>d.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static IReadOnlyList<ListeningSource> EnumerateApplications()
    {
        // Show visible application roots, including quiet apps; an audio-session-only
        // list would hide applications until they first played a sound.
        var result=new List<ListeningSource>();
        foreach(var process in Process.GetProcesses())using(process)
        {
            try
            {
                if(process.Id==Environment.ProcessId||process.MainWindowHandle==IntPtr.Zero)continue;
                string? path=null;try{path=process.MainModule?.FileName;}catch(Win32Exception){}
                string? description=null;
                try{if(path!=null)description=FileVersionInfo.GetVersionInfo(path).FileDescription;}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException)
                { /* An unavailable description must not remove the process or fail the entire list. */ }
                string name=string.IsNullOrWhiteSpace(description)?process.ProcessName:description.Trim();
                result.Add(new(ListeningScope.Application,name,ProcessId:process.Id,
                    ProcessStartTicks:process.StartTime.ToUniversalTime().Ticks,ExecutablePath:path));
            }
            catch(Exception e)when(e is Win32Exception or InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
            { /* Exited and inaccessible processes cannot be selectable targets. */ }
        }
        return result.OrderBy(p=>p.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(p=>p.ProcessId).ToArray();
    }

    public static bool IsSameApplication(ListeningSource source)
    {
        if(source.Scope!=ListeningScope.Application)return true;
        if(source.ProcessId<=0||source.ProcessStartTicks<=0)return false;
        try{using var p=Process.GetProcessById(source.ProcessId);return !p.HasExited&&p.StartTime.ToUniversalTime().Ticks==source.ProcessStartTicks;}
        catch(Exception e)when(e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException){return false;}
    }
}
