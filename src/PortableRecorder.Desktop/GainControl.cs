using System.Globalization;
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

public sealed record GainControlOptions(
    string AutomationName,
    string EditorAutomationName,
    string ToolTip,
    Func<double> GetDb,
    Action<double> SetDb,
    bool UseAccentOnNonZero = true,
    Action? OnCommitted = null);

public sealed class GainControl : UserControl
{
    private readonly GainControlOptions _options;
    private readonly TextBlock _number = Design.Text("0 dB", 13, Design.Zero);
    private readonly TextBox _editor = new()
    {
        MinWidth = 0,
        MinHeight = 0,
        Height = 24,
        FontSize = 13,
        Padding = new Thickness(6, 2, 2, 2),
        Foreground = Design.Brush(Design.White),
        Background = Design.Brush(Microsoft.UI.Colors.Transparent),
        BorderThickness = new Thickness(0),
        VerticalContentAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.NoWrap
    };
    private readonly Border _entry = new()
    {
        Width = 70,
        Height = 26,
        CornerRadius = new CornerRadius(5),
        BorderThickness = new Thickness(1),
        Background = Design.Brush(Design.Hex("262628")),
        BorderBrush = Design.Brush(Design.Hex("65656A")),
        Visibility = Visibility.Collapsed
    };
    private Color _accent = Design.Blue;
    private bool _editing, _finishing, _moved, _pressedNumber, _wheelUnlocked;
    private double _startX, _beforeEdit, _wheelDelta;
    private readonly Line _marker;
    private bool _dragging;
    private double _startY, _startGain;
    private double _lastGain = double.NaN;

    public event Action? VisualWidthChanged;

    public double VisualRightEdge => _editing
        ? 95
        : _number.ActualWidth > 0
            ? 25 + _number.ActualWidth
            : 54;

    public GainControl(ShellViewModel state)
        : this(new GainControlOptions(
            "软件增益",
            "输入软件增益，单位 dB，范围负 60 至正 60",
            "单击数字输入；旋钮上拖增加、下拖衰减；离开 0 dB 后滚轮可调；Shift 精调；双击旋钮归零",
            () => state.GainDb,
            db => state.SetGain(db),
            UseAccentOnNonZero: true))
    { }

    public GainControl(GainControlOptions options)
    {
        _options = options;
        var figures = new FontFamily("ms-appx:///Assets/TypeStudy/Barlow-SemiBold.ttf#Barlow SemiBold");
        _number.FontFamily = figures;
        _number.FontWeight = FontWeights.SemiBold;
        _editor.FontFamily = figures;
        _editor.FontWeight = FontWeights.SemiBold;
        Typography.SetNumeralAlignment(_number, FontNumeralAlignment.Tabular);
        Typography.SetNumeralAlignment(_editor, FontNumeralAlignment.Tabular);
        Width = 96;
        Height = 44;
        IsTabStop = true;
        TabIndex = 4;
        AutomationProperties.SetName(this, _options.AutomationName);
        ToolTipService.SetToolTip(this, _options.ToolTip);

        var root = new Grid { Background = Design.Brush(Microsoft.UI.Colors.Transparent) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var knob = new Grid { Width = 17, Height = 17 };
        knob.Children.Add(new Ellipse { Stroke = Design.Brush(Design.Grey), StrokeThickness = 1.4 });
        _marker = new Line { X1 = 8.5, Y1 = 8.5, X2 = 8.5, Y2 = 1, Stroke = Design.Brush(Design.Grey), StrokeThickness = 1.5 };
        knob.Children.Add(_marker);
        row.Children.Add(knob);
        row.Children.Add(_number);
        _number.SizeChanged += (_, _) => VisualWidthChanged?.Invoke();

        _entry.Margin = new Thickness(25, 0, 0, 0);
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

        RepeatButton Step(string name, double direction)
        {
            // Geometric chevrons share identical bounds; font glyph bearings cannot shift their centers.
            var arrow = new Polyline();
            arrow.Points.Add(new Windows.Foundation.Point(0, direction > 0 ? 3 : 0));
            arrow.Points.Add(new Windows.Foundation.Point(3, direction > 0 ? 0 : 3));
            arrow.Points.Add(new Windows.Foundation.Point(6, direction > 0 ? 3 : 0));
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
                Background = Design.Brush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                IsTabStop = false,
                // DefaultRepeatButtonStyle is Left/Center: override the button's own slot alignment,
                // not just its content, so both hit/hover bounds and arrow centers fill each half.
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
            button.PointerExited += (_, _) => { hovering = false; button.Background = Design.Brush(Microsoft.UI.Colors.Transparent); };
            button.PointerPressed += (_, _) => button.Background = Design.Brush(Design.Hex("606065"));
            button.PointerReleased += (_, _) => button.Background = Design.Brush(hovering ? Design.Hex("48484D") : Microsoft.UI.Colors.Transparent);
            button.PointerCanceled += (_, _) => { hovering = false; button.Background = Design.Brush(Microsoft.UI.Colors.Transparent); };
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            button.Click += (_, _) =>
            {
                double baseDb = TryValue(out var value) ? value : _options.GetDb();
                ApplyDb(baseDb + direction * .1, commit: true);
                _editor.Text = _options.GetDb().ToString("0.0");
                _editor.Focus(FocusState.Programmatic);
                _editor.SelectAll();
            };
            return button;
        }

        var up = Step("增加 0.1 dB", 1);
        var down = Step("减少 0.1 dB", -1);
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
        AutomationProperties.SetName(_editor, _options.EditorAutomationName);
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

        PointerEntered += (_, _) =>
        {
            if (_options.GetDb() != 0) _wheelUnlocked = true;
        };
        PointerExited += (_, _) =>
        {
            if (!_dragging && _options.GetDb() == 0)
            {
                _wheelUnlocked = false;
                _wheelDelta = 0;
            }
        };
        PointerPressed += Pressed;
        PointerMoved += Moved;
        PointerReleased += (s, e) =>
        {
            if (!_dragging) return;
            bool edit = _pressedNumber && !_moved;
            _dragging = false;
            ReleasePointerCapture(e.Pointer);
            if (edit) BeginEdit();
            else if (_moved) _options.OnCommitted?.Invoke();
            e.Handled = true;
        };
        PointerCanceled += (s, e) => CancelDrag();
        PointerCaptureLost += (s, e) => CancelDrag();
        PointerWheelChanged += WheelChanged;
        DoubleTapped += (s, e) =>
        {
            if (_editing) return;
            _dragging = false;
            ApplyDb(0, commit: true, relockZero: true);
            e.Handled = true;
        };
        KeyDown += Key;
        _editor.KeyDown += (s, e) =>
        {
            if (e.Key == VirtualKey.Escape) { FinishEdit(false, true); e.Handled = true; }
            else if (e.Key == VirtualKey.Enter) { FinishEdit(true, true); e.Handled = true; }
        };
    }

    public void CommitIfOutside(PointerRoutedEventArgs args)
    {
        if (!_editing) return;
        var point = args.GetCurrentPoint(this).Position;
        if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight) FinishEdit(true, false);
    }

    public void CancelInteraction()
    {
        CancelDrag();
        if (_editing) FinishEdit(false, false);
    }

    public void SetAccent(Color color)
    {
        _accent = color;
        if (_editing) _entry.BorderBrush = Design.Brush(color);
    }

    private bool TryValue(out double value)
    {
        string text = _editor.Text.Replace("dB", "", StringComparison.OrdinalIgnoreCase).Replace('−', '-').Trim();
        return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);
    }

    private void BeginEdit()
    {
        if (_editing) return;
        _editing = true;
        _beforeEdit = _options.GetDb();
        _editor.Text = _beforeEdit.ToString("0.0");
        _number.Visibility = Visibility.Collapsed;
        _entry.Visibility = Visibility.Visible;
        VisualWidthChanged?.Invoke();
        _editor.Focus(FocusState.Programmatic);
        _editor.SelectAll();
    }

    private void ApplyDb(double db, bool commit = false, bool relockZero = false)
    {
        _options.SetDb(db);
        double current = _options.GetDb();
        if (current != 0) _wheelUnlocked = true;
        else if (relockZero)
        {
            _wheelUnlocked = false;
            _wheelDelta = 0;
        }
        if (commit) _options.OnCommitted?.Invoke();
    }

    private void FinishEdit(bool commit, bool refocus)
    {
        if (!_editing || _finishing) return;
        _finishing = true;
        try
        {
            if (commit && TryValue(out var value))
            {
                ApplyDb(value, commit: true, relockZero: true);
                _wheelDelta = 0;
            }
            else if (!commit)
            {
                ApplyDb(_beforeEdit, relockZero: true);
            }
            _editing = false;
            _entry.Visibility = Visibility.Collapsed;
            _number.Visibility = Visibility.Visible;
            _entry.BorderBrush = Design.Brush(Design.Hex("65656A"));
            Refresh();
            VisualWidthChanged?.Invoke();
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
            if (point.X >= 25) return;
            FinishEdit(true, false);
        }
        Focus(FocusState.Pointer);
        _startY = point.Y;
        _startX = point.X;
        _startGain = _options.GetDb();
        _pressedNumber = point.X >= 25;
        _moved = false;
        _dragging = CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var point = e.GetCurrentPoint(this).Position;
        if (Math.Abs(point.Y - _startY) > 3 || Math.Abs(point.X - _startX) > 3) _moved = true;
        if (_moved)
            ApplyDb(_startGain + (_startY - point.Y) * (Shift ? .025 : .25));
        e.Handled = true;
    }

    private void WheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_editing || !_wheelUnlocked) return;
        int rawDelta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (rawDelta == 0) return;
        double stepPerNotch = Shift ? 0.1 : 0.5;
        _wheelDelta += rawDelta / 120.0 * stepPerNotch;
        double applied = Math.Truncate(_wheelDelta * 10.0) / 10.0;
        if (Math.Abs(applied) >= 0.1)
        {
            _wheelDelta -= applied;
            ApplyDb(_options.GetDb() + applied, commit: true);
        }
        e.Handled = true;
    }

    private void CancelDrag()
    {
        if (_dragging)
        {
            _dragging = false;
            ApplyDb(_startGain, relockZero: true);
            ReleasePointerCaptures();
        }
    }

    private void Key(object sender, KeyRoutedEventArgs e)
    {
        if (_editing) return;
        switch (e.Key)
        {
            case VirtualKey.Up:
            case VirtualKey.Right:
                ApplyDb(_options.GetDb() + (Shift ? .1 : 1), commit: true);
                break;
            case VirtualKey.Down:
            case VirtualKey.Left:
                ApplyDb(_options.GetDb() - (Shift ? .1 : 1), commit: true);
                break;
            case VirtualKey.Home:
            case VirtualKey.Number0:
                ApplyDb(0, commit: true, relockZero: true);
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
        var value = _options.GetDb();
        if (value == _lastGain) return;
        var previous = _lastGain;
        _lastGain = value;
        if (double.IsFinite(previous))
            FrameworkElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, previous, value);
        _number.Text = value == 0 ? "0 dB" : $"{(value > 0 ? "+" : "−")}{Math.Abs(value):0.0} dB";
        _number.Foreground = Design.Brush(_options.UseAccentOnNonZero && value != 0 ? Design.Blue : Design.Zero);
        _marker.RenderTransform = new RotateTransform { CenterX = 8.5, CenterY = 8.5, Angle = value / 60 * 130 };
        VisualWidthChanged?.Invoke();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GainPeer(this, _options);

    private sealed class GainPeer(GainControl owner, GainControlOptions options) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => "GainControl";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.RangeValue ? this : base.GetPatternCore(pattern);
        public bool IsReadOnly => false;
        public double LargeChange => 1;
        public double SmallChange => .1;
        public double Maximum => 60;
        public double Minimum => -60;
        public double Value => options.GetDb();
        public void SetValue(double value) => options.SetDb(value);
    }
}
