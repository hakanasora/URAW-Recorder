using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace PortableRecorder.Desktop;

/// <summary>One persistent XAML overlay. Motion runs on the native compositor; the background stays undimmed.</summary>
internal sealed class SettingsDrawer : IDisposable
{
    private enum DrawerPhase { Closed, Opening, Open, Closing, Disposed }
    private const float Travel = FixedWindowFrame.PaneWidth + 32;
    private readonly Grid _parent, _panel;
    private readonly Grid _layer = new() { Visibility = Visibility.Collapsed,
        TabFocusNavigation = KeyboardNavigationMode.Cycle };
    private readonly Border _dismissSurface = new() { Background = Design.Brush(Colors.Transparent) };
    private readonly Grid _sheet = new() { Width = FixedWindowFrame.PaneWidth,
        HorizontalAlignment = HorizontalAlignment.Left, Translation = new Vector3(-Travel,0,0) };
    private readonly Grid _shadowHost = new() { IsHitTestVisible = false };
    private readonly Func<bool> _animations;
    private readonly Action _focusPane, _focusMenu;
    private Compositor? _compositor;
    private SpriteVisual? _shadowVisual;
    private DropShadow? _shadow;
    private CompositionScopedBatch? _batch;
    private Vector3KeyFrameAnimation? _slide;
    private int _generation;
    private DrawerPhase _phase;

    public SettingsDrawer(Grid parent, Grid panel, Func<bool> animations,
        Action focusPane, Action focusMenu)
    {
        _parent = parent; _panel = panel; _animations = animations;
        _focusPane = focusPane; _focusMenu = focusMenu;
        _panel.Margin = new Thickness(0);
        _layer.Children.Add(_dismissSurface);
        _sheet.Children.Add(_shadowHost);
        _sheet.Children.Add(_panel);
        // One quiet catchlight separates the neutral dark sheet from the instrument surface.
        _sheet.Children.Add(new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Right,
            Background = Design.Brush(MaterialSurface.Tint(Colors.White,18)), IsHitTestVisible = false });
        _layer.Children.Add(_sheet);
        _parent.Children.Add(_layer); Canvas.SetZIndex(_layer,10);
        AutomationProperties.SetName(_panel,"输入与文件设置");
        _dismissSurface.PointerPressed += (_, args) => { Close(); args.Handled = true; };
        _layer.KeyDown += KeyDown;
        _layer.SizeChanged += (_, _) => UpdateClip();
    }

    public bool IsOpen => _phase is DrawerPhase.Opening or DrawerPhase.Open;

    public void ApplyMotionPreference()
    {
        if (_phase is not (DrawerPhase.Opening or DrawerPhase.Closing) || _animations()) return;
        Finish(++_generation);
    }

    public void Toggle()
    {
        if (_phase == DrawerPhase.Disposed) return;
        if (IsOpen) { Close(); return; }
        bool wasClosed = _phase == DrawerPhase.Closed;
        _phase = DrawerPhase.Opening;
        if (wasClosed)
        {
            _layer.Visibility = Visibility.Visible;
            _layer.UpdateLayout();
            UpdateClip();
        }
        Animate();
    }

    public void Close()
    {
        if (!IsOpen) return;
        _phase = DrawerPhase.Closing;
        // Keep keyboard focus inside the sheet until the visual has actually exited.
        Animate();
    }

    private void UpdateClip() => _layer.Clip = new RectangleGeometry {
        Rect = new Rect(0,0,_parent.ActualWidth,_parent.ActualHeight) };

    private void EnsureComposition()
    {
        if (_compositor != null) return;
        _compositor = ElementCompositionPreview.GetElementVisual(_sheet).Compositor;
        // Native rectangular DropShadow behind the sheet, not a painted gradient.
        // The parent clip removes shadow beyond the window; only its right edge remains visible.
        _shadow = _compositor.CreateDropShadow();
        _shadow.Color = Colors.Black; _shadow.BlurRadius = 16;
        _shadow.Opacity = .10f; _shadow.Offset = new Vector3(3,0,0);
        _shadowVisual = _compositor.CreateSpriteVisual();
        _shadowVisual.Shadow = _shadow; _shadowVisual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(_shadowHost,_shadowVisual);
        StartupLog.Write("Settings drawer: single-window overlay; native Composition slide; no dimming; main bounds unchanged");
    }

    private void Animate()
    {
        var generation = ++_generation;
        _batch?.Dispose(); _batch = null;
        EnsureComposition();
        if (!_animations()) { Finish(generation); return; }
        var compositor = _compositor!;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _batch = batch;
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        // Match the installed WinUI SplitView template (Open 200 ms / Close 100 ms).
        slide.Duration = TimeSpan.FromMilliseconds(IsOpen ? 200 : 100);
        slide.InsertExpressionKeyFrame(0,"this.StartingValue");
        var ease = compositor.CreateCubicBezierEasingFunction(new(0,.35f),new(.15f,1));
        slide.InsertKeyFrame(1,new Vector3(IsOpen ? 0 : -Travel,0,0),ease);
        var previousSlide = _slide;
        _slide = slide;
        _sheet.StartAnimation(slide);
        previousSlide?.Dispose();
        batch.Completed += (_, _) => _parent.DispatcherQueue.TryEnqueue(() => Finish(generation));
        batch.End();
    }

    private void Finish(int generation)
    {
        if (generation != _generation || _phase is not (DrawerPhase.Opening or DrawerPhase.Closing)) return;
        bool open = _phase == DrawerPhase.Opening;
        // Commit base values before stopping native animation fill, avoiding a one-frame snap.
        _sheet.Translation = new Vector3(open ? 0 : -Travel,0,0);
        StopSlide();
        _batch?.Dispose(); _batch = null;
        _phase = open ? DrawerPhase.Open : DrawerPhase.Closed;
        if (!open) { _layer.Visibility = Visibility.Collapsed; _focusMenu(); }
        else _focusPane(); // Do not focus a native selector while its presenter is still offscreen.
    }

    private void StopSlide()
    {
        if (_slide == null) return;
        _sheet.StopAnimation(_slide);
        _slide.Dispose();
        _slide = null;
    }

    private void KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { Close(); args.Handled = true; }
    }

    public void Dispose()
    {
        if (_phase == DrawerPhase.Disposed) return;
        _phase = DrawerPhase.Disposed;
        ++_generation;
        StopSlide();
        _batch?.Dispose(); _batch = null;
        ElementCompositionPreview.SetElementChildVisual(_shadowHost,null);
        _shadowVisual?.Dispose(); _shadow?.Dispose();
        _sheet.Children.Clear(); _parent.Children.Remove(_layer);
    }
}
