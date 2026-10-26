using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace PortableRecorder.Desktop;

/// <summary>Fixed-size owned window. Only the drawer's region and visual move, never the main HWND.</summary>
internal sealed class SettingsDrawer : IDisposable
{
    private readonly Window _owner;
    private readonly Grid _overlayParent, _panel;
    private readonly Func<bool> _animations;
    private readonly Action _focusPane, _focusMenu;
    private readonly Window _window;
    private readonly Grid _surface = new() { RequestedTheme = ElementTheme.Dark };
    private readonly Grid _overlay = new() { Width = FixedWindowFrame.PaneWidth,
        HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly nint _hwnd, _ownerHwnd;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _progress, _from, _target, _started;
    private int _width, _height;
    private bool _external, _open, _running, _disposed, _suspended;
    private RectInt32 _bounds;

    public SettingsDrawer(Window owner, Grid overlayParent, Grid panel, Func<bool> animations,
        Action focusPane, Action focusMenu)
    {
        _owner = owner; _overlayParent = overlayParent; _panel = panel;
        _animations = animations; _focusPane = focusPane; _focusMenu = focusMenu;
        _ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        _window = new Window { Title = "Portable Recorder - Settings", Content = _surface };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        SetWindowLongPtrW(_hwnd, -8, _ownerHwnd); // GWLP_HWNDPARENT: owned, not child, HWND.
        var app = _window.AppWindow;
        app.IsShownInSwitchers = false;
        if (app.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false;
        }
        _surface.Background = Design.Brush(Design.Inspector);
        _overlayParent.Children.Add(_overlay); Canvas.SetZIndex(_overlay, 10);
        _surface.KeyDown += KeyDown; _overlay.KeyDown += KeyDown;
        app.Closing += (_, e) => { if (!_disposed) { e.Cancel = true; Close(); } };
        owner.AppWindow.Changed += OwnerChanged;
    }

    public bool IsOpen => _open;

    public void Toggle()
    {
        if (_disposed) return;
        if (_open) { Close(); return; }
        _open = true;
        if (!_running && _progress == 0) Attach(HasRoom());
        Position();
        if (_external)
        {
            Draw(); // Install the initial region BEFORE showing the native surface.
            _window.AppWindow.Show(false);
        }
        else _overlay.Visibility = Visibility.Visible;
        Animate(1);
    }

    public void Close()
    {
        if (!_open || _disposed) return;
        _open = false; Animate(0);
    }

    private bool HasRoom()
    {
        var point = new NativePoint(); ClientToScreen(_ownerHwnd, ref point);
        var work = DisplayArea.GetFromWindowId(_owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        return point.X - (int)Math.Round(FixedWindowFrame.PaneWidth * Scale) >= work.X;
    }

    private double Scale => _overlayParent.XamlRoot?.RasterizationScale ?? 1;

    private void Attach(bool external)
    {
        _surface.Children.Clear(); _overlay.Children.Clear();
        _external = external;
        // Match the owner's 32 DIP title strip without adding a second title/caption bar.
        _panel.Margin = external ? new Thickness(0,32,0,0) : new Thickness(0);
        (external ? _surface : _overlay).Children.Add(_panel);
        ElementCompositionPreview.SetIsTranslationEnabled(_panel, true);
        _overlay.Visibility = external ? Visibility.Collapsed : Visibility.Visible;
        StartupLog.Write($"Settings drawer: {(external ? "owned side window" : "overlay (left edge)")}; main client unchanged");
    }

    private void Position()
    {
        var scale = Scale;
        _width = (int)Math.Round(FixedWindowFrame.PaneWidth * scale);
        _height = _owner.AppWindow.ClientSize.Height;
        if (!_external) return;
        var point = new NativePoint(); ClientToScreen(_ownerHwnd, ref point);
        var bounds = new RectInt32(point.X - _width, point.Y, _width, _height);
        if (bounds.X == _bounds.X && bounds.Y == _bounds.Y && bounds.Width == _bounds.Width && bounds.Height == _bounds.Height) return;
        _bounds = bounds;
        _window.AppWindow.MoveAndResize(bounds);
    }

    private void Animate(double target)
    {
        Advance(); _from = _progress; _target = target; _started = _clock.Elapsed.TotalSeconds;
        if (!_animations()) { _progress = target; Draw(); Finish(); return; }
        if (!_running) { _running = true; CompositionTarget.Rendering += Render; }
    }

    private void Advance()
    {
        if (!_running) return;
        var t = Math.Clamp((_clock.Elapsed.TotalSeconds - _started) / .28, 0, 1);
        // Smooth, moderate ease-out. Reversals retain their current presentation position.
        var eased = 1 - Math.Pow(1-t, 3);
        _progress = _from + (_target - _from) * eased;
    }

    private void Render(object? sender, object e)
    {
        if (!_animations()) { _progress = _target; Draw(); Finish(); return; }
        Advance(); Draw();
        if (_clock.Elapsed.TotalSeconds - _started >= .28) { _progress = _target; Draw(); Finish(); }
    }

    private void Draw()
    {
        if (_external)
        {
            var visible = Math.Clamp((int)Math.Round(_width * _progress), 0, _width);
            // A native clipping region prevents a full-width blank rectangle appearing on Show.
            // On success Windows owns this HRGN; on failure we must delete it ourselves.
            var region = CreateRectRgn(_width-visible, 0, _width, _height);
            if (SetWindowRgn(_hwnd, region, true) == 0) DeleteObject(region);
            ElementCompositionPreview.GetElementVisual(_panel).Properties.InsertVector3("Translation", new System.Numerics.Vector3((float)((1-_progress)*FixedWindowFrame.PaneWidth),0,0));
        }
        else
        {
            _overlay.Clip = new RectangleGeometry { Rect = new Rect(0,0,FixedWindowFrame.PaneWidth,_overlayParent.ActualHeight) };
            ElementCompositionPreview.GetElementVisual(_panel).Properties.InsertVector3("Translation", new System.Numerics.Vector3((float)(-(1-_progress)*FixedWindowFrame.PaneWidth),0,0));
        }
    }

    private void Finish()
    {
        CompositionTarget.Rendering -= Render; _running = false;
        if (!_open)
        {
            _window.AppWindow.Hide(); _overlay.Visibility = Visibility.Collapsed;
            if (!_suspended) { _owner.Activate(); _focusMenu(); }
        }
        else if (!_suspended)
        {
            if (_external) _window.Activate();
            _focusPane();
        }
    }

    private void OwnerChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_disposed || (!_open && !_running)) return;
        var minimized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        if (minimized) { _suspended = true; _window.AppWindow.Hide(); return; }
        var resumed = _suspended; _suspended = false;
        if (_external && !HasRoom())
        {
            _window.AppWindow.Hide(); Attach(false);
        }
        Position(); Draw();
        if (resumed && _external) _window.AppWindow.Show(false);
    }

    private void KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { Close(); args.Handled = true; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; CompositionTarget.Rendering -= Render;
        _owner.AppWindow.Changed -= OwnerChanged;
        _surface.Children.Clear(); _overlay.Children.Clear(); _overlayParent.Children.Remove(_overlay);
        _window.Close();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}
