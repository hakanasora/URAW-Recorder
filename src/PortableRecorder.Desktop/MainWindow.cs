using System.Diagnostics;
using PortableRecorder.Audio;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.Storage.Pickers;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;

namespace PortableRecorder.Desktop;

public sealed class MainWindow : Window
{
    private readonly ShellViewModel _state=new();
    private readonly MonitorRenderer _renderer=new();
    private readonly UISettings _settings=new();
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(16.67)};
    private readonly Grid _root=new(),_body=new(),_main=new(),_header=new(),_monitor=new(),_transport=new();
    private readonly Grid _inspector=new(){Background=Design.Brush(Design.Inspector),Width=264};
    private readonly CanvasControl _canvas=new(){IsHitTestVisible=false};
    private readonly SplitView _splitView=new()
    {
        DisplayMode=SplitViewDisplayMode.Inline, PanePlacement=SplitViewPanePlacement.Left,
        OpenPaneLength=FixedWindowFrame.PaneWidth, CompactPaneLength=0,
        PaneBackground=Design.Brush(Design.Inspector)
    };
    private readonly FixedWindowFrame _frame;
    private readonly Grid _primaryContent=new();
    private readonly NativeFade _monitorFade,_clockFade,_statusFade,_primaryFade,_pauseFade;
    private RecordingStage? _lastStage;
    private int _transportGeneration;
    private readonly StackPanel _level=new(),_pitch=new(),_inputPage=new(){Spacing=24},_filePage=new(){Spacing=24};
    private readonly FadingDeviceName _inputName=new();
    private readonly TextBlock _number=Design.Text("32.0",69),_peak=Design.Text("",14,Design.White);
    private readonly TextBlock _note=Design.Text("B2",69),_frequency=Design.Text("123.47",20),_clock=Design.Text("00:00.00",32),_sessionLabel=Design.Text("录制中",12,Design.Zero);
    private readonly TextBlock _fileHint=Design.Text("",12,Design.Label),_pathError=Design.Text("",12,Design.Alert);
    private readonly Border _minus=new(){Width=18,Height=5,Background=Design.Brush(Design.White),VerticalAlignment=VerticalAlignment.Center};
    private readonly Button _settingsButton,_pauseButton,_stopButton,_discardButton;
    private readonly GainControl _gain;
    private readonly Grid _actions=new(){HorizontalAlignment=HorizontalAlignment.Right};
    private readonly StackPanel _activeButtons=new(){Orientation=Orientation.Horizontal,Spacing=3,Visibility=Visibility.Collapsed};
    private readonly Grid _clockSlot=new(){HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed};
    private readonly SegmentedSelector _modeSelector,_paneSelector;
    private readonly ComboBox _recordModeBox=new();
    private readonly ComboBox _deviceBox=new(),_calibrationBox=new(),_formatBox=new(),_qualityBox=new();
    private readonly TextBox _path=new(){PlaceholderText="选择文件夹",MinWidth=0};
    private readonly StackPanel _qualityGroup=new(){Spacing=8};
    private readonly List<Control> _configuration=[];
    private Storyboard? _widthAnimation;
    private bool _firstFrameLogged,_paneOpen,_updatingForm,_started;
    private bool _audioBusy,_closing,_closeAllowed,_dialogOpen;
    private AudioRunState? _lastAudioState;
    private long _lastRawDecision;
    private string _lastAudioMessage="";
    private readonly TextBlock _captureInfo=Design.Text("正在准备音频服务",11,Design.Muted);
    private readonly TextBlock _serviceNotice=Design.Text("",11,Design.Muted);
    private readonly bool _noCapture=Environment.GetCommandLineArgs().Contains("--no-capture");
    private int? _displayedNote;
    private readonly SolidColorBrush _normalPeakInk=Design.Brush(Design.White),_overloadPeakInk=Design.Brush(Design.Alert);
    private static readonly bool OverlayDrawer=false;
    private DeviceTopologyWatcher? _deviceWatcher;
    private readonly DispatcherTimer _deviceRefreshTimer=new(){Interval=TimeSpan.FromMilliseconds(300)};
    private bool _devicesDirty,_devicesLoaded;
    private int _deviceVersion;
    private readonly Stopwatch _renderClock=Stopwatch.StartNew();
    private double _lastRender,_nextRenderDue,_nextDisplayProbe;
    private bool _displayProbePending;
    private DisplayRefreshInfo _displayRefresh=new("unknown",null,"compositor fallback");
    private int _renderFrames,_drawFrames;
    private double _renderReportTime;
    private double _maxRenderGap;

    public MainWindow()
    {
        Title="Portable Recorder";ExtendsContentIntoTitleBar=true;
        _state.Audio.Diagnostic+=StartupLog.Write;
        _frame=new FixedWindowFrame(this,()=>_root.XamlRoot?.RasterizationScale??1);
        _monitorFade=new NativeFade(_monitor,()=>_settings.AnimationsEnabled);
        _clockFade=new NativeFade(_clockSlot,()=>_settings.AnimationsEnabled);
        _statusFade=new NativeFade(_sessionLabel,()=>_settings.AnimationsEnabled);
        _primaryFade=new NativeFade(_primaryContent,()=>_settings.AnimationsEnabled);
        _root.RequestedTheme=ElementTheme.Dark;
        var micaSupported=MicaController.IsSupported();
        if(micaSupported)SystemBackdrop=new MicaBackdrop{Kind=MicaKind.Base};
        _root.Background=Design.Brush(micaSupported?Colors.Transparent:Design.Display);
        // Neutral veils preserve the gray/near-black hierarchy while allowing visible Mica tint.
        var displaySurface=MaterialSurface.Tint(Design.Display,micaSupported?MaterialSurface.DisplayVeilAlpha:255);
        var transportSurface=MaterialSurface.Tint(Design.Transport,micaSupported?MaterialSurface.TransportVeilAlpha:255);
        StartupLog.Write($"Material: Mica={micaSupported}; neutral veils display={MaterialSurface.DisplayVeilAlpha}/255, transport={MaterialSurface.TransportVeilAlpha}/255");
        _root.RowDefinitions.Add(new(){Height=new GridLength(32)});_root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var title=new Grid{Background=Design.Brush(displaySurface),Height=32};
        title.Children.Add(new TextBlock{Text=Title,FontSize=12,Foreground=Design.Brush(Design.Muted),Margin=new Thickness(16,0,140,0),VerticalAlignment=VerticalAlignment.Center});
        _root.Children.Add(title);SetTitleBar(title);
        AppWindow.TitleBar.BackgroundColor=Colors.Transparent;AppWindow.TitleBar.ButtonBackgroundColor=Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor=Design.Muted;AppWindow.TitleBar.ButtonInactiveBackgroundColor=Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor=Design.Selected;
        _root.Children.Add(_body);Grid.SetRow(_body,1);_body.Children.Add(_splitView);
        _main.Width=FixedWindowFrame.MainWidth;_main.Height=384;
        _main.HorizontalAlignment=HorizontalAlignment.Right;
        _main.Clip=new RectangleGeometry{Rect=new Rect(0,0,820,384)};
        _main.RowDefinitions.Add(new(){Height=new GridLength(64)});
        _main.RowDefinitions.Add(new(){Height=new GridLength(236)});
        _main.RowDefinitions.Add(new(){Height=new GridLength(84)});
        var displayPanel=MaterialSurface.DisplayPanel(displaySurface);
        _main.Children.Add(displayPanel);Grid.SetRowSpan(displayPanel,2);
        _main.Children.Add(_header);_main.Children.Add(_monitor);Grid.SetRow(_monitor,1);
        _main.Children.Add(_transport);Grid.SetRow(_transport,2);
        // The main view is a sibling, never a child of the SplitView's animated content grid.
        _body.Children.Add(_main);
        Canvas.SetZIndex(_main,1);
        Canvas.SetZIndex(_splitView,OverlayDrawer?2:0);
        _splitView.DisplayMode=OverlayDrawer?SplitViewDisplayMode.Overlay:SplitViewDisplayMode.Inline;
        _settingsButton=Design.Button(MenuGlyph(),"输入与文件设置",Colors.Transparent);_settingsButton.Width=40;_settingsButton.Height=44;_settingsButton.Padding=new Thickness(0);
        _settingsButton.HorizontalAlignment=HorizontalAlignment.Left;_settingsButton.VerticalAlignment=VerticalAlignment.Center;_settingsButton.Margin=new Thickness(28,0,0,0);_settingsButton.TabIndex=0;
        _settingsButton.Click+=(_,_)=>TogglePane();_header.Children.Add(_settingsButton);

        _modeSelector=Design.Selector(["电平","频谱","音高"],76,2,index=>{_state.Mode=(MonitorMode)index;if(_started)RefreshMode();});_modeSelector.TabIndex=1;
        _header.Children.Add(new Border{Background=Design.Brush(MaterialSurface.Tint(Design.Hex("0A0A0A"),102)),CornerRadius=new CornerRadius(4),Padding=new Thickness(4),Child=_modeSelector,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});
        _monitor.Children.Add(_canvas);BuildReadouts();
        _transport.Background=Design.Brush(transportSurface);
        _transport.Children.Add(new Border{Height=1,Background=Design.Brush(MaterialSurface.Tint(Colors.White,14)),VerticalAlignment=VerticalAlignment.Top,IsHitTestVisible=false});
        _gain=new(_state){Width=96,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(190,0,0,0)};_transport.Children.Add(_gain);
        _inputName.HorizontalAlignment=HorizontalAlignment.Left;
        _inputName.VerticalAlignment=VerticalAlignment.Center;
        _inputName.Margin=new Thickness(28,0,0,0);
        _transport.Children.Add(_inputName);
        _inputName.SizeChanged+=(_,_)=>_gain.Margin=new Thickness(28+_inputName.GainStartOffset,0,0,0);
        _clockSlot.Width=240;_clockSlot.Height=60;
        _clockSlot.RowDefinitions.Add(new(){Height=new GridLength(20)});
        _clockSlot.RowDefinitions.Add(new(){Height=new GridLength(40)});
        _sessionLabel.HorizontalAlignment=HorizontalAlignment.Stretch;
        _clock.HorizontalAlignment=HorizontalAlignment.Stretch;
        _sessionLabel.TextAlignment=TextAlignment.Center;_clock.TextAlignment=TextAlignment.Center;
        _clockSlot.Children.Add(_sessionLabel);_clockSlot.Children.Add(_clock);Grid.SetRow(_clock,1);
        _clock.FontWeight=FontWeights.Bold;_clock.CharacterSpacing=-10;_transport.Children.Add(_clockSlot);
        _clockSlot.SizeChanged+=(_,_)=>LogClockCenters();
        _actions.Margin=new Thickness(0,0,28,0);_actions.VerticalAlignment=VerticalAlignment.Center;
        _pauseButton=Design.Button(Design.ActionContent("pause"),"暂停录制");
        _pauseButton.Width=0;_pauseButton.Opacity=0;_pauseButton.Visibility=Visibility.Collapsed;
        _pauseButton.CornerRadius=new(5,0,0,5);_pauseButton.TabIndex=5;
        _pauseFade=new NativeFade(_pauseButton,()=>_settings.AnimationsEnabled);
        _primaryContent.Children.Add(Design.ActionContent("record","录制"));
        _stopButton=Design.Button(_primaryContent,"开始录制",Design.Record);
        _stopButton.Width=100;_stopButton.TabIndex=6;
        _pauseButton.Click+=async(_,_)=>await AudioCommand(()=>_state.Audio.PauseAsync(_state.Session.Stage!=RecordingStage.Paused));
        _stopButton.Click+=async(_,_)=>await AudioCommand(async()=>
        {
            if(_state.Session.Active)await _state.Audio.StopRecordingAsync();
            else await _state.Audio.StartRecordingAsync(_state.OutputPath,_state.RecordMode);
        });
        _activeButtons.Visibility=Visibility.Visible;_activeButtons.Spacing=0;
        _discardButton=Design.Button(Design.Icon("\uE74D",16,Design.White),"丢弃");
        _discardButton.Width=0;_discardButton.Opacity=0;_discardButton.Visibility=Visibility.Collapsed;
        _discardButton.Padding=new Thickness(0);_discardButton.CornerRadius=new(5,0,0,5);_discardButton.TabIndex=5;
        _discardButton.Click+=async(_,_)=>await AudioCommand(_state.Audio.DiscardRecordingAsync);
        _activeButtons.Children.Add(_discardButton);
        _activeButtons.Children.Add(_pauseButton);_activeButtons.Children.Add(_stopButton);
        _actions.Children.Add(_activeButtons);_transport.Children.Add(_actions);
        _serviceNotice.MaxWidth=215;_serviceNotice.TextTrimming=TextTrimming.CharacterEllipsis;
        _serviceNotice.HorizontalAlignment=HorizontalAlignment.Right;_serviceNotice.VerticalAlignment=VerticalAlignment.Center;
        _serviceNotice.Margin=new Thickness(0,0,28,0);_header.Children.Add(_serviceNotice);
        _paneSelector=Design.Selector(["输入","文件"],84,8,index=>
        {
            _inputPage.Visibility=index==0?Visibility.Visible:Visibility.Collapsed;
            _filePage.Visibility=index==1?Visibility.Visible:Visibility.Collapsed;
        });
        BuildInspector();
        _splitView.Pane=_inspector;
        _splitView.PaneClosed+=(_,_)=>
        {
            if(_paneOpen)return; // A rapid reopen must not shrink the new opening.
            if(!OverlayDrawer){_frame.SetExpanded(false);_root.UpdateLayout();}
            _settingsButton.Focus(FocusState.Programmatic);
        };
        _splitView.PaneClosing+=(_,_)=>{if(OverlayDrawer)_paneOpen=false;};
        _canvas.Draw+=(sender,args)=>
        {
            if(_closing || _closeAllowed)return;
            try { _drawFrames++;_renderer.Draw(sender,args,_state); if(!_firstFrameLogged){_firstFrameLogged=true;StartupLog.Write($"First monitor frame drawn: {sender.ActualWidth} x {sender.ActualHeight}");} }
            catch(Exception error){StartupLog.Write("Draw failed: "+error);throw;}
        };_state.PropertyChanged+=(_,_)=>RefreshValues();
        _root.Loaded+=async(_,_)=>
        {
            try
            {
                if(_started)return;
                StartupLog.Write("Root Loaded");_started=true;
                UpdateTextScale();StartupLog.Write("Text scale ready");
                _frame.Initialize();StartupLog.Write("Fixed client frame ready");
                _root.XamlRoot.Changed+=(_,_)=>_frame.RefreshDpi();
                RefreshMode();StartupLog.Write("Mode ready");
                RefreshSession(false);StartupLog.Write("Session ready");
                RefreshValues();StartupLog.Write("Values ready");
                _timer.Start();StartupLog.Write("Timer started");
                if(!_noCapture)
                {
                    try{_deviceWatcher=new DeviceTopologyWatcher(()=>DispatcherQueue.TryEnqueue(()=>
                        {if(_closing)return;_deviceVersion++;_devicesDirty=true;_deviceRefreshTimer.Stop();_deviceRefreshTimer.Start();}));}
                    catch(Exception error){StartupLog.Write("Device watcher unavailable: "+error);}
                    await LoadDevices(manual:false);
                }
                else _captureInfo.Text="诊断模式：未打开麦克风";
                CompositionTarget.Rendering+=OnRendering;
            }
            catch(Exception error){StartupLog.Write("Loaded failed: "+error);throw;}
        };
        _deviceRefreshTimer.Tick+=async(_,_)=>
        {
            if(_closing){_deviceRefreshTimer.Stop();return;}
            if(!_devicesDirty){_deviceRefreshTimer.Stop();return;}
            if(_audioBusy || _dialogOpen)return;
            _deviceRefreshTimer.Stop();await LoadDevices(manual:false);
        };
        _main.SizeChanged+=(_,_)=>Layout();_root.KeyDown+=OnKeyDown;_timer.Tick+=(_,_)=>Tick();
        _settings.AnimationsEnabledChanged+=(_,_)=>DispatcherQueue.TryEnqueue(()=>{UpdateTextScale();RefreshSession(false);});
        _settings.TextScaleFactorChanged+=(_,_)=>DispatcherQueue.TryEnqueue(()=>{UpdateTextScale();Layout();});
        AppWindow.Closing+=async(_,args)=>
        {
            if(_closeAllowed)return;
            args.Cancel=true;if(_closing)return;_closing=true;_deviceRefreshTimer.Stop();_deviceWatcher?.Dispose();_deviceWatcher=null;
            _stopButton.IsEnabled=false;_pauseButton.IsEnabled=false;
            try{await _state.Audio.DisposeAsync();}
            catch(Exception error){StartupLog.Write("Audio close: "+error);await Message("录音收尾异常",AudioEngine.Friendly(error));}
            finally{_closeAllowed=true;Close();}
        };
        Closed+=(_,_)=>{CompositionTarget.Rendering-=OnRendering;_inputName.ReleaseResources();_timer.Stop();_widthAnimation?.Stop();_monitorFade.Stop();_clockFade.Stop();_statusFade.Stop();_primaryFade.Stop();_pauseFade.Stop();_canvas.RemoveFromVisualTree();_renderer.Dispose();};Content=_root;
    }
    private static Grid MenuGlyph()
    {
        var grid=new Grid{Width=18,Height=12};var canvas=new Canvas{Width=18,Height=12};
        foreach(var y in new[]{0,5.25,10.5}){var line=new Border{Width=18,Height=1.5,Background=Design.Brush(Design.Muted)};Canvas.SetTop(line,y);canvas.Children.Add(line);}grid.Children.Add(canvas);return grid;
    }
    private void BuildReadouts()
    {
        _level.RenderTransform=new TranslateTransform{Y=-14};
        _level.Width=200;_level.Margin=new Thickness(28,0,0,0);_level.HorizontalAlignment=HorizontalAlignment.Left;_level.VerticalAlignment=VerticalAlignment.Center;
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4,Height=72};_number.FontWeight=FontWeights.Bold;_number.CharacterSpacing=-20;row.Children.Add(_minus);row.Children.Add(_number);_level.Children.Add(row);
        var units=Design.Text("dBFS · RMS",12,Design.Muted);units.Height=16;units.Margin=new Thickness(0,10,0,0);_level.Children.Add(units);
        _peak.FontWeight=FontWeights.Bold;
        var peakRow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Height=28,Margin=new Thickness(0,16,0,0)};
        var reset=Design.Button(Design.Icon("\uE777",14),"清除历史输出峰值",Colors.Transparent);reset.Width=24;reset.Height=28;reset.Padding=new Thickness(0);reset.TabIndex=3;
        reset.Click+=async(_,_)=>await AudioCommand(_state.ResetPeakAsync);peakRow.Children.Add(_peak);peakRow.Children.Add(reset);_level.Children.Add(peakRow);_monitor.Children.Add(_level);
        _pitch.Width=200;_pitch.Margin=new Thickness(28,0,0,0);_pitch.HorizontalAlignment=HorizontalAlignment.Left;_pitch.VerticalAlignment=VerticalAlignment.Center;_pitch.Visibility=Visibility.Collapsed;
        _note.Height=72;_note.CharacterSpacing=-20;_pitch.Children.Add(_note);var hz=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5,Margin=new Thickness(0,10,0,0),Height=26};hz.Children.Add(_frequency);hz.Children.Add(Design.Text("Hz",12,Design.Muted));_pitch.Children.Add(hz);_pitch.Children.Add(new Border{Height=64});_monitor.Children.Add(_pitch);
        ToolTipService.SetToolTip(_level,"300 ms RMS；PEAK 为增益后历史逐样本峰值。VU 参考：峰值 −18 dBFS 的 1 kHz 正弦 = 0 VU，RMS 约 −21.01 dBFS。+3 VU 不等于数字削波：表盘刻度为 VU，PEAK 为 dBFS。多声道电平按能量平均，VU 显示前两路；单声道镜像显示。软件增益同时影响文件。 ");
        AutomationProperties.SetName(_number,"当前输出 RMS，dBFS");AutomationProperties.SetLiveSetting(_sessionLabel,AutomationLiveSetting.Polite);
    }
    private void BuildInspector()
    {
        _inspector.RowDefinitions.Add(new(){Height=new GridLength(48)});_inspector.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var top=new Grid{Padding=new Thickness(20,0,16,0),ColumnSpacing=12};
        top.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        top.ColumnDefinitions.Add(new(){Width=new GridLength(32)});
        _paneSelector.HorizontalAlignment=HorizontalAlignment.Left;
        top.Children.Add(_paneSelector);
        var close=Design.Button(Design.Icon("\uE711",14,Design.Hex("747476")),"关闭设置",Colors.Transparent);
        close.Width=32;close.Height=32;close.Padding=new Thickness(0);
        Grid.SetColumn(close,1);close.Click+=(_,_)=>TogglePane();top.Children.Add(close);
        _inspector.Children.Add(top);
        var pages=new Grid{Padding=new Thickness(24,16,24,24)};
        pages.Children.Add(_inputPage);pages.Children.Add(_filePage);_filePage.Visibility=Visibility.Collapsed;
        _inspector.Children.Add(pages);Grid.SetRow(pages,1);
        _inputPage.Spacing=12;
        StyleField(_deviceBox);_deviceBox.ItemsSource=_state.Devices;_deviceBox.DisplayMemberPath="FriendlyName";
        _deviceBox.SelectionChanged+=async(_,_)=>
        {
            if(_updatingForm || _audioBusy || _deviceBox.SelectedItem is not InputDevice device)return;
            _state.SelectedInput=device;
            await AudioCommand(()=>_state.Audio.SelectDeviceAsync(new(device.Id,device.FriendlyName)));
        };
        var deviceGroup=Group("输入设备","\uE720",_deviceBox);
        var refresh=LinkButton("刷新设备 / 重新监看");refresh.Click+=async(_,_)=>await LoadDevices();deviceGroup.Children.Add(refresh);_inputPage.Children.Add(deviceGroup);
        StyleField(_recordModeBox);
        _recordModeBox.ItemsSource=new[]{"独占","不独占"};
        _recordModeBox.SelectedIndex=_state.RecordMode==RecordingMode.Exclusive?0:1;
        _recordModeBox.SelectionChanged+=(_,_)=>SetRecordingMode(_recordModeBox.SelectedIndex==0?RecordingMode.Exclusive:RecordingMode.Shared);
        _inputPage.Children.Add(Group("录制模式","\uE713",_recordModeBox));
        _captureInfo.TextWrapping=TextWrapping.Wrap;_captureInfo.MaxHeight=64;_inputPage.Children.Add(_captureInfo);
        var calibration=LinkButton("校准（预留）");calibration.Click+=async(_,_)=>await CalibrationSettings();_inputPage.Children.Add(calibration);
        StyleField(_calibrationBox);_calibrationBox.ItemsSource=_state.Calibrations;_calibrationBox.DisplayMemberPath="Name";
        _calibrationBox.SelectedItem=_state.SelectedCalibration;
        _calibrationBox.SelectionChanged+=(_,_)=>{if(_calibrationBox.SelectedItem is CalibrationFile file)_state.SelectedCalibration=file;};
        StyleField(_path);_path.VerticalContentAlignment=VerticalAlignment.Center;_path.Padding=new Thickness(12,8,12,8);_path.TextChanged+=(_,_)=>{_state.OutputPath=_path.Text;ToolTipService.SetToolTip(_path,_path.Text);};
        var pathRow=new Grid{ColumnSpacing=8};pathRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});pathRow.ColumnDefinitions.Add(new(){Width=new GridLength(36)});pathRow.Children.Add(_path);
        var browse=Design.Button(Design.Icon("\uE8B7",16,Design.White),"浏览保存文件夹",Design.Field);browse.Width=36;browse.Height=36;browse.CornerRadius=new(3);browse.Padding=new Thickness(0);Grid.SetColumn(browse,1);pathRow.Children.Add(browse);_configuration.Add(browse);browse.Click+=async(_,_)=>await BrowseFolder();
        var pathGroup=Group("保存路径","\uE8B7",pathRow);_pathError.TextWrapping=TextWrapping.Wrap;_pathError.Visibility=Visibility.Collapsed;pathGroup.Children.Add(_pathError);_filePage.Children.Add(pathGroup);
        StyleField(_formatBox);_formatBox.Items.Add(new ComboBoxItem{Content="WAV · 32 位浮点",Tag="WAV"});
        _formatBox.Items.Add(new ComboBoxItem{Content="FLAC（尚未接入）",IsEnabled=false});
        _formatBox.Items.Add(new ComboBoxItem{Content="MP3（尚未接入）",IsEnabled=false});_formatBox.SelectedIndex=0;_formatBox.SelectionChanged+=(_,_)=>UpdateQuality();_filePage.Children.Add(Group("录制格式","\uE8A5",_formatBox));
        StyleField(_qualityBox);_qualityGroup.Children.Add(Label("压缩品质","\uE713"));_qualityGroup.Children.Add(_qualityBox);_filePage.Children.Add(_qualityGroup);

        _fileHint.TextWrapping=TextWrapping.Wrap;_filePage.Children.Add(_fileHint);UpdateQuality();
    }
    private static StackPanel Label(string text,string glyph){var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Height=20};row.Children.Add(Design.Icon(glyph,16,Design.Label));row.Children.Add(Design.Text(text,13,Design.Label));return row;}
    private static StackPanel Group(string title,string glyph,UIElement field){var panel=new StackPanel{Spacing=8};panel.Children.Add(Label(title,glyph));panel.Children.Add(field);return panel;}
    private Button LinkButton(string text){var b=Design.Button(Design.Text(text,13,Design.Link),text,Colors.Transparent);b.Height=24;b.Padding=new Thickness(0);b.HorizontalAlignment=HorizontalAlignment.Left;_configuration.Add(b);return b;}
    private void StyleField(Control field)
    {
        field.Height=36;field.MinHeight=36;field.MinWidth=0;field.FontSize=13;field.Foreground=Design.Brush(Design.White);field.Background=Design.Brush(Design.Field);field.BorderThickness=new Thickness(0);field.CornerRadius=new CornerRadius(3);field.Padding=new Thickness(12,0,12,0);field.HorizontalAlignment=HorizontalAlignment.Stretch;
        foreach(var key in new[]{"ComboBoxBackground","ComboBoxBackgroundPointerOver","ComboBoxBackgroundPressed","ComboBoxBackgroundDisabled","TextControlBackground","TextControlBackgroundPointerOver","TextControlBackgroundFocused","TextControlBackgroundDisabled"})field.Resources[key]=Design.Brush(Design.Field);
        foreach(var key in new[]{"ComboBoxBorderBrush","ComboBoxBorderBrushPointerOver","ComboBoxBorderBrushPressed","TextControlBorderBrush","TextControlBorderBrushPointerOver"})field.Resources[key]=Design.Brush(Colors.Transparent);
        field.Resources["TextControlBorderBrushFocused"]=Design.Brush(Design.White);field.Resources["TextControlPlaceholderForeground"]=Design.Brush(Design.Hex("BDBDC2"));_configuration.Add(field);
    }
    private void UpdateQuality()
    {
        _state.Format="WAV";_qualityGroup.Visibility=Visibility.Collapsed;
        _fileHint.Text="32 位浮点 WAV / RF64；采样率、声道数沿用实际输入。\n未设置路径时保存到‘音乐\\Portable Recorder’。";
    }
    private async Task BrowseFolder()
    {
        if(_state.Session.Active)return;
        try{var result=await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();if(result!=null)_path.Text=result.Path;_pathError.Visibility=Visibility.Collapsed;}
        catch{_pathError.Text="暂时无法打开文件夹选择器，请手动输入路径。";_pathError.Visibility=Visibility.Visible;}
    }
    private async Task AddCalibration(bool selectAdded=true)
    {
        if(_state.Session.Active)return;
        try
        {
            var picker=new FileOpenPicker(AppWindow.Id);picker.FileTypeFilter.Add(".txt");var files=await picker.PickMultipleFilesAsync();if(_state.Session.Active || files is null)return;
            foreach(var file in files){var existing=_state.Calibrations.FirstOrDefault(c=>string.Equals(c.Path,file.Path,StringComparison.OrdinalIgnoreCase));var item=existing??new CalibrationFile(file.Path,System.IO.Path.GetFileName(file.Path));if(existing==null)_state.Calibrations.Add(item);if(selectAdded)_calibrationBox.SelectedItem=item;}
        }
        catch{await Message("校准文件","暂时无法打开文件选择器，请稍后再试。");}
    }
    private async Task ManageCalibrations()
    {
        if(_state.Session.Active)return;
        // Management has no selection action; the input ComboBox owns the active calibration.
        while(!_state.Session.Active)
        {
            var rows=new StackPanel{Spacing=4};
            void Rebuild()
            {
                rows.Children.Clear();
                foreach(var file in _state.Calibrations.Where(file=>!string.IsNullOrWhiteSpace(file.Path)).ToArray())
                {
                    var row=new Grid{Height=40,Background=Design.Brush(Colors.Transparent)};
                    row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
                    row.ColumnDefinitions.Add(new(){Width=new GridLength(56)});
                    var name=Design.Text(file.Name,13);name.Margin=new Thickness(12,0,8,0);
                    name.TextTrimming=TextTrimming.CharacterEllipsis;ToolTipService.SetToolTip(name,file.Path);
                    row.Children.Add(name);
                    var remove=Design.Button(Design.Text("移除",12,Design.Muted),"移除校准文件："+file.Name,Colors.Transparent);
                    remove.Height=32;remove.Padding=new Thickness(6,0,6,0);remove.Opacity=0;
                    Grid.SetColumn(remove,1);row.Children.Add(remove);
                    var hovered=false;
                    row.PointerEntered+=(_,_)=>{hovered=true;remove.Opacity=1;};
                    row.PointerExited+=(_,_)=>{hovered=false;if(remove.FocusState==FocusState.Unfocused)remove.Opacity=0;};
                    remove.GotFocus+=(_,_)=>remove.Opacity=1;
                    remove.LostFocus+=(_,_)=>{if(!hovered)remove.Opacity=0;};
                    remove.Click+=(_,_)=>
                    {
                        if(_state.Session.Active)return;
                        if(file==_state.SelectedCalibration)_calibrationBox.SelectedItem=_state.Calibrations.First(f=>string.IsNullOrWhiteSpace(f.Path));
                        _state.Calibrations.Remove(file);Rebuild();
                    };
                    rows.Children.Add(row);
                }
                if(rows.Children.Count==0)rows.Children.Add(Design.Text("尚未添加校准文件",13,Design.Muted));
            }
            Rebuild();
            var list=new ScrollViewer{Content=rows,Height=160,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            var dialog=new ContentDialog{XamlRoot=_root.XamlRoot,Title="已添加的校准",Content=list,PrimaryButtonText="添加校准",CloseButtonText="关闭",RequestedTheme=ElementTheme.Dark};
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
            // Close the modal before invoking the native picker; restore management afterwards.
            await AddCalibration(selectAdded:false);
        }
    }
    private async Task Message(string title,string text){await new ContentDialog{XamlRoot=_root.XamlRoot,Title=title,Content=text,CloseButtonText="关闭",RequestedTheme=ElementTheme.Dark}.ShowAsync();}
    private void Tick()
    {
        _state.Refresh();
        var status=_state.Audio.Status;
        if(status.State!=_lastAudioState){_lastAudioState=status.State;RefreshSession(true);}
        RefreshValues();_clock.Text=_state.Session.DisplayTime;
        _stopButton.IsEnabled=!_audioBusy && status.State is AudioRunState.Monitoring or AudioRunState.Preparing or AudioRunState.Recording or AudioRunState.Paused;
        _discardButton.IsEnabled=!_audioBusy && status.State==AudioRunState.Paused;
        _pauseButton.IsEnabled=!_audioBusy && status.State is AudioRunState.Recording or AudioRunState.Paused;
        var capture=status.Capture;
        _inputName.SetChannels(capture?.Format.Channels??0);
        _captureInfo.Text=capture==null?(_noCapture?"诊断模式：未打开麦克风":_state.Devices.Count==0?"未找到可用输入设备；接入设备后自动恢复监看。":status.Message):
            $"{(capture.Exclusive?"独占":"共享")} · {(capture.Raw?"RAW 请求已接受":"普通输入（本次许可）")}\n{capture.Format.Description}\n待机与停止后使用共享监看；校准未应用。";
        ToolTipService.SetToolTip(_captureInfo,_captureInfo.Text+"\n"+status.Message);
        if(status.Message!=_lastAudioMessage)
        {
            _lastAudioMessage=status.Message;StartupLog.Write("Audio: "+status.State+" / "+status.Message);
            _serviceNotice.Text=status.State is AudioRunState.Faulted or AudioRunState.Stopped || status.FilePath!=null && ! _state.Session.Active
                ?status.Message:status.State==AudioRunState.Monitoring && _state.Audio.Latest.RecoveredDiscontinuities>0
                ?$"监看已恢复（短暂不连续 {_state.Audio.Latest.RecoveredDiscontinuities} 次）":"";
            ToolTipService.SetToolTip(_serviceNotice,status.Message+(status.FilePath==null?"":"\n"+status.FilePath));
        }
        _=CheckRawDecision();
        bool minimized=AppWindow.Presenter is OverlappedPresenter{State:OverlappedPresenterState.Minimized};
        var interval=TimeSpan.FromMilliseconds(minimized?250:33.33);if(_timer.Interval!=interval)_timer.Interval=interval;
        // CompositionTarget drives drawing separately; the timer only updates XAML and commands.
    }
    private async Task AudioCommand(Func<Task> command)
    {
        if(_audioBusy || _closing)return;_audioBusy=true;
        _stopButton.IsEnabled=false;_pauseButton.IsEnabled=false;_discardButton.IsEnabled=false;
        foreach(var control in _configuration)control.IsEnabled=false;
        try{await command();}
        catch(Exception error){StartupLog.Write("Audio command: "+error);await Message("音频服务",AudioEngine.Friendly(error));}
        finally{_audioBusy=false;_state.Refresh();RefreshSession(true);RefreshValues();}
    }
    private void SetRecordingMode(RecordingMode mode)
    {
        _state.RecordMode=mode;
        try{RecordingPreferences.Save(mode);}catch(Exception error){StartupLog.Write("Preferences save failed: "+error);}
    }
    private void OnRendering(object? sender,object args)
    {
        if(!_started || _closing || AppWindow.Presenter is OverlappedPresenter{State:OverlappedPresenterState.Minimized})return;
        double now=_renderClock.Elapsed.TotalSeconds;
        if(now>=_nextDisplayProbe && !_displayProbePending)
        {
            _nextDisplayProbe=now+1;_displayProbePending=true;
            _=ProbeDisplayRefreshAsync(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        if(_displayRefresh.Hertz is double rate)
        {
            double period=1d/rate;
            if(now+Math.Min(.0003,period*.04)<_nextRenderDue)return;
            _nextRenderDue=Math.Max(_nextRenderDue+period,now);
        }
        _maxRenderGap=Math.Max(_maxRenderGap,now-_lastRender);_lastRender=now;
        _state.Refresh();_canvas.Invalidate();_renderFrames++;
        if(now-_renderReportTime>=10)
        {
            StartupLog.Write($"Monitor render requests: {_renderFrames/(now-_renderReportTime):F1}/s; Draw callbacks: {_drawFrames/(now-_renderReportTime):F1}/s; maximum gap {_maxRenderGap*1000:F1} ms (includes stalls); screen/compositor limited, target {_displayRefresh.Hertz?.ToString("F3")??"compositor"} Hz");
            _renderFrames=0;_drawFrames=0;_maxRenderGap=0;_renderReportTime=now;
        }
    }
    private async Task ProbeDisplayRefreshAsync(IntPtr window)
    {
        try
        {
            // Display topology queries stay off the render/UI thread.
            var display=await Task.Run(()=>DisplayRefreshRate.Read(window));
            if(_closing)return;
            if(display!=_displayRefresh)
            {
                _displayRefresh=display;_nextRenderDue=_renderClock.Elapsed.TotalSeconds;
                StartupLog.Write($"Display refresh: {display.Device}, {display.Hertz?.ToString("F3")??"unknown"} Hz, {display.Source}");
            }
        }
        catch(Exception error){StartupLog.Write("Display refresh query failed: "+error);}
        finally{_displayProbePending=false;}
    }
    private async Task LoadDevices(bool manual=true)
    {
        int version=_deviceVersion;
        IReadOnlyList<AudioDevice> devices;
        try{devices=await Task.Run(_state.Audio.EnumerateDevices);}
        catch(Exception error){if(manual)await Message("输入设备",AudioEngine.Friendly(error));else StartupLog.Write("Device refresh failed: "+error);return;}
        bool same=_state.Devices.OrderBy(d=>d.Id).Select(d=>(d.Id,d.FriendlyName))
            .SequenceEqual(devices.OrderBy(d=>d.Id).Select(d=>(d.Id,d.Name)));
        // Output-only/default-marker notifications must not flash/disable input controls or reopen capture.
        if(!manual && _devicesLoaded && same)
        {
            _devicesDirty=version!=_deviceVersion;if(_devicesDirty)_deviceRefreshTimer.Start();return;
        }
        if(_audioBusy || _dialogOpen){_devicesDirty=true;_deviceRefreshTimer.Start();return;}
        await AudioCommand(async()=>
        {
            await _state.Audio.ReconcileDevicesAsync(devices,manual);
            _devicesLoaded=true;_devicesDirty=version!=_deviceVersion;if(_devicesDirty)_deviceRefreshTimer.Start();
            var selected=_state.Audio.SelectedDevice;
            _updatingForm=true;
            try
            {
                if(!same){_state.Devices.Clear();foreach(var device in devices)_state.Devices.Add(new(device.Id,device.Name));}
                _deviceBox.SelectedItem=_state.Devices.FirstOrDefault(d=>d.Id==selected?.Id);
            }
            finally{_updatingForm=false;}
            _state.SelectedInput=selected==null?new("","未选择输入设备"):new(selected.Id,selected.Name);
        });
    }
    private async Task CheckRawDecision()
    {
        if(_closing || _dialogOpen || _audioBusy || !_state.Audio.RawDecisionPending || _lastRawDecision==_state.Audio.RawDecisionId)return;
        _lastRawDecision=_state.Audio.RawDecisionId;_dialogOpen=true;
        try
        {
            var result=await new ContentDialog{XamlRoot=_root.XamlRoot,Title="此设备未接受 RAW 请求",
                Content="可允许当前设备在本次运行中使用普通共享监看，或停止监看。此许可不适用于正式录制；设备忙、权限和断开等错误不会降级。",
                PrimaryButtonText="允许本次普通共享监看",CloseButtonText="停止监看",RequestedTheme=ElementTheme.Dark}.ShowAsync();
            await _state.Audio.DecideRawFallbackAsync(result==ContentDialogResult.Primary);
        }
        catch(Exception error){StartupLog.Write("RAW decision: "+error);}
        finally{_dialogOpen=false;}
    }
    private async Task CalibrationSettings()
    {
        if(_dialogOpen)return;_dialogOpen=true;
        try
        {
            var content=new StackPanel{Spacing=8};content.Children.Add(Design.Text("仅管理文件；当前信号链尚未应用校准。",12,Design.Muted));
            content.Children.Add(_calibrationBox);
            var dialog=new ContentDialog{XamlRoot=_root.XamlRoot,Title="校准（预留）",Content=content,
                PrimaryButtonText="添加校准",SecondaryButtonText="管理已添加的校准",CloseButtonText="关闭",RequestedTheme=ElementTheme.Dark};
            var result=await dialog.ShowAsync();content.Children.Remove(_calibrationBox);
            if(result==ContentDialogResult.Primary)await AddCalibration();
            else if(result==ContentDialogResult.Secondary)await ManageCalibrations();
        }
        finally{_dialogOpen=false;}
    }
    private void RefreshValues()
    {
        _inputName.SetName(_state.SelectedInput.FriendlyName);
        var db=_state.Snapshot.RmsDbfs;
        _number.Text=!_state.Snapshot.Valid?"—":double.IsNegativeInfinity(db)?"−∞":$"{(db>0?"+":"")}{Math.Abs(db):0.0}";
        _minus.Visibility=double.IsFinite(db) && db<0?Visibility.Visible:Visibility.Collapsed;
        _peak.Text=_state.HeldPeak is double p?(double.IsNegativeInfinity(p)?"PEAK −∞ dBFS":$"PEAK {(p>=0?"+":"−")}{Math.Abs(p):0.0} dBFS"):"PEAK — dBFS";_peak.Foreground=_state.HeldPeak>=0?_overloadPeakInk:_normalPeakInk;
        _gain.Refresh();
        if(_state.Snapshot.MidiNote is double midi)
        {
            var n=(int)Math.Round(midi);
            if(_displayedNote!=n)
            {
                string[] names=["C","C♯","D","D♯","E","F","F♯","G","G♯","A","A♯","B"];
                _note.Inlines.Clear();
                var name=names[((n%12)+12)%12];
                _note.Inlines.Add(new Run{Text=name[..1]});
                if(name.Length>1)_note.Inlines.Add(new Run{Text="♯",FontSize=42,FontFamily=new FontFamily("Segoe UI Symbol")});
                _note.Inlines.Add(new Run{Text=$"{n/12-1}",FontSize=36});_displayedNote=n;
            }
            _frequency.Text=$"{_state.Snapshot.FrequencyHz:0.00}";
        }
        else{_displayedNote=null;_note.Text="—";_frequency.Text="—";}
    }
    private void RefreshMode()
    {
        var keepReadout=_level.Visibility==Visibility.Visible && _state.Mode!=MonitorMode.Pitch;
        _level.Visibility=_state.Mode!=MonitorMode.Pitch?Visibility.Visible:Visibility.Collapsed;
        _pitch.Visibility=_state.Mode==MonitorMode.Pitch?Visibility.Visible:Visibility.Collapsed;
        _monitor.Background=Design.Brush(Colors.Transparent);
        ToolTipService.SetToolTip(_monitor,_state.Mode switch
        {
            MonitorMode.Level=>"VU 参考：−18 dBFS 峰值的 1 kHz 正弦 = 0 VU。+3 VU 不代表数字削波；数字超限看 PEAK ≥ 0 dBFS。",
            MonitorMode.Spectrum=>"单边频点幅度 dBFS；横轴为对数 Hz，纵轴为非均匀 dBFS。显示范围止于实际 Nyquist。",
            _=>"单音检测，A4 = 440 Hz；音高不明确时不显示音名。"
        });
        Layout();_canvas.Invalidate();
        if(_started && !keepReadout)_monitorFade.In(120);
        else{_monitorFade.Stop();_monitor.Opacity=1;}
    }

    private void RefreshSession(bool animate)
    {
        var stage=_state.Session.Stage;
        var active=_state.Session.Active;
        var paused=stage==RecordingStage.Paused;
        var wasActive=_lastStage is RecordingStage.Recording or RecordingStage.Paused;
        var changed=stage!=_lastStage;
        animate &= _settings.AnimationsEnabled;
        _lastStage=stage;
        var generation=++_transportGeneration;
        _clock.Text=_state.Session.DisplayTime;
        _inputName.SetRecording(active,animate);
        if(active)DispatcherQueue.TryEnqueue(LogClockCenters);
        foreach(var control in _configuration)control.IsEnabled=!active && !_audioBusy;
        UpdateQuality();
        _stopButton.CornerRadius=active?new CornerRadius(0,5,5,0):new CornerRadius(5);
        AutomationProperties.SetName(_stopButton,active?"停止录制":"开始录制");
        ToolTipService.SetToolTip(_stopButton,active?"停止录制":"开始录制");
        AutomationProperties.SetName(_pauseButton,paused?"继续录制":"暂停录制");
        ToolTipService.SetToolTip(_pauseButton,paused?"继续录制":"暂停录制");
        _discardButton.Visibility=paused?Visibility.Visible:Visibility.Collapsed;
        _discardButton.IsEnabled=paused && !_audioBusy;
        _pauseButton.CornerRadius=paused?new CornerRadius(0):new CornerRadius(5,0,0,5);
        _pauseButton.Content=Design.ActionContent(paused?"play":"pause",paused?"继续":null);
        Design.SetBaseFill(_stopButton,active?Design.Action:Design.Record,animate&&changed);

        void PrimaryContent()
        {
            _primaryContent.Children.Clear();
            _primaryContent.Children.Add(active?Design.ActionContent("stop",paused?null:"停止",Design.Alert):Design.ActionContent("record","录制"));
        }
        if(animate&&changed)
            _primaryFade.To(0,60,()=>{PrimaryContent();_primaryFade.To(1,100);});
        else{_primaryFade.Stop();_primaryContent.Opacity=1;PrimaryContent();}

        if(active)
        {
            _pauseButton.Visibility=Visibility.Visible;
            if(animate)_pauseFade.To(1,160);else{_pauseFade.Stop();_pauseButton.Opacity=1;}
        }
        else if(animate)_pauseFade.To(0,160);
        else{_pauseFade.Stop();_pauseButton.Opacity=0;}
        var left=active?(paused?100:42):0;
        var right=active&&paused?42:100;
        AnimateWidths(left,right,active?3:0,animate?240:0,()=>
        {
            if(generation==_transportGeneration&&!_state.Session.Active)_pauseButton.Visibility=Visibility.Collapsed;
        });

        if(active)
        {
            _clockSlot.Visibility=Visibility.Visible;
            if(!wasActive&&animate)_clockFade.In(180);
            else{_clockFade.Stop();_clockSlot.Opacity=1;}
        }
        else if(animate&&wasActive)
            _clockFade.To(0,120,()=>{if(!_state.Session.Active)_clockSlot.Visibility=Visibility.Collapsed;});
        else{_clockFade.Stop();_clockSlot.Opacity=0;_clockSlot.Visibility=Visibility.Collapsed;}

        if(!active)
        {
            _statusFade.Stop();
            _sessionLabel.Opacity=1;
            return;
        }

        void StatusContent()
        {
            _sessionLabel.Text=paused?"暂停中":_state.Audio.Status.State==AudioRunState.Preparing?"正在准备":_state.Audio.Status.State==AudioRunState.Finalizing?"正在保存":"录制中";
            _statusFade.To(1,animate?100:0,()=>
            {
                if(_state.Session.Stage==RecordingStage.Paused)_statusFade.Breathe();
            });
        }
        if(animate&&changed&&active&&wasActive)_statusFade.To(0,75,StatusContent);
        else
        {
            _statusFade.Stop();_sessionLabel.Opacity=1;
            _sessionLabel.Text=paused?"暂停中":_state.Audio.Status.State==AudioRunState.Preparing?"正在准备":_state.Audio.Status.State==AudioRunState.Finalizing?"正在保存":"录制中";
            if(paused)_statusFade.Breathe();
        }
    }

    private void AnimateWidths(double left,double right,double spacing,int duration,Action completed)
    {
        var fromDiscard=_discardButton.Width;
        var fromLeft=_pauseButton.Width;
        var fromRight=_stopButton.Width;
        var fromSpacing=_activeButtons.Spacing;
        _widthAnimation?.Stop();
        var discard=_state.Session.Stage==RecordingStage.Paused?42:0;
        _discardButton.Width=discard;_discardButton.Opacity=discard>0?1:0;
        _pauseButton.Width=left;_stopButton.Width=right;_activeButtons.Spacing=spacing;
        if(duration==0){completed();return;}
        var storyboard=new Storyboard();
        void Add(DependencyObject target,string property,double from,double to)
        {
            var animation=new DoubleAnimationUsingKeyFrames{EnableDependentAnimation=true,FillBehavior=FillBehavior.Stop};
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame{KeyTime=TimeSpan.Zero,Value=from});
            animation.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime=TimeSpan.FromMilliseconds(duration),Value=to,
                KeySpline=new KeySpline{ControlPoint1=new Point(.22,1),ControlPoint2=new Point(.36,1)}
            });
            Storyboard.SetTarget(animation,target);Storyboard.SetTargetProperty(animation,property);
            storyboard.Children.Add(animation);
        }
        Add(_discardButton,"Width",fromDiscard,discard);
        Add(_pauseButton,"Width",fromLeft,left);
        Add(_stopButton,"Width",fromRight,right);
        Add(_activeButtons,"Spacing",fromSpacing,spacing);
        storyboard.Completed+=(_,_)=>completed();
        _widthAnimation=storyboard;storyboard.Begin();
    }

    private void UpdateTextScale()
    {
        _renderer.TextScale=1;
        _renderer.ReducedMotion=!_settings.AnimationsEnabled;
    }

    private void Layout()
    {
        if(!_started)return;
        _main.Width=820;_main.Height=384;
        _monitor.Height=236;_monitor.MinHeight=0;
        _renderer.Stacked=false;_renderer.CentsTop=154;
        _level.Width=200;_pitch.Width=200;
        _level.VerticalAlignment=VerticalAlignment.Center;_pitch.VerticalAlignment=VerticalAlignment.Center;
        _level.Margin=new Thickness(28,0,0,0);_pitch.Margin=_level.Margin;
        _clockSlot.Width=240;
        _canvas.Invalidate();
    }

    private void LogClockCenters()
    {
        if(!_state.Session.Active||_clockSlot.ActualWidth<1)return;
        var status=_sessionLabel.TransformToVisual(_main).TransformPoint(new Point(_sessionLabel.ActualWidth/2,0));
        var clock=_clock.TransformToVisual(_main).TransformPoint(new Point(_clock.ActualWidth/2,0));
        var dpi=_root.XamlRoot?.RasterizationScale??1;
        StartupLog.Write($"Clock alignment: status center={status.X:F3} DIP, clock center={clock.X:F3} DIP, delta={(status.X-clock.X)*dpi:F3} physical px");
    }

    private void TogglePane()
    {
        _paneOpen=!_paneOpen;
        if(_paneOpen&&!OverlayDrawer)
        {
            _frame.SetExpanded(true);
            _root.UpdateLayout();
        }
        _splitView.IsPaneOpen=_paneOpen;
        if(_paneOpen)_paneSelector.FocusSelected();
    }

    private void OnKeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key==VirtualKey.Escape&&_paneOpen){TogglePane();e.Handled=true;}
        var ctrl=(InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0;var alt=(InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0;
        if(e.Key==VirtualKey.T&&ctrl&&alt){e.Handled=true;var family=_number.FontFamily.Source=="Bahnschrift"?"Segoe UI Variable":"Bahnschrift";foreach(var text in new[]{_number,_note,_frequency,_clock})text.FontFamily=new FontFamily(family);}
    }
}
