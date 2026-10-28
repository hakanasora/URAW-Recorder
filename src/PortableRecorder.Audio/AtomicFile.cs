using System.Text;

namespace PortableRecorder.Audio;

/// <summary>Small file operations shared by preferences and the calibration catalog.</summary>
public static class AtomicFile
{
    public static byte[] ReadBounded(string path,int maximumBytes,string limitMessage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length>maximumBytes)throw new InvalidDataException(limitMessage);
        var bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);return bytes;
    }

    public static string ReadTextBounded(string path,int maximumBytes,string limitMessage)
    {
        using var memory=new MemoryStream(ReadBounded(path,maximumBytes,limitMessage),writable:false);
        using var reader=new StreamReader(memory,Encoding.UTF8,detectEncodingFromByteOrderMarks:true);
        return reader.ReadToEnd();
    }

    public static void WriteAllBytes(string path,ReadOnlySpan<byte> bytes,int maximumBytes=int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if(bytes.Length>maximumBytes)throw new InvalidDataException("保存内容超过文件大小限制。");
        path=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        Exception? failure=null;
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {stream.Write(bytes);stream.Flush(flushToDisk:true);}
            File.Move(temporary,path,overwrite:true);
        }
        catch(Exception error){failure=error;throw;}
        finally
        {
            try{File.Delete(temporary);}
            catch(Exception cleanup) when(failure!=null&&cleanup is IOException or UnauthorizedAccessException)
            {
                // The original write/rename failure remains the exception observed by the caller.
                failure.Data["AtomicFile.CleanupFailure"]=cleanup.Message;
            }
        }
    }
}
