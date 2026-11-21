using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace PortableRecorder.Desktop;

public sealed class RecordingMonitorControl : Grid
{
    private enum RevealPhase { Collapsed, Opening, Open, Closing }

    private const double KnobMarginLeft = 8;
    private readonly ShellViewModel _state;
    private readonly Button _toggle;
    private readonly FontIcon _icon = Design.Icon("\uE7F6", 15, Design.Muted);
    private readonly Border _revealHost;
    private readonly RectangleGeometry _revealClip = new();
    private readonly GainControl _knob;
    private readonly NativeTween _reveal;
    private RevealPhase _phase = RevealPhase.Collapsed;
    private bool _hovering;
    private bool _pressed;
    private bool _toggling;
    private bool? _lastEnabled;

    private double RevealedWidth => KnobMarginLeft + _knob.VisualRightEdge + 4;

    public RecordingMonitorControl(ShellViewModel state, Func<bool> animationsEnabled, Func<bool, Task> onToggleRequested)
    {
        _state = state;
        Height = 44;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        ColumnDefinitions.Add(new() { Width = new GridLength(28) });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });

        _toggle = new Button
        {
            Content = _icon,
            Width = 28,
            Height = 28,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Background = Design.Brush(Colors.Transparent),
            BorderBrush = Design.Brush(Colors.Transparent),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TabIndex = 4
        };
        _toggle.Template = (ControlTemplate)XamlReader.Load("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="{TemplateBinding CornerRadius}" Padding="{TemplateBinding Padding}">
                <ContentPresenter Content="{TemplateBinding Content}" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
              </Border>
            </ControlTemplate>
            """);
        _toggle.PointerEntered += (_, _) => { _hovering = true; ApplyToggleVisual(); };
        _toggle.PointerExited += (_, _) => { _hovering = false; _pressed = false; ApplyToggleVisual(); };
        _toggle.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (!args.GetCurrentPoint(_toggle).Properties.IsLeftButtonPressed) return;
            _pressed = true;
            ApplyToggleVisual();
        }), true);
        _toggle.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            if (!_pressed) return;
            _pressed = false;
            ApplyToggleVisual();
        }), true);
        _toggle.PointerCanceled += (_, _) => { _hovering = false; _pressed = false; ApplyToggleVisual(); };
        _toggle.PointerCaptureLost += (_, _) => { _pressed = false; ApplyToggleVisual(); };
        _toggle.Click += async (_, _) =>
        {
            if (_toggling) return;
            _toggling = true;
            try
            {
                await onToggleRequested(!_state.RecordingMonitorEnabled);
            }
            finally
            {
                _toggling = false;
                Refresh(animate: true);
            }
        };

        _knob = new GainControl(new GainControlOptions(
            "监听音量",
            "输入监听音量，单位 dB，范围负 60 至正 60",
            "单击数字输入；旋钮上拖增加、下拖衰减；离开 0 dB 后滚轮可调；Shift 精调；双击旋钮归零",
            () => _state.RecordingMonitorDb,
            db => _state.SetRecordingMonitorGain(db),
            UseAccentOnNonZero: false,
            OnCommitted: () => _state.SaveRecordingMonitorPreferences()))
        {
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        _revealHost = new Border
        {
            Width = 0,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Clip = _revealClip,
            Child = _knob
        };

        _reveal = new NativeTween(
            animationsEnabled,
            w =>
            {
                double clamped = Math.Max(0, w);
                _revealHost.Width = clamped;
                _revealClip.Rect = new Rect(0, 0, clamped, 44);
            },
            milliseconds: 260,
            x1: .22, y1: 1, x2: .36, y2: 1,
            completed: CompleteReveal);

        Children.Add(_toggle);
        Children.Add(_revealHost);
        Grid.SetColumn(_revealHost, 1);
        Refresh(animate: false);
    }

    public void SetAccent(Color color) => _knob.SetAccent(color);
    public void CommitIfOutside(PointerRoutedEventArgs args) => _knob.CommitIfOutside(args);
    public void CancelEdit() => _knob.CancelInteraction();
    public void ReleaseResources() => _reveal.Stop();

    public void Refresh(bool animate = false)
    {
        bool enabled = _state.RecordingMonitorEnabled;
        _knob.Refresh();
        if (_lastEnabled == enabled) return;
        _lastEnabled = enabled;
        ApplyToggleVisual();

        AutomationProperties.SetName(_toggle, enabled ? "关闭监听" : "开启监听");
        ToolTipService.SetToolTip(_toggle, enabled ? "正在监听，点击关闭" : "开启录制监听");

        if (enabled) OpenReveal(animate);
        else CloseReveal(animate);
    }

    private void OpenReveal(bool animate)
    {
        if (!animate)
        {
            _phase = RevealPhase.Open;
            _reveal.Stop();
            _revealHost.Clip = null;
            _revealHost.Width = double.NaN;
            _revealHost.Visibility = Visibility.Visible;
            return;
        }

        if (_phase == RevealPhase.Open) return;
        if (_phase == RevealPhase.Collapsed)
        {
            _revealClip.Rect = new Rect(0, 0, 0, 44);
            _revealHost.Clip = _revealClip;
            _revealHost.Width = double.NaN;
            _revealHost.Visibility = Visibility.Visible;
            UpdateLayout();
            double target = RevealedWidth;
            _reveal.Jump(0);
            _phase = RevealPhase.Opening;
            _reveal.To(target);
            return;
        }

        _phase = RevealPhase.Opening;
        _reveal.To(RevealedWidth);
    }

    private void CloseReveal(bool animate)
    {
        _knob.CancelInteraction();
        if (!animate || _phase == RevealPhase.Collapsed)
        {
            _phase = RevealPhase.Collapsed;
            _reveal.Jump(0);
            _revealHost.Clip = _revealClip;
            _revealHost.Visibility = Visibility.Collapsed;
            return;
        }

        double startWidth = _phase == RevealPhase.Open
            ? Math.Max(_revealHost.ActualWidth, RevealedWidth)
            : Math.Max(0, _reveal.Current);
        _phase = RevealPhase.Closing;
        _revealHost.Clip = _revealClip;
        _reveal.Jump(startWidth);
        _reveal.To(0);
    }

    private void CompleteReveal()
    {
        switch (_phase)
        {
            case RevealPhase.Opening:
                _phase = RevealPhase.Open;
                _revealHost.Width = double.NaN;
                _revealHost.Clip = null;
                break;
            case RevealPhase.Closing:
                _phase = RevealPhase.Collapsed;
                _revealHost.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void ApplyToggleVisual()
    {
        var colors = RecordingMonitorPresentation.ResolveToggleVisual(_state.RecordingMonitorEnabled, _hovering, _pressed);
        _toggle.Background = Design.Brush(colors.Background);
        _toggle.BorderBrush = Design.Brush(colors.Border);
        _icon.Foreground = Design.Brush(colors.Foreground);
    }
}
