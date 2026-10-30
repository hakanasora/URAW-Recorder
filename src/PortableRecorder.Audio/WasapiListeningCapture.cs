using System.Buffers;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PortableRecorder.Audio;

public sealed class WasapiListeningCaptureFactory:IListeningCaptureFactory
{
    public async Task<IListeningCaptureSession> OpenAsync(ListeningSource source,Func<CapturePacket,bool> submit,Action<Exception> fault)
    {
        MMDevice? device=null;WasapiRecorder? recorder=null;
        try
        {
            var builder=new WasapiRecorderBuilder().WithSharedMode().WithEventSync()
                .WithBufferLength(100).WithMmcssThreadPriority("Audio");
            if(source.Scope==ListeningScope.SystemEndpoint)
            {
                if(string.IsNullOrWhiteSpace(source.EndpointId))throw new ArgumentException("请选择输出设备。");
                using var enumerator=new MMDeviceEnumerator();device=enumerator.GetDevice(source.EndpointId);
                // Preserve the actual endpoint mix format and all of its channels.
                using var client=device.CreateAudioClient();
                builder.WithDevice(device).WithLoopbackCapture().WithFormat(client.MixFormat);
            }
            else
            {
                if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,20348))
                    throw new PlatformNotSupportedException("应用／增强前回环需要 Windows 版本 20348 或更新版本。");
                if(!ListeningSources.IsSameApplication(source))throw new InvalidOperationException("所选应用已经退出，请重新选择。");
                builder.WithProcessLoopback((uint)(source.Scope==ListeningScope.Application?source.ProcessId:Environment.ProcessId),
                    source.Scope==ListeningScope.Application?ProcessLoopbackMode.IncludeTargetProcessTree:ProcessLoopbackMode.ExcludeTargetProcessTree)
                    .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000,2));
            }
            recorder=await builder.BuildAsync().ConfigureAwait(false);
            if(!ListeningSources.IsSameApplication(source))throw new InvalidOperationException("所选应用已退出，请重新选择。");
            var session=new WasapiListeningCapture(recorder,device,submit,fault);
            session.Start();return session;
        }
        catch
        {
            if(recorder!=null)await recorder.DisposeAsync().ConfigureAwait(false);
            device?.Dispose();throw;
        }
    }
}

internal sealed class WasapiListeningCapture: IListeningCaptureSession
{
    private readonly WasapiRecorder _recorder;
    private readonly MMDevice? _device;
    private readonly Func<CapturePacket,bool> _submit;
    private readonly Action<Exception> _fault;
    private int _stopping;
    public AudioFormat Format=>WasapiCaptureSession.Describe(_recorder.WaveFormat);
    public WasapiListeningCapture(WasapiRecorder recorder,MMDevice? device,Func<CapturePacket,bool> submit,Action<Exception> fault)
    {
        _recorder=recorder;_device=device;_submit=submit;_fault=fault;
        recorder.DataAvailable+=OnData;
        recorder.RecordingStopped+=(_,e)=>
        {
            if(Volatile.Read(ref _stopping)==0)_fault(e.Exception??new IOException("侦听音频流已停止，请重新开始侦听。"));
        };
    }
    public void Start()=>_recorder.StartRecording();
    private void OnData(ReadOnlySpan<byte> data,AudioClientBufferFlags flags,long position,long qpc)
    {
        if(Volatile.Read(ref _stopping)!=0||data.IsEmpty)return;
        byte[]? bytes=null;
        try
        {
            int align=_recorder.WaveFormat.BlockAlign;
            if(data.Length%align!=0)throw new InvalidDataException("回环音频包未按声道帧对齐。");
            bytes=ArrayPool<byte>.Shared.Rent(data.Length);data.CopyTo(bytes);
            var packet=new CapturePacket(bytes,data.Length,data.Length/align,position,
                (flags&AudioClientBufferFlags.Silent)!=0,(flags&AudioClientBufferFlags.DataDiscontinuity)!=0,(int)flags,qpc100ns:qpc);
            bytes=null;if(!_submit(packet))packet.Dispose();
        }
        catch(Exception e){if(bytes!=null)ArrayPool<byte>.Shared.Return(bytes);_fault(e);}
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref _stopping,1)!=0)return;
        try{await _recorder.DisposeAsync().ConfigureAwait(false);}
        finally{_device?.Dispose();}
    }
}
