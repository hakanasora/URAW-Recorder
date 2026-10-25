using System.Runtime.InteropServices;
namespace PortableRecorder.Desktop;

internal sealed record DisplayRefreshInfo(string Device,double? Hertz,string Source);
internal static class DisplayRefreshRate
{
    // SDK DISPLAYCONFIG_PATH_INFO / MODE_INFO / SOURCE_DEVICE_NAME sizes on Win32 and x64.
    internal static bool NativeLayoutsValid=>Marshal.SizeOf<PathInfo>()==72 && Marshal.SizeOf<ModeInfo>()==64 && Marshal.SizeOf<SourceName>()==84 && Marshal.SizeOf<MonitorInfo>()==104;
    public static DisplayRefreshInfo Read(IntPtr window)
    {
        var monitor=new MonitorInfo{Size=104,Device=""};
        if(!GetMonitorInfoW(MonitorFromWindow(window,2),ref monitor))return new("unknown",null,"compositor fallback");
        for(int retry=0;retry<3;retry++)
        {
            if(GetDisplayConfigBufferSizes(2,out uint paths,out uint modes)!=0 || paths==0 || paths>256 || modes>4096)break;
            var pathArray=new PathInfo[paths];var modeArray=new ModeInfo[Math.Max(1,modes)];
            int error=QueryDisplayConfig(2,ref paths,pathArray,ref modes,modeArray,IntPtr.Zero);
            if(error==122)continue; // topology changed between sizing and reading
            if(error!=0)break;
            for(int i=0;i<paths;i++)
            {
                var path=pathArray[i];
                var source=new SourceName{Type=1,Size=84,Adapter=path.Source.Adapter,Id=path.Source.Id,Device=""};
                if(DisplayConfigGetDeviceInfo(ref source)!=0 || source.Device!=monitor.Device)continue;
                if(path.Target.Denominator>0 && path.Target.Numerator>0)
                    return new(monitor.Device,path.Target.Numerator/(double)path.Target.Denominator,"QueryDisplayConfig active path");
            }
            break;
        }
        var mode=new DeviceMode{Size=220};
        if(EnumDisplaySettingsExW(monitor.Device,-1,ref mode,0) && mode.Frequency>1)
            return new(monitor.Device,mode.Frequency,"EnumDisplaySettingsEx current settings");
        return new(monitor.Device,null,"compositor fallback");
    }
    [StructLayout(LayoutKind.Sequential)]private struct Luid{public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)]private struct SourceInfo{public Luid Adapter;public uint Id,ModeIndex,Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct TargetInfo
    {public Luid Adapter;public uint Id,ModeIndex,Technology,Rotation,Scaling,Numerator,Denominator,Scanline;public int Available;public uint Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct PathInfo{public SourceInfo Source;public TargetInfo Target;public uint Flags;}
    [StructLayout(LayoutKind.Explicit,Size=64)]private struct ModeInfo{[FieldOffset(0)]public uint Type;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct SourceName
    {public uint Type,Size;public Luid Adapter;public uint Id;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Device;}
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct MonitorInfo
    {public uint Size;public Rect Monitor,Work;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Device;}
    // Only the fields we read/write are projected; the complete native DEVMODEW occupies 220 bytes.
    [StructLayout(LayoutKind.Explicit,Size=220)]private struct DeviceMode
    {[FieldOffset(68)]public ushort Size;[FieldOffset(184)]public uint Frequency;}
    [DllImport("user32.dll")]private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetMonitorInfoW(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")]private static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
    [DllImport("user32.dll")]private static extern int QueryDisplayConfig(uint flags,ref uint paths,[Out]PathInfo[] pathArray,ref uint modes,[Out]ModeInfo[] modeArray,IntPtr topology);
    [DllImport("user32.dll")]private static extern int DisplayConfigGetDeviceInfo(ref SourceName name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool EnumDisplaySettingsExW(string device,int mode,ref DeviceMode data,uint flags);
}
