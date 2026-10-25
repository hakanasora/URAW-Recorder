using System.Buffers;
namespace PortableRecorder.Audio;

public enum RecordingMode { Exclusive, Shared }
public enum AudioRunState { Stopped, Opening, Monitoring, Preparing, Recording, Paused, Finalizing, Faulted }
public enum PcmEncoding { SignedInteger, Float }
public sealed record AudioDevice(string Id,string Name,bool IsDefault=false);
public sealed record AudioFormat(int SampleRate,int Channels,int Bits,int ValidBits,PcmEncoding Encoding)
{
    public int BlockAlign=>Channels*(Bits/8);
    public string Description=>$"{SampleRate/1000d:0.###} kHz \u00b7 {ValidBits} bit{(Encoding==PcmEncoding.Float?" float":"")} \u00b7 {Channels} \u58f0\u9053";
}
public sealed record CaptureInfo(AudioDevice Device,AudioFormat Format,bool Exclusive,bool Raw,long Epoch);
public sealed record SpectrumFrame(double[] FrequenciesHz,float[] Dbfs,int FftSize,long EndFrame);
public sealed record AudioSnapshot(long Epoch,long FramePosition,int SampleRate,int Channels,bool Valid,
    double RmsDbfs,double SourcePeakDbfs,double OutputPeakDbfs,double? HeldPeakDbfs,
    double VuLeft,double VuRight,double? FrequencyHz,double PitchClarity,SpectrumFrame? Spectrum,long PitchFramePosition=0,bool InputFullScale=false,bool OutputOverload=false,int Segment=0,int RecoveredDiscontinuities=0)
{
    public double Time=>SampleRate>0?FramePosition/(double)SampleRate:0;
    public static AudioSnapshot Empty(long epoch=0)=>new(epoch,0,0,0,false,double.NaN,double.NaN,double.NaN,null,
        double.NegativeInfinity,double.NegativeInfinity,null,0,null);
}
public sealed record AudioStatus(AudioRunState State,CaptureInfo? Capture,string Message,string? FilePath=null,bool Partial=false);
public sealed class RawUnavailableException(string message,Exception? inner=null):Exception(message,inner);
public sealed class CaptureGapException(string detail):IOException("录音期间音频数据不连续，本次录音已停止；这不代表麦克风已断开。"+detail);
public sealed class CapturePacket(byte[] data,int length,int frames,long position,bool silent,bool discontinuity,
    int nativeFlags=0,long? positionDelta=null,long qpc100ns=0):IDisposable
{
    public byte[] Data {get;}=data;
    public int Length {get;}=length;
    public int Frames {get;}=frames;
    public long Position {get;}=position;
    public bool Silent {get;}=silent;
    public bool Discontinuity {get;}=discontinuity;
    public int NativeFlags {get;}=nativeFlags;
    public long? PositionDelta {get;}=positionDelta;
    public long Qpc100ns {get;}=qpc100ns;
    public void Dispose()=>ArrayPool<byte>.Shared.Return(Data);
}
public interface IAudioCaptureService:IAsyncDisposable { CaptureInfo Info {get;} }
public interface ICaptureFactory
{
    IReadOnlyList<AudioDevice> Enumerate();
    Task<IAudioCaptureService> OpenAsync(AudioDevice device,bool exclusive,bool raw,long epoch,
        Func<CapturePacket,bool> packet,Action<Exception> fault);
}
public interface ICalibrationStage { void Process(Span<float> samples,AudioFormat format); }
public sealed class IdentityCalibration:ICalibrationStage { public void Process(Span<float> samples,AudioFormat format) { } }
public interface IAudioAnalyzer:IDisposable
{
    AudioSnapshot Latest {get;}
    void Process(ReadOnlySpan<float> samples,double sourcePeak,long endFrame);
    void ResetPeak();
}
public interface IRecordingWriter:IAsyncDisposable
{
    string Path {get;}
    long WrittenFrames {get;}
    Exception? Failure {get;}
    bool TryWrite(ReadOnlySpan<float> samples);
    Task CompleteAsync();
}
