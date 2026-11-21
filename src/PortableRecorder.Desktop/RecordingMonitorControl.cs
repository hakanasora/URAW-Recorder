using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace PortableRecorder.Desktop;

public sealed class RecordingMonitorControl : UserControl
{
    private readonly ShellViewModel _state;
    private readonly Button _toggle;
    private readonly FontIcon _icon = Design.Icon("\uE7F6", 14, Design.Muted);
    private readonly MonitorVolumeKnob _volume;
    private readonly NativeFade _volumeFade;
    private bool _hovering;
    private bool _pressed;
    private bool? _lastEnabled;
    private int _fadeGeneration;

    public RecordingMonitorControl(ShellViewModel state, Func<bool> animationsEnabled)
    {
        _state = state;
        Height = 44;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;

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

        _volume = new MonitorVolumeKnob(state)
        {
            Visibility = Visibility.Collapsed,
            Opacity = 0
        };
        _volumeFade = new NativeFade(_volume, animationsEnabled);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_toggle);
        row.Children.Add(_volume);
        Content = row;
        Refresh(animate: false);
    }

    public void SetAccent(Color color) => _volume.SetAccent(color);
    public void CommitIfOutside(PointerRoutedEventArgs args) => _volume.CommitIfOutside(args);
    public void CancelEdit() => _volume.CancelEdit();
    public void ReleaseResources() => _volumeFade.Stop();

    public void Refresh(bool animate = false)
    {
        bool enabled = _state.RecordingMonitorEnabled;
        _volume.Refresh();
        if (_lastEnabled == enabled) return;
        _lastEnabled = enabled;
        ApplyToggleVisual();

        string actionName = enabled ? "关闭监听" : "开启监听";
        AutomationProperties.SetName(_toggle, actionName);
        ToolTipService.SetToolTip(_toggle, enabled ? "正在监听，点击关闭" : "开启录制监听");

        int generation = ++_fadeGeneration;
        if (enabled)
        {
            _volume.Visibility = Visibility.Visible;
            if (animate) _volumeFade.To(1, 140);
            else { _volumeFade.Stop(); _volume.Opacity = 1; }
        }
        else
        {
            _volume.CancelEdit();
            if (animate)
            {
                _volumeFade.To(0, 100, () =>
                {
                    if (generation == _fadeGeneration && !_state.RecordingMonitorEnabled)
                        _volume.Visibility = Visibility.Collapsed;
                });
            }
            else
            {
                _volumeFade.Stop();
                _volume.Opacity = 0;
                _volume.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ApplyToggleVisual()
    {
        if (_state.RecordingMonitorEnabled)
        {
            byte bgAlpha = _pressed ? (byte)20 : _hovering ? (byte)42 : (byte)28;
            byte borderAlpha = _hovering ? (byte)102 : (byte)72;
            _toggle.Background = Design.Brush(Color.FromArgb(bgAlpha, Design.Gold.R, Design.Gold.G, Design.Gold.B));
            _toggle.BorderBrush = Design.Brush(Color.FromArgb(borderAlpha, Design.Gold.R, Design.Gold.G, Design.Gold.B));
            _icon.Foreground = Design.Brush(Design.Gold);
        }
        else
        {
            _toggle.Background = Design.Brush(_pressed ? Design.Hex("1F1F22") : _hovering ? Design.Grid : Colors.Transparent);
            _toggle.BorderBrush = Design.Brush(Colors.Transparent);
            _icon.Foreground = Design.Brush(_hovering ? Design.White : Design.Muted);
        }
    }

    private sealed class MonitorVolumeKnob : UserControl
    {
        private readonly ShellViewModel _state;
        private readonly TextBlock _number = Design.Text("100%", 13, Design.Zero);
        private readonly TextBox _editor = new()
        {
            MinWidth = 0,
            MinHeight = 0,
            Height = 24,
            FontSize = 13,
            Padding = new Thickness(6, 2, 2, 2),
            Foreground = Design.Brush(Design.White),
            Background = Design.Brush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        private readonly Border _entry = new()
        {
            Width = 66,
            Height = 26,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Background = Design.Brush(Design.Hex("262628")),
            BorderBrush = Design.Brush(Design.Hex("65656A")),
            Visibility = Visibility.Collapsed
        };
        private readonly Ellipse _ring;
        private readonly Line _marker;
        private Color _accent = Design.Gold;
        private bool _editing, _finishing, _moved, _pressedNumber, _dragging;
        private double _startX, _startY, _startPercent;
        private int _beforeEdit;
        private int _lastPercent = -1;

        public MonitorVolumeKnob(ShellViewModel state)
        {
            _state = state;
            var figures = new FontFamily("ms-appx:///Assets/TypeStudy/Barlow-SemiBold.ttf#Barlow SemiBold");
            _number.FontFamily = figures;
            _number.FontWeight = FontWeights.SemiBold;
            _editor.FontFamily = figures;
            _editor.FontWeight = FontWeights.SemiBold;
            Typography.SetNumeralAlignment(_number, FontNumeralAlignment.Tabular);
            Typography.SetNumeralAlignment(_editor, FontNumeralAlignment.Tabular);

            Width = 78;
            Height = 44;
            IsTabStop = true;
            TabIndex = 4;
            AutomationProperties.SetName(this, "监听音量");
            ToolTipService.SetToolTip(this, "单击数字输入；旋钮下拖衰减、上拖恢复；滚轮微调；Shift 精调；双击复位 100%");

            var root = new Grid { Background = Design.Brush(Colors.Transparent) };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var knob = new Grid { Width = 17, Height = 17 };
            _ring = new Ellipse { Stroke = Design.Brush(Design.Grey), StrokeThickness = 1.4 };
            _marker = new Line { X1 = 8.5, Y1 = 8.5, X2 = 8.5, Y2 = 1, Stroke = Design.Brush(Design.Grey), StrokeThickness = 1.5 };
            knob.Children.Add(_ring);
            knob.Children.Add(_marker);
            row.Children.Add(knob);
            row.Children.Add(_number);

            _entry.Margin = new Thickness(12, 0, 0, 0);
            _entry.HorizontalAlignment = HorizontalAlignment.Left;
            _entry.VerticalAlignment = VerticalAlignment.Center;
            var input = new Grid();
            input.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            input.ColumnDefinitions.Add(new() { Width = new GridLength(14) });
            input.Children.Add(_editor);
            _editor.Template = (ControlTemplate)XamlReader.Load("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="TextBox">
                  <Grid Background="{TemplateBinding Background}">
                    <ScrollViewer x:Name="ContentElement" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      Margin="{TemplateBinding Padding}" IsTabStop="False" ZoomMode="Disabled"
                      HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
                  </Grid>
                </ControlTemplate>
                """);

            var steps = new Grid { UseLayoutRounding = true };
            steps.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            steps.RowDefinitions.Add(new() { Height = new GridLength(1) });
            steps.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });

            RepeatButton Step(string name, int delta)
            {
                var arrow = new Polyline();
                arrow.Points.Add(new Windows.Foundation.Point(0, delta > 0 ? 3 : 0));
                arrow.Points.Add(new Windows.Foundation.Point(3, delta > 0 ? 0 : 3));
                arrow.Points.Add(new Windows.Foundation.Point(6, delta > 0 ? 3 : 0));
                arrow.Width = 7;
                arrow.Height = 4;
                arrow.Stretch = Stretch.Uniform;
                arrow.Stroke = Design.Brush(Design.White);
                arrow.StrokeThickness = 1;
                arrow.StrokeStartLineCap = PenLineCap.Round;
                arrow.StrokeEndLineCap = PenLineCap.Round;
                arrow.StrokeLineJoin = PenLineJoin.Round;
                arrow.HorizontalAlignment = HorizontalAlignment.Center;
                arrow.VerticalAlignment = VerticalAlignment.Center;
                arrow.IsHitTestVisible = false;

                var button = new RepeatButton
                {
                    Content = arrow,
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    Background = Design.Brush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    IsTabStop = false,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                button.Template = (ControlTemplate)XamlReader.Load("""
                    <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="RepeatButton">
                      <Border Margin="1,0.5" CornerRadius="2.5" Background="{TemplateBinding Background}">
                        <ContentPresenter Content="{TemplateBinding Content}" HorizontalAlignment="Stretch" VerticalAlignment="Stretch"
                          HorizontalContentAlignment="Center" VerticalContentAlignment="Center"/>
                      </Border>
                    </ControlTemplate>
                    """);
                bool hovering = false;
                button.PointerEntered += (_, _) => { hovering = true; button.Background = Design.Brush(Design.Hex("48484D")); };
                button.PointerExited += (_, _) => { hovering = false; button.Background = Design.Brush(Colors.Transparent); };
                button.PointerPressed += (_, _) => button.Background = Design.Brush(Design.Hex("606065"));
                button.PointerReleased += (_, _) => button.Background = Design.Brush(hovering ? Design.Hex("48484D") : Colors.Transparent);
                button.PointerCanceled += (_, _) => { hovering = false; button.Background = Design.Brush(Colors.Transparent); };
                AutomationProperties.SetName(button, name);
                ToolTipService.SetToolTip(button, name);
                button.Click += (_, _) =>
                {
                    if (TryValue(out var value)) _state.SetRecordingMonitorVolume(value);
                    _state.SetRecordingMonitorVolume(_state.RecordingMonitorVolumePercent + delta);
                    _editor.Text = _state.RecordingMonitorVolumePercent.ToString(CultureInfo.InvariantCulture);
                    _editor.Focus(FocusState.Programmatic);
                    _editor.SelectAll();
                };
                return button;
            }

            var up = Step("增加 1%", 1);
            var down = Step("减少 1%", -1);
            var separator = new Border { Height = 1, Background = Design.Brush(Design.Hex("48484B")), IsHitTestVisible = false };
            steps.Children.Add(up);
            steps.Children.Add(separator);
            Grid.SetRow(separator, 1);
            steps.Children.Add(down);
            Grid.SetRow(down, 2);
            var stepFrame = new Border { BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = Design.Brush(Design.Hex("48484B")), Child = steps };
            Grid.SetColumn(stepFrame, 1);
            input.Children.Add(stepFrame);
            _entry.Child = input;

            AutomationProperties.SetName(_editor, "输入监听音量百分比，范围 0 至 100");
            _entry.GotFocus += (_, _) => _entry.BorderBrush = Design.Brush(_accent);
            _entry.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!_editing || XamlRoot == null) return;
                var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
                while (focused != null)
                {
                    if (focused == _entry) return;
                    focused = VisualTreeHelper.GetParent(focused);
                }
                FinishEdit(true, false);
            });

            root.Children.Add(row);
            root.Children.Add(_entry);
            Content = root;

            PointerPressed += Pressed;
            PointerMoved += Moved;
            PointerReleased += (_, e) =>
            {
                if (!_dragging) return;
                bool edit = _pressedNumber && !_moved;
                _dragging = false;
                ReleasePointerCapture(e.Pointer);
                if (edit) BeginEdit();
                e.Handled = true;
            };
            PointerCanceled += (_, _) => CancelDrag();
            PointerCaptureLost += (_, _) => CancelDrag();
            PointerWheelChanged += (_, e) =>
            {
                if (_editing) return;
                int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
                if (delta == 0) return;
                _state.SetRecordingMonitorVolume(_state.RecordingMonitorVolumePercent + (delta > 0 ? 1 : -1) * (Shift ? 1 : 5));
                e.Handled = true;
            };
            DoubleTapped += (_, e) =>
            {
                if (_editing) return;
                _dragging = false;
                _state.SetRecordingMonitorVolume(100);
                e.Handled = true;
            };
            KeyDown += Key;
            _editor.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Escape) { FinishEdit(false, true); e.Handled = true; }
                else if (e.Key == VirtualKey.Enter) { FinishEdit(true, true); e.Handled = true; }
            };
        }

        public void SetAccent(Color color)
        {
            _accent = color;
            if (_editing) _entry.BorderBrush = Design.Brush(color);
        }

        public void CommitIfOutside(PointerRoutedEventArgs args)
        {
            if (!_editing) return;
            var point = args.GetCurrentPoint(this).Position;
            if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight)
                FinishEdit(true, false);
        }

        public void CancelEdit()
        {
            CancelDrag();
            if (_editing) FinishEdit(false, false);
        }

        private bool TryValue(out double value)
        {
            string text = _editor.Text.Replace("%", "", StringComparison.Ordinal).Trim();
            return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);
        }

        private void BeginEdit()
        {
            if (_editing) return;
            _editing = true;
            _beforeEdit = _state.RecordingMonitorVolumePercent;
            _editor.Text = _beforeEdit.ToString(CultureInfo.InvariantCulture);
            _number.Visibility = Visibility.Collapsed;
            _entry.Visibility = Visibility.Visible;
            _editor.Focus(FocusState.Programmatic);
            _editor.SelectAll();
        }

        private void FinishEdit(bool commit, bool refocus)
        {
            if (!_editing || _finishing) return;
            _finishing = true;
            try
            {
                if (commit && TryValue(out var value)) _state.SetRecordingMonitorVolume(value);
                else if (!commit) _state.SetRecordingMonitorVolume(_beforeEdit);
                _editing = false;
                _entry.Visibility = Visibility.Collapsed;
                _number.Visibility = Visibility.Visible;
                _entry.BorderBrush = Design.Brush(Design.Hex("65656A"));
                Refresh();
                if (refocus) Focus(FocusState.Programmatic);
            }
            finally { _finishing = false; }
        }

        private static bool Shift => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;

        private void Pressed(object sender, PointerRoutedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var point = e.GetCurrentPoint(this).Position;
            if (_editing)
            {
                if (point.X >= 12) return;
                FinishEdit(true, false);
            }
            Focus(FocusState.Pointer);
            _startY = point.Y;
            _startX = point.X;
            _startPercent = _state.RecordingMonitorVolumePercent;
            _pressedNumber = point.X >= 22;
            _moved = false;
            _dragging = CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void Moved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
            var point = e.GetCurrentPoint(this).Position;
            if (Math.Abs(point.Y - _startY) > 3 || Math.Abs(point.X - _startX) > 3) _moved = true;
            if (_moved) _state.SetRecordingMonitorVolume(_startPercent + (_startY - point.Y) * (Shift ? .15 : .75));
            e.Handled = true;
        }

        private void CancelDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            _state.SetRecordingMonitorVolume(_startPercent);
            ReleasePointerCaptures();
        }

        private void Key(object sender, KeyRoutedEventArgs e)
        {
            if (_editing) return;
            switch (e.Key)
            {
                case VirtualKey.Up:
                case VirtualKey.Right:
                    _state.SetRecordingMonitorVolume(_state.RecordingMonitorVolumePercent + (Shift ? 1 : 5));
                    break;
                case VirtualKey.Down:
                case VirtualKey.Left:
                    _state.SetRecordingMonitorVolume(_state.RecordingMonitorVolumePercent - (Shift ? 1 : 5));
                    break;
                case VirtualKey.Home:
                case VirtualKey.End:
                    _state.SetRecordingMonitorVolume(100);
                    break;
                case VirtualKey.Escape:
                    CancelDrag();
                    break;
                case VirtualKey.F2:
                case VirtualKey.Enter:
                    BeginEdit();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        public void Refresh()
        {
            int value = _state.RecordingMonitorVolumePercent;
            if (value == _lastPercent) return;
            int previous = _lastPercent;
            _lastPercent = value;
            if (previous >= 0)
                FrameworkElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, (double)previous, (double)value);
            _number.Text = $"{value}%";
            bool attenuated = value < 100;
            _number.Foreground = Design.Brush(attenuated ? Design.Gold : Design.Zero);
            _ring.Stroke = Design.Brush(attenuated ? Color.FromArgb(180, Design.Gold.R, Design.Gold.G, Design.Gold.B) : Design.Grey);
            _marker.Stroke = Design.Brush(attenuated ? Design.Gold : Design.Grey);
            _marker.RenderTransform = new RotateTransform { CenterX = 8.5, CenterY = 8.5, Angle = value / 100d * 260d - 130d };
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new VolumePeer(this, _state);

        private sealed class VolumePeer(MonitorVolumeKnob owner, ShellViewModel state) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
        {
            protected override string GetClassNameCore() => "MonitorVolumeKnob";
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
            protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.RangeValue ? this : base.GetPatternCore(pattern);
            public bool IsReadOnly => false;
            public double LargeChange => 5;
            public double SmallChange => 1;
            public double Maximum => 100;
            public double Minimum => 0;
            public double Value => state.RecordingMonitorVolumePercent;
            public void SetValue(double value) => state.SetRecordingMonitorVolume(value);
        }
    }
}
