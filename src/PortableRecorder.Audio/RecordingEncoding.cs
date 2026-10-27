namespace PortableRecorder.Audio;

public enum RecordingCodec { Wav, Flac, Mp3 }
public sealed record RecordingEncoding(RecordingCodec Codec=RecordingCodec.Wav,int Quality=5)
{
    public string Extension=>Codec switch{RecordingCodec.Wav=>"wav",RecordingCodec.Flac=>"flac",RecordingCodec.Mp3=>"mp3",_=>throw new ArgumentOutOfRangeException(nameof(Codec))};
    public void Validate(AudioFormat format)
    {
        if(!Enum.IsDefined(Codec))throw new ArgumentOutOfRangeException(nameof(Codec));
        if((Codec==RecordingCodec.Flac && Quality is <0 or >8) || (Codec==RecordingCodec.Mp3 && Quality is <0 or >9))
            throw new ArgumentOutOfRangeException(nameof(Quality));
        if(Codec==RecordingCodec.Flac && (format.Channels is <1 or >8 || format.SampleRate is <1 or >655350))
            throw new NotSupportedException("FLAC 支持 1–8 声道，当前输入格式不可用；请选择 WAV。");
        if(Codec==RecordingCodec.Mp3 && (format.Channels is <1 or >2 || !new[]{8000,11025,12000,16000,22050,24000,32000,44100,48000}.Contains(format.SampleRate)))
            throw new NotSupportedException("MP3 仅支持单声道／立体声及 8–48 kHz 的标准采样率。请明确选择兼容的输入格式，或使用 WAV／FLAC；不会自动混音或重采样。");
    }
    public static string EncoderPath=>System.IO.Path.Combine(AppContext.BaseDirectory,"encoders","ffmpeg.exe");
    public IRecordingWriter CreateWriter(string path,AudioFormat format)
    {
        Validate(format);
        return Codec==RecordingCodec.Wav?new FloatWaveWriter(path,format):new CompressedAudioWriter(path,format,this,EncoderPath);
    }
}
