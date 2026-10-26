using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PortableRecorder.Audio;

public sealed record CalibrationPoint(double FrequencyHz,double ResponseDb,double? PhaseDegrees=null);
public sealed record CalibrationProfile(string Id,string Name,CalibrationPoint[] Points,string[] Headers,
    string? SerialNumber,double? SensitivityDb,string? SensitivityKind,string? Direction,string? UnsupportedReason)
{
    public const int MaximumBytes=2*1024*1024;
    public double MinimumHz=>Points[0].FrequencyHz;
    public double MaximumHz=>Points[^1].FrequencyHz;
    public string CapabilityNotice=>
        (SensitivityDb.HasValue?"检测到灵敏度，绝对声压未启用：缺少经验证的设备／输入增益依据。":"文件未提供灵敏度：未启用绝对声压。")+
        "\n未进行相位恢复、采样时钟或音高基准标定；音高检测使用校准后样本。";

    public static CalibrationProfile Parse(byte[] bytes,string name)
    {
        if(bytes.Length>MaximumBytes)throw new InvalidDataException("校准文件超过 2 MiB。");
        string text;
        try
        {
            if(bytes.AsSpan().StartsWith(new byte[]{255,254}))text=new UnicodeEncoding(false,true,true).GetString(bytes,2,bytes.Length-2);
            else if(bytes.AsSpan().StartsWith(new byte[]{254,255}))text=new UnicodeEncoding(true,true,true).GetString(bytes,2,bytes.Length-2);
            else text=new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch(DecoderFallbackException e){throw new InvalidDataException("校准文件须为 UTF-8、ASCII 或带 BOM 的 UTF-16。",e);}
        var points=new List<CalibrationPoint>();var headers=new List<string>();
        string? serial=null,direction=null,kind=null;double? sensitivity=null;int lineNo=0;
        foreach(var original in text.Split('\n'))
        {
            lineNo++;string line=original.Trim().Replace('\u2212','-');
            if(line.Length==0)continue;
            if(line.Contains('\0'))throw Error(lineNo,"文件包含二进制空字符");
            if(line.Length>16384)throw Error(lineNo,"单行过长");
            if(line.Contains("<html",StringComparison.OrdinalIgnoreCase)||line.Contains("<!doctype",StringComparison.OrdinalIgnoreCase)||line.Contains("Unable to locate calibration",StringComparison.OrdinalIgnoreCase))
                throw Error(lineNo,"文件是网页／下载错误信息，不是校准数据");
            bool commentHeader=line[0] is '#' or ';' || line.StartsWith("//");
            bool numeric=!commentHeader&&(char.IsDigit(line[0])||line[0] is '+' or '-' or '.'||line.StartsWith("NaN",StringComparison.OrdinalIgnoreCase)||line.StartsWith("Infinity",StringComparison.OrdinalIgnoreCase));
            if(!numeric)
            {
                if(points.Count>0&&!commentHeader)throw Error(lineNo,"数据开始后出现无法解释的内容");
                headers.Add(line);
                var sm=Regex.Match(line,@"(?:Sens\s*Factor\s*=|Sensitivity\s+)([+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)\s*dB(?:FS)?",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
                if(line.Contains("Sens Factor",StringComparison.OrdinalIgnoreCase)&&!sm.Success)throw Error(lineNo,"无法解释 Sens Factor 灵敏度字段");
                if(sm.Success)
                {
                    double value=Number(sm.Groups[1].Value,lineNo);
                    if(sensitivity.HasValue&&Math.Abs(value-sensitivity.Value)>1e-9)throw Error(lineNo,"灵敏度头部互相冲突");
                    sensitivity=value;kind=line.Contains("Sens",StringComparison.OrdinalIgnoreCase)&&line.Contains("Factor",StringComparison.OrdinalIgnoreCase)?"Sens Factor":"Sensitivity dBFS";
                }
                var sn=Regex.Match(line,@"SERNO\s*:\s*([\w-]+)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
                if(sn.Success)
                {
                    if(serial!=null&&serial!=sn.Groups[1].Value)throw Error(lineNo,"序列号头部互相冲突");
                    serial=sn.Groups[1].Value;
                }
                if(line.Contains("90-degree",StringComparison.OrdinalIgnoreCase)||line.Contains("90deg",StringComparison.OrdinalIgnoreCase))direction="90°（文件说明）";
                continue;
            }
            int comment=line.IndexOfAny(['#',';']);if(comment>=0)line=line[..comment].Trim();
            var fields=line.Contains(',')?line.Split(',').Select(f=>f.Trim()).ToArray():Regex.Split(line,@"\s+",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
            if(fields.Length is not (2 or 3))throw Error(lineNo,"每行必须为频率、响应 dB，以及可选相位三列");
            double frequency=Number(fields[0],lineNo),response=Number(fields[1],lineNo);
            double? phase=fields.Length==3?Number(fields[2],lineNo):null;
            if(frequency<=0)throw Error(lineNo,"频率必须大于 0 Hz");
            if(Math.Abs(response)>200)throw Error(lineNo,"响应超过支持范围 ±200 dB");
            if(points.Count>0&&frequency<=points[^1].FrequencyHz)throw Error(lineNo,"频率必须严格递增，不能重复或倒序");
            points.Add(new(frequency,response,phase));
            if(points.Count>100000)throw Error(lineNo,"数据点超过 100,000 个");
        }
        if(points.Count<2)throw new InvalidDataException("校准文件至少需要两个有效频点。");
        string? unsupported=points.Any(p=>(p.PhaseDegrees??0)!=0)?"文件包含非零相位，首版暂不支持，未启用此校准。":
            points.Any(p=>p.ResponseDb< -24)?"所需频响提升超过 +24 dB，未启用；不会自动截断补偿量。":null;
        return new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),Path.GetFileName(name),points.ToArray(),headers.ToArray(),serial,sensitivity,kind,direction,unsupported);
    }
    private static double Number(string token,int line)
    {
        if(!double.TryParse(token,NumberStyles.Float,CultureInfo.InvariantCulture,out double v)||!double.IsFinite(v))throw Error(line,"数字损坏或为非有限值："+token);
        return v;
    }
    private static InvalidDataException Error(int line,string reason)=>new($"校准文件第 {line} 行：{reason}。");
}

public sealed record CalibrationStatus(string? ProfileId,string Name,bool Applied,int Channels,int LatencyFrames,int SampleRate,
    long Revision,string Notice,double MaximumFitErrorDb=0,double MinimumHz=0,double MaximumHz=0,int FirTaps=1,int ConvolutionFft=1)
{
    public double LatencyMs=>SampleRate>0?LatencyFrames*1000d/SampleRate:0;
    public static CalibrationStatus Off(int channels=0,int rate=0,long revision=0,string? notice=null)=>
        new(null,"不使用校准",false,channels,0,rate,revision,notice??"未应用校准；保留原始频响。未启用绝对声压。");
}


