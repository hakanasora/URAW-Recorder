namespace PortableRecorder.Desktop;

internal static class StartupLog
{
    private static readonly string Folder=System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PortableRecorder","Logs");
    private static readonly string FileName=System.IO.Path.Combine(Folder,$"startup-{Environment.ProcessId}.log");
    internal static void Write(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Folder);
            System.IO.File.AppendAllText(FileName,$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { /* A diagnostic failure must not mask the original startup failure. */ }
    }
}
