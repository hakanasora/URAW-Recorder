using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace PortableRecorder.Desktop;

/// <summary>The main client never changes size when settings open or close.</summary>
internal sealed class FixedWindowFrame(Window window, Func<double> scale)
{
    public const int MainWidth = 820;
    public const int ClientHeight = 416;
    public const int PaneWidth = 264;
    private bool _initialized;
    private double _dpiScale;

    public void Initialize()
    {
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
        ApplySize();
    }

    public void RefreshDpi()
    {
        if (Math.Abs(scale() - _dpiScale) > .001) ApplySize();
    }

    private void ApplySize()
    {
        var dpi = scale();
        if (_initialized && Math.Abs(dpi - _dpiScale) < .001) return;
        var app = window.AppWindow;
        var clientWidth = (int)Math.Round(MainWidth * dpi);
        var clientHeight = (int)Math.Round(ClientHeight * dpi);
        var frameWidth = app.Size.Width - app.ClientSize.Width;
        var frameHeight = app.Size.Height - app.ClientSize.Height;
        var bounds = new RectInt32(
            app.Position.X, app.Position.Y,
            clientWidth + frameWidth, clientHeight + frameHeight);
        _dpiScale = dpi;
        _initialized = true;
        app.MoveAndResize(bounds);
    }
}
