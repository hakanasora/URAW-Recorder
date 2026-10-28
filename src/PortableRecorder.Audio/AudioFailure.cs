namespace PortableRecorder.Audio;

// Preserve the first fault and every cleanup fault without allowing cleanup to mask it.
internal static class AudioFailure
{
    public static Exception Combine(Exception? first,Exception next)
    {
        if(first==null)return next;
        if(ReferenceEquals(first,next))return first;
        return new AggregateException("音频操作及收尾发生多个错误。",first,next);
    }

    public static string Describe(Exception error)
    {
        if(error is AggregateException aggregate)
            return string.Join("；",aggregate.InnerExceptions.Select(Describe).Distinct(StringComparer.Ordinal));

        string message=error.HResult switch
        {
            unchecked((int)0x8889000A)=>"输入设备忙，可能正被其他程序独占。",
            unchecked((int)0x8889000E)=>"Windows 或驱动已禁止此设备的独占模式。",
            unchecked((int)0x88890004)=>"输入设备已断开或被系统重新配置。",
            unchecked((int)0x88890010)=>"Windows 音频服务未运行。",
            unchecked((int)0x80070005)=>"无法访问输入或文件，请检查 Windows 麦克风隐私权限及路径权限。",
            _=>error.Message
        };
        if(error.InnerException!=null)
        {
            string detail=Describe(error.InnerException);
            if(!message.Contains(detail,StringComparison.Ordinal))message+=" 原因："+detail;
        }
        return message;
    }

    public static bool IsNameCollision(IOException error)
        =>error.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7);
}
