using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace PortableRecorder.Desktop;

/// <summary>Keep the native window out of composition until its first content frame is submitted.</summary>
internal sealed class StartupPresentation
{
    private readonly Window _window;
    private readonly nint _handle;
    private readonly DispatcherTimer _fallback = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _cloaked, _queued, _closed;

    public StartupPresentation(Window window)
    {
        _window = window;
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        int enabled = 1;
        int result = DwmSetWindowAttribute(_handle, 13 /* DWMWA_CLOAK */, ref enabled, sizeof(int));
        _cloaked = result >= 0;
        StartupLog.Write($"Startup cloak: 0x{result:X8}");
        _fallback.Tick += (_, _) => Reveal("first-frame timeout");
        window.Closed += (_, _) => { _closed = true; _cloaked = false; _fallback.Stop(); };
        if (_cloaked) _fallback.Start();
    }

    public void FrameSubmitted()
    {
        if (_closed || !_cloaked || _queued) return;
        _queued = true;
        // Rendering callbacks run before XAML submits its scene. Reveal on the next idle turn,
        // after both the XAML scene and the monitor swap chain have had a chance to commit.
        if (!_window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Reveal("first content frame")))
            Reveal("dispatcher unavailable");
    }

    private void Reveal(string reason)
    {
        _fallback.Stop();
        if (_closed || !_cloaked) return;
        int disabled = 0;
        int result = DwmSetWindowAttribute(_handle, 13, ref disabled, sizeof(int));
        if (result < 0)
        {
            // Do not leave an activated application invisible if the compositor rejects the request.
            StartupLog.Write($"Startup reveal failed: 0x{result:X8}");
            _window.Close();
            return;
        }
        _cloaked = false;
        StartupLog.Write("Window revealed: " + reason);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
