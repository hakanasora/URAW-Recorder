using System.Text.Json;
using PortableRecorder.Audio;

namespace PortableRecorder.Desktop;

/// <summary>Remembers the user's destination without creating or probing that destination.</summary>
internal static class OutputFolderPreferences
{
    private const int MaximumBytes=64*1024;
    private static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PortableRecorder","output-folder.json");

    public static string Load(out string? warning,string? path=null)
    {
        warning=null;path??=DefaultPath;
        try
        {
            if(!File.Exists(path)&&!Directory.Exists(path))return "";
            using var json=JsonDocument.Parse(AtomicFile.ReadTextBounded(path,MaximumBytes,"保存路径偏好文件过大。"));
            if(json.RootElement.ValueKind!=JsonValueKind.Object||
                !json.RootElement.TryGetProperty("OutputFolder",out var folder)||folder.ValueKind!=JsonValueKind.String)
                throw new InvalidDataException("保存路径偏好缺失或无效。");
            return Normalize(folder.GetString()!);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or NotSupportedException)
        {warning="保存路径偏好不可用，当前使用默认文件夹："+error.Message;return "";}
    }

    public static void Save(string folder,string? path=null)
        =>AtomicFile.WriteAllBytes(path??DefaultPath,JsonSerializer.SerializeToUtf8Bytes(new{OutputFolder=Normalize(folder)}),MaximumBytes);

    private static string Normalize(string folder)
    {
        if(string.IsNullOrWhiteSpace(folder))return "";
        folder=folder.Trim();
        if(!Path.IsPathFullyQualified(folder)||folder.IndexOfAny(Path.GetInvalidPathChars())>=0)
            throw new ArgumentException("请输入完整的保存文件夹路径。");
        return Path.GetFullPath(folder);
    }
}
