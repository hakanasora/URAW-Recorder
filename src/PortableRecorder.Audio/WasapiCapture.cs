using System.Diagnostics;
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
        catch(Exception openingError)
        {
            try{await capture.DisposeAsync();}
            catch(Exception cleanupError){throw AudioFailure.Combine(openingError,cleanupError);}
            throw;
        }
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
    private readonly object _disposeGate=new();
    private Task? _disposeTask;
    public Task<CaptureInfo> Ready=>_ready.Task;
    public CaptureInfo Info=>_ready.Task.GetAwaiter().GetResult();
    public WasapiCaptureSession(AudioDevice device,bool exclusive,bool raw,long epoch,Func<CapturePacket,bool> submit,Action<Exception> fault,AudioFormat? preferredFormat)
    {
        var thread=new Thread(()=>Run(device,exclusive,raw,epoch,submit,fault,preferredFormat)){IsBackground=true,Name="Portable Recorder WASAPI"};
        thread.SetApartmentState(ApartmentState.MTA);thread.Start();
    }
    private void Run(AudioDevice description,bool exclusive,bool raw,long epoch,Func<CapturePacket,bool> submit,Action<Exception> fault,AudioFormat? preferredFormat)
    {
        AudioClient? client=null;
        IntPtr mmcss=IntPtr.Zero;
        bool started=false;
        Exception? cleanupFailure=null;
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
            int registrationError=mmcss==IntPtr.Zero?Marshal.GetLastWin32Error():0;
            string scheduling="Pro Audio";
            if(mmcss==IntPtr.Zero)
            {
                mmcss=AvSetMmThreadCharacteristics("Audio",ref taskIndex);
                scheduling=mmcss!=IntPtr.Zero?$"Audio fallback (Pro Audio error={registrationError})"
                    :$"managed AboveNormal fallback (Pro Audio error={registrationError}, Audio error={Marshal.GetLastWin32Error()})";
                if(mmcss==IntPtr.Zero)Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
            }
            int bufferFrames=client.BufferSize;
            // Shared events usually deliver one engine period, not the full endpoint buffer.
            // Warm both buckets before Start; do not add work to an active native packet.
            int periodFrames=exclusive?bufferFrames:Math.Clamp((int)Math.Ceiling(format.SampleRate*client.DefaultDevicePeriod/10_000_000d),1,bufferFrames);
            var warm=ArrayPool<byte>.Shared.Rent(checked(bufferFrames*format.BlockAlign));
            var packetWarm=ArrayPool<byte>.Shared.Rent(checked(periodFrames*format.BlockAlign));
            ArrayPool<byte>.Shared.Return(packetWarm);ArrayPool<byte>.Shared.Return(warm);
            var waits=new WaitHandle[]{signal,_stop.Token.WaitHandle};
            var continuity=new CaptureContinuity();
            EndpointVolumeSnapshot? volume=null;string volumeDiagnostic;
            try
            {
                var endpointVolume=device.AudioEndpointVolume;
                float db=endpointVolume.MasterVolumeLevel;
                if(!float.IsFinite(db))throw new NotSupportedException("Non-finite endpoint volume");
                volume=new(db,((int)endpointVolume.HardwareSupport&1)!=0);
                volumeDiagnostic=$"endpointDb={db:F3}; hardwareVolume={volume.HardwareVolume}";
            }
            catch(Exception error)when(error is COMException or NotSupportedException)
            {volumeDiagnostic=$"endpointVolumeUnavailable=0x{error.HResult:X8}";}
            var info=new CaptureInfo(description,format,exclusive,raw,epoch){EndpointVolume=volume,StreamDiagnostics=
                $"bufferFrames={bufferFrames}; bufferMs={bufferFrames*1000d/format.SampleRate:F3}; requestedMs={duration/10000d:F3}; defaultPeriodMs={client.DefaultDevicePeriod/10000d:F3}; scheduling={scheduling}; {volumeDiagnostic}"};
            long lastWake=Stopwatch.GetTimestamp();
            client.Start();
            started=true;
            _ready.TrySetResult(info);
            while(!_stop.IsCancellationRequested)
            {
                int signaled=WaitHandle.WaitAny(waits,3000);
                if(signaled==1)break;
                if(signaled==WaitHandle.WaitTimeout)throw new IOException("采集设备连续 3 秒未提供音频包。");
                long wake=Stopwatch.GetTimestamp();double wakeGapMs=Stopwatch.GetElapsedTime(lastWake,wake).TotalMilliseconds;lastWake=wake;
                // GetNextPacketSize is shared-mode only. Exclusive events supply one buffer.
                do
                {
                    long acquired=Stopwatch.GetTimestamp();
                    var pointer=capture.GetBuffer(out int frames,out var flags,out long position,out long qpc);
                    if(frames==0)break;
                    byte[]? bytes=null;
                    int length=0;
                    try
                    {
                        try
                        {
                            length=checked(frames*format.BlockAlign);
                            bytes=ArrayPool<byte>.Shared.Rent(length);
                            if((flags&AudioClientBufferFlags.Silent)!=0)Array.Clear(bytes,0,length);
                            else Marshal.Copy(pointer,bytes,0,length);
                        }
                        finally{capture.ReleaseBuffer(frames);}
                        double holdMs=Stopwatch.GetElapsedTime(acquired).TotalMilliseconds;
                        var boundary=continuity.Observe(frames,position,(int)flags);
                        var packet=new CapturePacket(bytes!,length,frames,position,
                            (flags&AudioClientBufferFlags.Silent)!=0,boundary.Discontinuous,(int)flags,boundary.Delta,qpc,wakeGapMs,holdMs);
                        // The packet owns the pool rental even if submission fails.
                        bytes=null;
                        try{if(!submit(packet))throw new IOException("采集处理队列溢出，已停止，未静默丢包。");}
                        catch{packet.Dispose();throw;}
                    }
                    finally{if(bytes!=null)ArrayPool<byte>.Shared.Return(bytes);}
                }while(!exclusive && !_stop.IsCancellationRequested && capture.GetNextPacketSize()>0);
            }
        }
        catch(Exception error)
        {
            if(!_ready.TrySetException(error)&&!_stop.IsCancellationRequested)
                try{fault(error);}catch(Exception callbackError){cleanupFailure=callbackError;}
        }
        finally
        {
            if(client!=null)
            {
                if(started)
                    try{client.Stop();}catch(Exception error){cleanupFailure=AudioFailure.Combine(cleanupFailure,error);}
                try{client.Dispose();}catch(Exception error){cleanupFailure=AudioFailure.Combine(cleanupFailure,error);}
            }
            if(mmcss!=IntPtr.Zero)
            {
                try
                {
                    if(!AvRevertMmThreadCharacteristics(mmcss))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法释放音频线程调度注册。");
                }
                catch(Exception error){cleanupFailure=AudioFailure.Combine(cleanupFailure,error);}
            }
            // Every path signals completion; a COM cleanup exception must not escape
            // the native thread or leave DisposeAsync waiting for an unsignalled task.
            if(cleanupFailure==null)_done.TrySetResult();
            else _done.TrySetException(cleanupFailure);
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
    public ValueTask DisposeAsync()
    {
        lock(_disposeGate)return new(_disposeTask??=StopAndReleaseAsync());
    }
    private async Task StopAndReleaseAsync()
    {
        _stop.Cancel();
        try{await _done.Task.ConfigureAwait(false);}
        finally{_stop.Dispose();}
    }
    [DllImport("avrt.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr AvSetMmThreadCharacteristics(string taskName,ref uint taskIndex);
    [DllImport("avrt.dll",SetLastError=true)]private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
