using System.Buffers.Binary;
namespace PortableRecorder.Audio;

public static class PcmDecoder
{
    public static void Decode(ReadOnlySpan<byte> bytes,Span<float> samples,AudioFormat format,bool silent=false)
    {
        if(silent){samples.Clear();return;}
        int size=format.Bits/8;
        if(bytes.Length!=samples.Length*size)throw new InvalidDataException("PCM 包未按声道帧对齐。");
        for(int i=0;i<samples.Length;i++)
        {
            var b=bytes.Slice(i*size,size);
            float value;
            if(format.Encoding==PcmEncoding.Float && format.Bits==32)
                value=BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b));
            else if(format.Encoding==PcmEncoding.SignedInteger)
                value=format.Bits switch
                {
                    16=>BinaryPrimitives.ReadInt16LittleEndian(b)/32768f,
                    24=>((b[0]|b[1]<<8|b[2]<<16)<<8>>8)/8388608f,
                    32=>Decode32(BinaryPrimitives.ReadInt32LittleEndian(b),format.ValidBits),
                    _=>throw new NotSupportedException("仅支持 PCM 16/24/32 和 float32。")
                };
            else throw new NotSupportedException("不支持的采集格式。");
            if(!float.IsFinite(value))throw new InvalidDataException("采集数据包含非有限浮点值。");
            samples[i]=value;
        }
    }
    private static float Decode32(int code,int validBits)
    {
        // WAVEFORMATEXTENSIBLE valid bits are left-aligned in the PCM container.
        int shift=32-validBits;
        return (float)((code>>shift)/Math.Pow(2,validBits-1));
    }
}
