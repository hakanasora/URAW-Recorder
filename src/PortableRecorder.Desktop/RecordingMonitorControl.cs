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
    private const double KnobMarginLeft = 8;
    private readonly ShellViewModel _state;
    private readonly Button _toggle;
    private readonly FontIcon _icon = Design.Icon("\uE7F6", 15, Design.Muted);
    private readonly Border _revealHost;
    private readonly RectangleGeometry _revealClip = new();
    private readonly GainControl _knob;
    private readonly NativeTween _reveal;
    private bool _hovering;
    private bool _pressed;
    private bool? _lastEnabled;

    private double RevealedWidth => KnobMarginLeft + _knob.VisualRightEdge + 4;

    public RecordingMonitorControl(ShellViewModel state, Func<bool> animationsEnabled)
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
        _toggle.PointerPressed += (_, _) => { _pressed = true; ApplyToggleVisual(); };
        _toggle.PointerReleased += (_, _) => { _pressed = false; ApplyToggleVisual(); };
        _toggle.PointerCanceled += (_, _) => { _hovering = false; _pressed = false; ApplyToggleVisual(); };
        _toggle.Click += (_, _) =>
        {
            _state.SetRecordingMonitorEnabled(!_state.RecordingMonitorEnabled);
            Refresh(animate: true);
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
            milliseconds: 360,
            x1: .25, y1: .1, x2: .25, y2: 1,
            completed: () =>
            {
                if (!_state.RecordingMonitorEnabled)
                    _revealHost.Visibility = Visibility.Collapsed;
            });

        _knob.VisualWidthChanged += () =>
        {
            if (_state.RecordingMonitorEnabled)
                _reveal.To(RevealedWidth);
        };

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

        if (!enabled) _knob.CancelInteraction();
        if (enabled)
        {
            if (_revealHost.Visibility != Visibility.Visible)
            {
                _reveal.Jump(0);
                _revealHost.Visibility = Visibility.Visible;
            }
            if (animate) _reveal.To(RevealedWidth);
            else _reveal.Jump(RevealedWidth);
        }
        else if (animate)
        {
            _reveal.To(0);
        }
        else
        {
            _reveal.Jump(0);
            _revealHost.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyToggleVisual()
    {
        if (_state.RecordingMonitorEnabled)
        {
            _toggle.Background = Design.Brush(_pressed ? Design.Hex("1F1F22") : Design.Grid);
            _toggle.BorderBrush = Design.Brush(Design.White);
            _icon.Foreground = Design.Brush(Design.White);
        }
        else
        {
            _toggle.Background = Design.Brush(_pressed ? Design.Hex("1F1F22") : _hovering ? Design.Grid : Colors.Transparent);
            _toggle.BorderBrush = Design.Brush(Colors.Transparent);
            _icon.Foreground = Design.Brush(_hovering ? Design.White : Design.Muted);
        }
    }
}
