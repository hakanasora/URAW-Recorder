using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace PortableRecorder.Desktop;

/// <summary>Two absolute client sizes. Never derives a new size from transient XAML measurements.</summary>
internal sealed class FixedWindowFrame(Window window, Func<double> scale)
{
    public const int MainWidth = 820;
    public const int ClientHeight = 416;
    public const int PaneWidth = 264;
    private bool _expanded;
    private bool _initialized;
    private int _panePixels;
    private double _dpiScale;

    public void Initialize()
    {
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
        SetExpanded(false, true);
    }

    public void RefreshDpi()
    {
        if (Math.Abs(scale() - _dpiScale) > .001) SetExpanded(_expanded, true);
    }

    public void SetExpanded(bool expanded, bool force = false)
    {
        var dpi = scale();
        if (_initialized && expanded == _expanded && !force && Math.Abs(dpi - _dpiScale) < .001) return;
        var app = window.AppWindow;
        // Preserve the main view's screen origin, adding/removing space on its left.
        var mainLeft = app.Position.X + (_expanded ? _panePixels : 0);
        var panePixels = (int)Math.Round(PaneWidth * dpi);
        var clientWidth = (int)Math.Round(MainWidth * dpi) + (expanded ? panePixels : 0);
        var clientHeight = (int)Math.Round(ClientHeight * dpi);
        var frameWidth = app.Size.Width - app.ClientSize.Width;
        var frameHeight = app.Size.Height - app.ClientSize.Height;
        var left = mainLeft - (expanded ? panePixels : 0);
        if (expanded)
        {
            var workArea = DisplayArea.GetFromWindowId(app.Id, DisplayAreaFallback.Nearest).WorkArea;
            left = Math.Max(left, workArea.X);
        }
        var bounds = new RectInt32(
            left, app.Position.Y,
            clientWidth + frameWidth, clientHeight + frameHeight);
        _expanded = expanded;
        _panePixels = panePixels;
        _dpiScale = dpi;
        _initialized = true;
        app.MoveAndResize(bounds);
    }
}
