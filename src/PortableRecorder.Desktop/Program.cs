using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PortableRecorder.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        StartupLog.Write($"Main; base={AppContext.BaseDirectory}");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            StartupLog.Write("Application callback");
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new RecorderApp();
        });
    }
}

public sealed partial class RecorderApp : Application
{
    private Window? _window;
    public RecorderApp()
    {
        StartupLog.Write("App constructor");
        UnhandledException += (_, e) => StartupLog.Write("Unhandled: "+e.Exception);
        RequestedTheme = ApplicationTheme.Dark;
        try { InitializeComponent(); StartupLog.Write("App resources loaded"); }
        catch(Exception error){StartupLog.Write("App resources failed: "+error);throw;}
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupLog.Write("OnLaunched");
        try { _window = new MainWindow(); StartupLog.Write("Window constructed"); _window.Activate(); StartupLog.Write("Window activated"); }
        catch(Exception error){StartupLog.Write("Window failed: "+error);throw;}
    }
}
