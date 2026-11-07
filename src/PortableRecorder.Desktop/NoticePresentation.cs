namespace PortableRecorder.Desktop;

internal static class NoticePresentation
{
    public static string Title(string text)
    {
        if (text.StartsWith("录音继续中：压缩文件出现削波", StringComparison.Ordinal)) return "录音出现削波";
        int end = text.IndexOfAny(['：', '；', '。', '\n', '（']);
        return end > 0 ? text[..end].Trim() : text.Trim();
    }

    public static string Details(params string?[] sections)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section)) continue;
            foreach (var raw in section.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Split only the known monitoring prefix; paths and arbitrary errors remain intact.
                var line = raw.Trim();
                foreach (var prefix in new[] { "共享监看 · RAW 请求已接受；", "共享监看 · 普通输入（仅本次许可）；" })
                    if (line.StartsWith(prefix, StringComparison.Ordinal)) line = line[prefix.Length..];
                var key = line switch
                {
                    "校准尚未生效，请选择有效文件或禁用校准。" or
                    "校准尚未生效，请选择有效文件。" or
                    "校准尚未生效：请选择校准文件，或禁用校准后录制。" => "calibration-not-ready",
                    _ => line
                };
                if (seen.Add(key)) lines.Add(key == "calibration-not-ready"
                    ? "校准尚未生效，请选择有效文件或禁用校准。" : line);
            }
        }
        return string.Join("\n", lines);
    }
}
