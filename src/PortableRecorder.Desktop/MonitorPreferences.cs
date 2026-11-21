using System.Text.Json;
using PortableRecorder.Audio;

namespace PortableRecorder.Desktop;

internal static class MonitorPreferences
{
    internal const double DefaultGainDb = -30;
    private const int MaximumBytes = 64 * 1024;
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortableRecorder", "recording-monitor.json");

    public static double Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return DefaultGainDb;
            using var json = JsonDocument.Parse(AtomicFile.ReadTextBounded(path, MaximumBytes, "监听音量偏好文件过大。"));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("MonitorGainDb", out var value) ||
                value.ValueKind != JsonValueKind.Number)
                throw new InvalidDataException("监听音量偏好缺失或无效。");
            double db = value.GetDouble();
            if (!double.IsFinite(db) || db is < -60 or > 60)
                throw new InvalidDataException("监听音量超出有效范围。");
            double normalized = Math.Round(db, 1);
            return Math.Abs(normalized) < .05 ? 0 : normalized;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or InvalidDataException)
        {
            return DefaultGainDb;
        }
    }

    public static void Save(double monitorGainDb, string? path = null)
    {
        if (!double.IsFinite(monitorGainDb)) throw new ArgumentOutOfRangeException(nameof(monitorGainDb));
        double normalized = Math.Round(Math.Clamp(monitorGainDb, -60, 60), 1);
        if (Math.Abs(normalized) < .05) normalized = 0;
        AtomicFile.WriteAllBytes(path ?? DefaultPath, JsonSerializer.SerializeToUtf8Bytes(new { MonitorGainDb = normalized }), MaximumBytes);
    }
}
