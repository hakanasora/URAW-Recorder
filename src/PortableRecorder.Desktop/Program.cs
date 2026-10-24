using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PortableRecorder.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
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
        UnhandledException += (_, e) => System.IO.File.WriteAllText(
            System.IO.Path.Combine(AppContext.BaseDirectory,"crash.log"),e.Exception.ToString());
        RequestedTheme = ApplicationTheme.Dark;
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
