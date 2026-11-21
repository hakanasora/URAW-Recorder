namespace PortableRecorder.Audio;

internal sealed class RecordingStore(CalibrationLibrary calibrations, Action<string> trace)
{
    private object? _calibrationMetadata;
    public string? SummaryOwned { get; private set; }

    public static string DefaultFolder
    {
        get
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrEmpty(desktop))
                desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
            return Path.Combine(desktop, "URAW Recorder Recordings");
        }
    }

    public IRecordingWriter CreateWriter(
        string directory,
        AudioDevice device,
        AudioFormat format,
        RecordingEncoding encoding,
        CalibrationStatus calibration,
        object? profile,
        Func<string, AudioFormat, RecordingEncoding, IRecordingWriter> createWriter)
    {
        string folder = string.IsNullOrWhiteSpace(directory) ? DefaultFolder : Path.GetFullPath(directory);
        Directory.CreateDirectory(folder);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        IRecordingWriter writer;
        for (int suffix = 0; ; suffix++)
        {
            string path = Path.Combine(folder, $"Recording-{stamp}{(suffix == 0 ? "" : "-" + suffix)}.{encoding.Extension}");
            try { writer = createWriter(path, format, encoding); break; }
            catch (IOException error) when (AudioFailure.IsNameCollision(error) && suffix < 99) { }
        }
        SummaryOwned = null;
        _calibrationMetadata = new
        {
            Version = 2,
            Device = device,
            Format = format,
            Encoding = encoding,
            Calibration = calibration,
            Profile = profile,
            Processing = "raw → frequency correction → software gain → monitoring / selected encoder",
            LinearPhase = true,
            FftRule = "nextpow2(Fs*0.34); taps=7*N/8+1; designFFT=4*N; Hamming",
            AbsoluteSpl = false
        };
        return writer;
    }

    public string? SaveCalibrationSummary(IRecordingWriter writer, Exception? error, long unconfirmedDiscontinuities)
    {
        string? temp = null;
        try
        {
            if (!File.Exists(writer.Path) || new FileInfo(writer.Path).Length == 0) return null;
            string sidecar = GetMetadataPath(writer.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
            temp = sidecar + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                System.Text.Json.JsonSerializer.Serialize(file, new
                {
                    Recording = _calibrationMetadata,
                    Frames = writer.WrittenFrames,
                    UnconfirmedDiscontinuities = unconfirmedDiscontinuities,
                    Completed = error == null,
                    Error = error == null ? null : AudioEngine.Friendly(error)
                });
                file.Flush(true);
            }
            File.Move(temp, sidecar, false);
            SummaryOwned = sidecar;
            return null;
        }
        catch (Exception e) { trace("Calibration summary failed: " + e); return e.Message; }
        finally
        {
            try { if (temp != null && File.Exists(temp)) File.Delete(temp); }
            catch (Exception cleanupError) { trace("Calibration summary temporary-file cleanup failed: " + cleanupError); }
        }
    }

    public void ClearOwnedSummaryIfMatch(string deletedPath)
    {
        if (string.Equals(SummaryOwned, deletedPath, StringComparison.OrdinalIgnoreCase))
            SummaryOwned = null;
    }

    public string GetMetadataPath(string recordingPath)
    {
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(recordingPath).ToUpperInvariant())));
        return Path.Combine(calibrations.Folder, "RecordingHistory", key + ".json");
    }
}
