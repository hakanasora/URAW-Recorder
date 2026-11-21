using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.UI;

namespace PortableRecorder.Desktop;

public sealed class RecordingMonitorControl : Grid
{
    private readonly ShellViewModel _state;
    private readonly Button _toggle;
    private readonly FontIcon _icon = Design.Icon("\uE7F6", 15, Design.Muted);
    private readonly GainControl _knob;
    private bool _hovering;
    private bool _pressed;
    private bool _toggling;
    private bool? _lastEnabled;

    public RecordingMonitorControl(ShellViewModel state, Func<bool, Task> onToggleRequested)
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
                Refresh();
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
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        Children.Add(_toggle);
        Children.Add(_knob);
        Grid.SetColumn(_knob, 1);
        Refresh();
    }

    public void SetAccent(Color color) => _knob.SetAccent(color);
    public void SetToggleEnabled(bool enabled) => _toggle.IsEnabled = enabled;
    public void CommitIfOutside(PointerRoutedEventArgs args) => _knob.CommitIfOutside(args);
    public void CancelEdit() => _knob.CancelInteraction();

    public void Refresh()
    {
        bool enabled = _state.RecordingMonitorEnabled;
        _knob.Refresh();
        if (_lastEnabled == enabled) return;
        _lastEnabled = enabled;
        ApplyToggleVisual();

        AutomationProperties.SetName(_toggle, enabled ? "关闭监听" : "开启监听");
        ToolTipService.SetToolTip(_toggle, enabled ? "正在监听，点击关闭" : "开启录制监听");

        if (!enabled) _knob.CancelInteraction();
        _knob.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyToggleVisual()
    {
        var colors = RecordingMonitorPresentation.ResolveToggleVisual(_state.RecordingMonitorEnabled, _hovering, _pressed);
        _toggle.Background = Design.Brush(colors.Background);
        _toggle.BorderBrush = Design.Brush(colors.Border);
        _icon.Foreground = Design.Brush(colors.Foreground);
    }
}
