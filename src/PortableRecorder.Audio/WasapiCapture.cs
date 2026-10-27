using System.Buffers;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace PortableRecorder.Audio;

public sealed class WasapiCaptureFactory:ICaptureFactory
{
    public IReadOnlyList<AudioDevice> Enumerate()
    {
        using var enumerator=new MMDeviceEnumerator();
        string? defaultId=null;
        try{using var endpoint=enumerator.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Console);defaultId=endpoint.ID;}catch(COMException){}
        using var devices=enumerator.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active);
        var result=new List<AudioDevice>();
        foreach(var device in devices)
            using(device) result.Add(new(device.ID,device.FriendlyName,device.ID==defaultId));
        return result.OrderByDescending(d=>d.IsDefault).ToArray();
    }
    public async Task<IAudioCaptureService> OpenAsync(AudioDevice device,bool exclusive,bool raw,long epoch,
        Func<CapturePacket,bool> packet,Action<Exception> fault,AudioFormat? preferredFormat=null)
    {
        var capture=new WasapiCaptureSession(device,exclusive,raw,epoch,packet,fault,preferredFormat);
        try{await capture.Ready.ConfigureAwait(false);return capture;}
        catch{await capture.DisposeAsync();throw;}
    }
    public Task<InputFormatOptions> QueryFormatsAsync(AudioDevice description)=>Task.Run(()=>
    {
        // Capability queries do not initialize/start a stream or take exclusive ownership.
        using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDevice(description.Id);
        using var client=device.CreateAudioClient();var mix=client.MixFormat;
        var formats=WasapiCaptureSession.Candidates(mix)
            .Where(w=>client.IsFormatSupported(AudioClientShareMode.Exclusive,w))
            .Select(WasapiCaptureSession.Describe).Distinct().ToArray();
        return new InputFormatOptions(WasapiCaptureSession.Describe(mix),formats);
    });
}
internal sealed class WasapiCaptureSession:IAudioCaptureService
{
    private readonly CancellationTokenSource _stop=new();
    private readonly TaskCompletionSource<CaptureInfo> _ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _done=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<CaptureInfo> Ready=>_ready.Task;
    public CaptureInfo Info=>_ready.Task.GetAwaiter().GetResult();
    public WasapiCaptureSession(AudioDevice device,bool exclusive,bool raw,long epoch,Func<CapturePacket,bool> submit,Action<Exception> fault,AudioFormat? preferredFormat)
    {
        var thread=new Thread(()=>Run(device,exclusive,raw,epoch,submit,fault,preferredFormat)){IsBackground=true,Name="Portable Recorder WASAPI"};
        thread.SetApartmentState(ApartmentState.MTA);thread.Start();
    }
    private void Run(AudioDevice description,bool exclusive,bool raw,long epoch,Func<CapturePacket,bool> submit,Action<Exception> fault,AudioFormat? preferredFormat)
    {
        AudioClient? client=null;IntPtr mmcss=IntPtr.Zero;
        try
        {
            using var enumerator=new MMDeviceEnumerator();
            using var device=enumerator.GetDevice(description.Id);
            client=device.CreateAudioClient();
            var mode=exclusive?AudioClientShareMode.Exclusive:AudioClientShareMode.Shared;
            if(raw)RequestRaw(client);
            var mix=client.MixFormat;
            var wave=exclusive?FindFormat(client,mix,preferredFormat):mix;
            var format=Describe(wave);
            using var signal=new EventWaitHandle(false,EventResetMode.AutoReset);
            // Shared monitoring benefits from scheduling headroom; event cadence remains engine-driven.
            long duration=Math.Max(exclusive?300_000:1_000_000,client.DefaultDevicePeriod);
            for(int attempt=0;;attempt++)
            {
                if(raw)RequestRaw(client);
                try{client.Initialize(mode,AudioClientStreamFlags.EventCallback,duration,exclusive?duration:0,wave,Guid.Empty);break;}
                catch(Exception error) when(exclusive && attempt==0 && error.HResult==unchecked((int)0x88890019))
                {
                    var aligned=client.BufferSize;
                    duration=(long)Math.Round(10_000_000d*aligned/format.SampleRate);
                    client.Dispose();client=device.CreateAudioClient();
                }
                catch(Exception error) when(raw && error.HResult==unchecked((int)0x88890027))
                {throw new RawUnavailableException("设备不支持 RAW 采集。",error);}
            }
            client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle());
            var capture=client.AudioCaptureClient;
            uint taskIndex=0;mmcss=AvSetMmThreadCharacteristics("Pro Audio",ref taskIndex);
            client.Start();
            _ready.TrySetResult(new(description,format,exclusive,raw,epoch));
            var waits=new WaitHandle[]{signal,_stop.Token.WaitHandle};
            var continuity=new CaptureContinuity();
            while(!_stop.IsCancellationRequested)
            {
                int signaled=WaitHandle.WaitAny(waits,3000);
                if(signaled==1)break;
                if(signaled==WaitHandle.WaitTimeout)throw new IOException("采集设备连续 3 秒未提供音频包。");
                // GetNextPacketSize is shared-mode only. Exclusive events supply one buffer.
                do
                {
                    var pointer=capture.GetBuffer(out int frames,out var flags,out long position,out long qpc);
                    if(frames==0)break;
                    try
                    {
                        var boundary=continuity.Observe(frames,position,(int)flags);
                        var bytes=ArrayPool<byte>.Shared.Rent(frames*format.BlockAlign);
                        var packet=new CapturePacket(bytes,frames*format.BlockAlign,frames,position,
                            (flags&AudioClientBufferFlags.Silent)!=0,boundary.Discontinuous,(int)flags,boundary.Delta,qpc);
                        try
                        {
                            if(packet.Silent)Array.Clear(bytes,0,packet.Length);
                            else Marshal.Copy(pointer,bytes,0,packet.Length);
                            if(!submit(packet))throw new IOException("采集处理队列溢出，已停止，未静默丢包。");
                        }
                        catch{packet.Dispose();throw;}
                    }
                    finally{capture.ReleaseBuffer(frames);}
                }while(!exclusive && !_stop.IsCancellationRequested && capture.GetNextPacketSize()>0);
            }
        }
        catch(Exception error){if(!_ready.TrySetException(error) && !_stop.IsCancellationRequested)fault(error);}
        finally
        {
            if(client!=null){try{client.Stop();}catch{}client.Dispose();}
            if(mmcss!=IntPtr.Zero)AvRevertMmThreadCharacteristics(mmcss);
            _done.TrySetResult();
        }
    }
    private static void RequestRaw(AudioClient client)
    {
        try{client.SetClientProperties(AudioStreamCategory.Other,AudioClientStreamOptions.Raw);}
        catch(Exception e) when(e is NotSupportedException || e is InvalidOperationException ||
            e.HResult==unchecked((int)0x80004002) || e.HResult==unchecked((int)0x88890027))
        {throw new RawUnavailableException("设备不支持 RAW 请求。",e);}
    }
    private static WaveFormat FindFormat(AudioClient client,WaveFormat mix,AudioFormat? preferred)
    {
        foreach(var candidate in Candidates(mix))
            if((preferred==null||Describe(candidate)==preferred)&&client.IsFormatSupported(AudioClientShareMode.Exclusive,candidate))return candidate;
        throw new NotSupportedException(preferred==null?"设备没有可用的独占 PCM 格式，请检查驱动或选择共享录制。":"设备当前不支持所选独占输入格式，请重新选择或明确改用自动格式。");
    }
    internal static IEnumerable<WaveFormat> Candidates(WaveFormat mix)
    {
        Describe(mix);yield return mix;
        var original=Describe(mix);
        int mask=mix is WaveFormatExtensible extensible?extensible.ChannelMask:0;
        foreach(var rate in new[]{mix.SampleRate,48000,44100,96000,88200,192000,176400}.Distinct())
        {
            WaveFormat same=new WaveFormatExtensible(rate,original.Bits,mix.Channels,original.Encoding==PcmEncoding.Float,original.ValidBits,mask);
            foreach(var candidate in new WaveFormat[]{same,new WaveFormat(rate,24,mix.Channels),
                new WaveFormatExtensible(rate,32,mix.Channels,false,24,mask),
                WaveFormat.CreateIeeeFloatWaveFormat(rate,mix.Channels),new WaveFormat(rate,32,mix.Channels),new WaveFormat(rate,16,mix.Channels)})
            {
                yield return candidate;
                // Some drivers only accept extensible descriptors (especially >2 channels).
                var normal=Describe(candidate);
                var ext=new WaveFormatExtensible(rate,normal.Bits,mix.Channels,normal.Encoding==PcmEncoding.Float,normal.ValidBits,mask);
                yield return ext;
            }
        }
    }
    internal static AudioFormat Describe(WaveFormat wave)
    {
        var standard=wave.AsStandardWaveFormat();
        int valid=wave is WaveFormatExtensible ext?ext.ValidBitsPerSample:wave.BitsPerSample;
        if(valid==0)valid=wave.BitsPerSample;
        var encoding=standard.Encoding==WaveFormatEncoding.IeeeFloat?PcmEncoding.Float:PcmEncoding.SignedInteger;
        if((standard.Encoding!=WaveFormatEncoding.Pcm && standard.Encoding!=WaveFormatEncoding.IeeeFloat) ||
            (encoding==PcmEncoding.Float?wave.BitsPerSample!=32:wave.BitsPerSample is not(16 or 24 or 32)))
            throw new NotSupportedException("设备格式不是支持的 PCM 16/24/32 或 float32。");
        return new(wave.SampleRate,wave.Channels,wave.BitsPerSample,valid,encoding);
    }
    public async ValueTask DisposeAsync(){_stop.Cancel();await _done.Task.ConfigureAwait(false);}
    [DllImport("avrt.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr AvSetMmThreadCharacteristics(string taskName,ref uint taskIndex);
    [DllImport("avrt.dll")]private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
