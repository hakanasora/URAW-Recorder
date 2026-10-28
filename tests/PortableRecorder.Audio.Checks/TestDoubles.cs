using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PortableRecorder.Audio;

sealed class FakeFactory:ICaptureFactory
{
    public AudioDevice Device {get;}=new("fake","Deterministic input",true);
    public AudioFormat Format=new(48000,1,32,32,PcmEncoding.Float);
    public bool DenySharedRaw,DenyExclusive,FailDuringOpen;
    public FakeCapture? Current;
    public IReadOnlyList<AudioDevice> Enumerate()=>[Device];
    public AudioFormat? LastRequestedFormat;
    public bool RejectPreferred;
    public Task<InputFormatOptions> QueryFormatsAsync(AudioDevice device)=>Task.FromResult(new InputFormatOptions(Format,
        new[]{Format,new AudioFormat(96000,Format.Channels,24,24,PcmEncoding.SignedInteger)}));
    public Task<IAudioCaptureService> OpenAsync(AudioDevice device,bool exclusive,bool raw,long epoch,Func<CapturePacket,bool> packet,Action<Exception> fault,AudioFormat? preferredFormat=null)
    {
        if(exclusive && DenyExclusive)throw new IOException("Exclusive denied");
        if(!exclusive && raw && DenySharedRaw)throw new RawUnavailableException("RAW unsupported");
        LastRequestedFormat=preferredFormat;
        if(preferredFormat!=null&&RejectPreferred)throw new NotSupportedException("Requested format unavailable");
        Current=new(new(device,preferredFormat??Format,exclusive,raw,epoch),packet,fault);
        if(FailDuringOpen)fault(new IOException("Capture stopped during preparation"));
        return Task.FromResult<IAudioCaptureService>(Current);
    }
    public void Push(float[] samples,bool gap=false)=>Current!.Push(samples,gap);
}
sealed class FakeCapture(CaptureInfo info,Func<CapturePacket,bool> packet,Action<Exception> fault):IAudioCaptureService
{
    public CaptureInfo Info=>info;long _frame;bool _closed;
    public bool Closed=>_closed;
    public void TriggerFault()=>fault(new IOException("Old session fault"));
    public void Push(float[] data,bool gap)
    {
        if(_closed)throw new InvalidOperationException("Closed capture");
        int width=info.Format.Bits/8;var bytes=ArrayPool<byte>.Shared.Rent(data.Length*width);
        if(info.Format.Encoding==PcmEncoding.Float)MemoryMarshal.AsBytes(data.AsSpan()).CopyTo(bytes);
        else for(int i=0;i<data.Length;i++)
        {
            long scale=1L<<(info.Format.ValidBits-1);
            int code=(int)(Math.Clamp((long)Math.Round(data[i]*scale),-scale,scale-1)<<(info.Format.Bits-info.Format.ValidBits));
            var target=bytes.AsSpan(i*width,width);
            if(width==2)BinaryPrimitives.WriteInt16LittleEndian(target,(short)code);
            else if(width==3){target[0]=(byte)code;target[1]=(byte)(code>>8);target[2]=(byte)(code>>16);}
            else BinaryPrimitives.WriteInt32LittleEndian(target,code);
        }
        var block=new CapturePacket(bytes,data.Length*width,data.Length/info.Format.Channels,_frame,false,gap);_frame+=block.Frames;
        if(!packet(block)){block.Dispose();fault(new IOException("queue overflow"));}
    }
    public ValueTask DisposeAsync(){_closed=true;return ValueTask.CompletedTask;}
}

sealed class FailingWriter:IRecordingWriter
{
    public string Path=>"injected-disk-failure.wav";
    public long WrittenFrames {get;private set;}
    public Exception? Failure {get;private set;}
    public bool Reject;
    public bool TryWrite(ReadOnlySpan<float> samples){if(Failure!=null || Reject)return false;WrittenFrames+=samples.Length;return true;}
    public void Fail()=>Failure=new IOException("Injected disk failure");
    public Task CompleteAsync()=>Failure==null?Task.CompletedTask:Task.FromException(Failure);
    public ValueTask DisposeAsync()=>new(CompleteAsync());
}
