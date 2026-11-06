using System.Diagnostics;
using PortableRecorder.Audio;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Canvas;
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
using NAudio.CoreAudioApi;

namespace PortableRecorder.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly ShellViewModel _state=new();
    private readonly MonitorRenderer _renderer=new();
    private readonly UISettings _settings=new();
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(16.67)};
    private readonly Grid _root=new(),_body=new(),_main=new(),_header=new(),_monitor=new(),_transport=new();
    private readonly Grid _inspector=new(){Background=Design.Brush(Design.Inspector),Width=264};
    private readonly CanvasSwapChainPanel _canvas=new(){IsHitTestVisible=false};
    private CanvasDevice? _renderDevice;
    private CanvasSwapChain? _swapChain;
    private readonly SettingsDrawer _drawer;
    private readonly FixedWindowFrame _frame;
    private readonly StartupPresentation _startupPresentation;
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
    private readonly ComboBox _inputFormatBox=new(),_calibrationEnabledBox=new();
    private readonly StackPanel _calibrationSection=new(){Spacing=4};
    private readonly Grid _calibrationHeading=new(){Height=24};
    private readonly Border _calibrationFileRow=new();
    private readonly Grid _inputFormatGroup=new();
    private readonly TextBlock _inputFormatHint=Design.Text("当录制模式为共享时，该选项不可用",11,Design.Muted);
    private readonly TextBlock _modeGainHint=Design.Text("",11,Design.Muted);
    private MMDeviceEnumerator? _volumeEnumerator;
    private MMDevice? _volumeDevice;
    private AudioEndpointVolume? _volumeEndpoint;
    private AudioEndpointVolumeNotificationDelegate? _volumeHandler;
    private string? _volumeDeviceId;
    private long _volumeGeneration;
    private EndpointVolumeSnapshot? _liveEndpointVolume;
    private readonly ScrollViewer _inputHost=new(),_fileHost=new();
    private InputFormatOptions? _inputFormats;
    private string? _formatsDeviceId,_inputFormatViewKey,_formatError;
    private int _formatQueryVersion;
    private bool _updatingInputFormat,_formatsBusy;
    private bool? _calibrationMotionEnabled;
    private (long Revision,string? Device,CalibrationLibrary.Selection Choice,CalibrationStatus Status)? _calibrationViewKey;
    private string _calibrationDescription="";
    private readonly ComboBox _deviceBox=new(),_calibrationBox=new(),_formatBox=new(),_qualityBox=new();
    private readonly TextBox _path=new(){PlaceholderText="选择文件夹",MinWidth=0};
    private string _savedOutputFolder="";
    private readonly StackPanel _qualityGroup=new(){Spacing=0};
    private EncodingPreferences _encodingPreferences;
    private RecordingCodec? _qualityCodec;
    private bool _updatingQuality;
    private readonly TextBlock _qualityTitle=Design.Text("压缩等级",12,Design.Muted);
    private readonly List<Control> _configuration=[];
    private readonly SolidColorBrush _linkInk=Design.Brush(Design.Link);
    private Storyboard? _widthAnimation;
    private bool _firstFrameLogged,_updatingForm,_started,_updatingCalibration,_alternateTypography;
    private TextBlock? _managerError;
    private bool _audioBusy,_closing,_closeAllowed,_dialogOpen;
    private AudioRunState? _lastAudioState;
    private readonly DialogQueue _dialogs=new();
    private readonly RawDecisionPrompt _rawPrompt=new();
    private ContentDialog? _activeDialog;
    private string? _modePreferenceWarning,_encodingPreferenceWarning;
    private string _lastNoticeKey="";
    private string _lastAudioMessage="";
    private readonly ServiceNotice _serviceNotice;
    private readonly bool _noCapture=Environment.GetCommandLineArgs().Contains("--no-capture");
    private int? _displayedNote;
    private readonly SolidColorBrush _normalPeakInk=Design.Brush(Design.White),_overloadPeakInk=Design.Brush(Design.Alert);
    private DeviceTopologyWatcher? _deviceWatcher;
    private readonly DispatcherTimer _deviceRefreshTimer=new(){Interval=TimeSpan.FromMilliseconds(300)};
    private bool _devicesDirty,_devicesLoaded;
    private int _deviceVersion,_deviceQueryVersion;
    private readonly Stopwatch _renderClock=Stopwatch.StartNew();
    private double _lastRender,_nextDisplayProbe;
    private bool _displayProbePending;
    private DisplayRefreshInfo _displayRefresh=new("unknown",null,"compositor fallback");
    private int _renderFrames,_drawFrames;
    private double _renderReportTime;
    private double _maxRenderGap;

    public MainWindow()
    {
        _encodingPreferences=_state.EncodingSettings;
        ApplyTypography(false);
        _modePreferenceWarning=_state.ModePreferenceWarning;_encodingPreferenceWarning=_state.EncodingPreferenceWarning;
        Title="URAW Recorder";ExtendsContentIntoTitleBar=true;
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory,"Assets","URAWRecorder.ico"));
        _state.Audio.Diagnostic+=StartupLog.Write;
        _startupPresentation=new StartupPresentation(this);
        _frame=new FixedWindowFrame(this,()=>_root.XamlRoot?.RasterizationScale??FixedWindowFrame.NativeScale(this));
        _frame.Initialize();
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
        var title=new Grid{Background=Design.Brush(transportSurface),Height=32};
        title.Children.Add(new TextBlock{Text=Title,FontSize=12,Foreground=Design.Brush(Design.Muted),Margin=new Thickness(16,0,140,0),VerticalAlignment=VerticalAlignment.Center});
        _root.Children.Add(title);SetTitleBar(title);
        AppWindow.TitleBar.BackgroundColor=Colors.Transparent;AppWindow.TitleBar.ButtonBackgroundColor=Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor=Design.Muted;AppWindow.TitleBar.ButtonInactiveBackgroundColor=Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor=Design.Selected;
        _root.Children.Add(_body);Grid.SetRow(_body,1);
        _main.Width=FixedWindowFrame.MainWidth;_main.Height=384;
        _main.HorizontalAlignment=HorizontalAlignment.Right;
        _main.Clip=new RectangleGeometry{Rect=new Rect(0,0,820,384)};
        _main.RowDefinitions.Add(new(){Height=new GridLength(64)});
        _main.RowDefinitions.Add(new(){Height=new GridLength(236)});
        _main.RowDefinitions.Add(new(){Height=new GridLength(84)});
        var displayPanel=MaterialSurface.DisplayPanel(displaySurface);
        _main.Children.Add(displayPanel);Grid.SetRow(displayPanel,1);
        _header.Background=Design.Brush(transportSurface);
        _main.Children.Add(_header);_main.Children.Add(_monitor);Grid.SetRow(_monitor,1);
        _main.Children.Add(_transport);Grid.SetRow(_transport,2);
        _body.Children.Add(_main);
        _settingsButton=Design.Button(MenuGlyph(),"输入与文件设置",Colors.Transparent);_settingsButton.Width=40;_settingsButton.Height=44;_settingsButton.Padding=new Thickness(0);
        _settingsButton.HorizontalAlignment=HorizontalAlignment.Left;_settingsButton.VerticalAlignment=VerticalAlignment.Center;_settingsButton.Margin=new Thickness(28,0,0,0);_settingsButton.TabIndex=0;
        _settingsButton.Click+=(_,_)=>TogglePane();_header.Children.Add(_settingsButton);
        BuildWorkModeSelector();

        _modeSelector=Design.Selector(["电平","频谱","音高"],76,2,index=>{_state.Mode=(MonitorMode)index;if(_started)RefreshMode();});_modeSelector.TabIndex=1;
        _header.Children.Add(new Border{Background=Design.Brush(MaterialSurface.Tint(Design.Hex("0A0A0A"),102)),CornerRadius=new CornerRadius(4),Padding=new Thickness(4),Child=_modeSelector,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});
        _monitor.Children.Add(_canvas);BuildReadouts();
        _transport.Background=Design.Brush(displaySurface);
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
        _clock.FontWeight=FontWeights.SemiBold;_clock.CharacterSpacing=-10;_transport.Children.Add(_clockSlot);
        _clockSlot.SizeChanged+=(_,_)=>LogClockCenters();
        _actions.Margin=new Thickness(0,0,28,0);_actions.VerticalAlignment=VerticalAlignment.Center;
        _pauseButton=Design.Button(Design.ActionContent("pause"),"暂停录制",Design.TransportAction);
        _pauseButton.Width=0;_pauseButton.Opacity=0;_pauseButton.Visibility=Visibility.Collapsed;
        _pauseButton.CornerRadius=new(5,0,0,5);_pauseButton.TabIndex=5;
        _pauseFade=new NativeFade(_pauseButton,()=>_settings.AnimationsEnabled);
        _primaryContent.Children.Add(Design.ActionContent("record","录制"));
        _stopButton=Design.Button(_primaryContent,"开始录制",Design.Record);
        _stopButton.Width=100;_stopButton.TabIndex=6;
        _pauseButton.Click+=async(_,_)=>await AudioCommand(()=>_state.Audio.PauseAsync(_state.Session.Stage!=RecordingStage.Paused));
        _stopButton.Click+=async(_,_)=>await AudioCommand(async()=>
        {
            if(_state.IsListening)await ToggleListening();
            else if(_state.Session.Active)await _state.Audio.StopRecordingAsync();
            else await _state.Audio.StartRecordingAsync(OutputFolderPreferences.Normalize(_path.Text),_state.RecordMode,_state.Encoding);
        });
        _activeButtons.Visibility=Visibility.Visible;_activeButtons.Spacing=0;
        _discardButton=Design.Button(Design.Icon("\uE74D",16,Design.White),"丢弃",Design.TransportAction);
        _discardButton.Width=0;_discardButton.Opacity=0;_discardButton.Visibility=Visibility.Collapsed;
        _discardButton.Padding=new Thickness(0);_discardButton.CornerRadius=new(5,0,0,5);_discardButton.TabIndex=5;
        _discardButton.Click+=async(_,_)=>await AudioCommand(_state.Audio.DiscardRecordingAsync);
        _activeButtons.Children.Add(_discardButton);
        _activeButtons.Children.Add(_pauseButton);_activeButtons.Children.Add(_stopButton);
        _actions.Children.Add(_activeButtons);_transport.Children.Add(_actions);
        _serviceNotice=new ServiceNotice(()=>_settings.AnimationsEnabled,ActivateNotice){MaxWidth=215};
        _serviceNotice.HorizontalAlignment=HorizontalAlignment.Right;_serviceNotice.VerticalAlignment=VerticalAlignment.Center;
        _serviceNotice.Margin=new Thickness(0,0,28,0);_header.Children.Add(_serviceNotice);
        _paneSelector=Design.Selector(["输入","文件"],84,8,index=>
        {
            _inputHost.Visibility=index==0?Visibility.Visible:Visibility.Collapsed;
            _fileHost.Visibility=index==1?Visibility.Visible:Visibility.Collapsed;
        });
        BuildInspector();UpdateSystemAccent();_settings.ColorValuesChanged+=OnSystemColorsChanged;
        _drawer=new SettingsDrawer(_body,_inspector,()=>_settings.AnimationsEnabled,
            ()=>{if(_state.IsListening)_listenScope.Focus(FocusState.Programmatic);else _paneSelector.FocusSelected();},()=>_settingsButton.Focus(FocusState.Programmatic));
        _state.PropertyChanged+=(_,_)=>RefreshValues();
        _root.Loaded+=async(_,_)=>
        {
            try
            {
                if(_started)return;
                StartupLog.Write("Root Loaded");_started=true;
                UpdateTextScale();StartupLog.Write("Text scale ready");
                _frame.Initialize();StartupLog.Write("Fixed client frame ready");
                _root.XamlRoot.Changed+=OnXamlRootChanged;
                RefreshMode();StartupLog.Write("Mode ready");
                RefreshSession(false);StartupLog.Write("Session ready");
                RefreshValues();StartupLog.Write("Values ready");
                _timer.Start();StartupLog.Write("Timer started");
                CompositionTarget.Rendering+=OnRendering;
                if(!_noCapture)
                {
                    try{_deviceWatcher=new DeviceTopologyWatcher(()=>DispatcherQueue.TryEnqueue(()=>
                        {if(_closing)return;_deviceVersion++;_devicesDirty=true;_deviceRefreshTimer.Stop();_deviceRefreshTimer.Start();}));}
                    catch(Exception error){StartupLog.Write("Device watcher unavailable: "+error);}
                    await LoadDevices(manual:false);
                }
                else StartupLog.Write("诊断模式：未打开麦克风");
            }
            catch(Exception error){StartupLog.Write("Loaded failed: "+error);if(!_closing)throw;}
        };
        _deviceRefreshTimer.Tick+=async(_,_)=>
        {
            if(_closing){_deviceRefreshTimer.Stop();return;}
            if(!_devicesDirty){_deviceRefreshTimer.Stop();return;}
            if(_audioBusy || _dialogOpen || _dialogs.IsBusy)return;
            _deviceRefreshTimer.Stop();await LoadDevices(manual:false);
        };
        _root.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_,args)=>_gain.CommitIfOutside(args)),true);
        _main.SizeChanged+=(_,_)=>Layout();_root.KeyDown+=OnKeyDown;_timer.Tick+=(_,_)=>Tick();
        _settings.AnimationsEnabledChanged+=OnAnimationsChanged;
        _settings.TextScaleFactorChanged+=OnTextScaleChanged;
        AppWindow.Closing+=async(_,args)=>
        {
            if(_closeAllowed)return;
            SaveOutputFolder();
            args.Cancel=true;if(_closing)return;_closing=true;StopVolumeWatcher();_deviceQueryVersion++;_formatQueryVersion++;_activeDialog?.Hide();_drawer.Dispose();_deviceRefreshTimer.Stop();_deviceWatcher?.Dispose();_deviceWatcher=null;
            _stopButton.IsEnabled=false;_pauseButton.IsEnabled=false;
            try{try{await _state.Listener.DisposeAsync();}finally{await _state.Audio.DisposeAsync();}}
            catch(Exception error){StartupLog.Write("Audio close: "+error);await Message("录音收尾异常",AudioEngine.Friendly(error),duringClose:true);}
            finally{_closeAllowed=true;Close();}
        };
        Closed+=(_,_)=>{_dialogs.Close();_drawer.Dispose();_settings.ColorValuesChanged-=OnSystemColorsChanged;_settings.AnimationsEnabledChanged-=OnAnimationsChanged;_settings.TextScaleFactorChanged-=OnTextScaleChanged;if(_root.XamlRoot!=null)_root.XamlRoot.Changed-=OnXamlRootChanged;CompositionTarget.Rendering-=OnRendering;_inputName.ReleaseResources();_serviceNotice.ReleaseResources();_timer.Stop();_widthAnimation?.Stop();_monitorFade.Stop();_clockFade.Stop();_statusFade.Stop();_primaryFade.Stop();_pauseFade.Stop();ReleaseRenderSurface();_renderer.Dispose();};Content=_root;
    }
    private void OnXamlRootChanged(XamlRoot sender,XamlRootChangedEventArgs args){if(!_closing)_frame.RefreshDpi();}
    private void OnAnimationsChanged(UISettings sender,object args)=>DispatcherQueue.TryEnqueue(()=>
    {if(_closing)return;UpdateTextScale();RefreshSession(false);_drawer.ApplyMotionPreference();_serviceNotice.ApplyMotionPreference();});
    private void OnTextScaleChanged(UISettings sender,object args)=>DispatcherQueue.TryEnqueue(()=>
    {if(_closing)return;UpdateTextScale();Layout();});

    private static Grid MenuGlyph()
    {
        var grid=new Grid{Width=18,Height=12};var canvas=new Canvas{Width=18,Height=12};
        foreach(var y in new[]{0,5.25,10.5}){var line=new Border{Width=18,Height=1.5,Background=Design.Brush(Design.Muted)};Canvas.SetTop(line,y);canvas.Children.Add(line);}grid.Children.Add(canvas);return grid;
    }
    private void BuildReadouts()
    {
        // Number + unit are one primary readout group; PEAK is a separate secondary metric.
        // Pull the unit into the number's line-box padding and keep the whole block slightly below
        // the monitor's geometric center so it does not read high against the VU pair.
        _level.RenderTransform=new TranslateTransform{Y=8};
        _level.Width=200;_level.Margin=new Thickness(28,0,0,0);_level.HorizontalAlignment=HorizontalAlignment.Left;_level.VerticalAlignment=VerticalAlignment.Center;
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4,Height=72};_number.FontWeight=FontWeights.SemiBold;_number.CharacterSpacing=-20;row.Children.Add(_minus);row.Children.Add(_number);_level.Children.Add(row);
        var units=Design.Text("dBFS · RMS",12,Design.Muted);units.Height=16;units.Margin=new Thickness(0,-6,0,0);_level.Children.Add(units);
        _peak.FontWeight=FontWeights.SemiBold;
        var peakRow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Height=28,Margin=new Thickness(0,24,0,0)};
        var reset=Design.Button(Design.Icon("\uE777",14),"清除历史输出峰值",Colors.Transparent);reset.Width=24;reset.Height=28;reset.Padding=new Thickness(0);reset.TabIndex=3;
        reset.Click+=async(_,_)=>await AudioCommand(_state.ResetPeakAsync);peakRow.Children.Add(_peak);peakRow.Children.Add(reset);_level.Children.Add(peakRow);_monitor.Children.Add(_level);
        _pitch.Width=200;_pitch.Margin=new Thickness(28,0,0,0);_pitch.HorizontalAlignment=HorizontalAlignment.Left;_pitch.VerticalAlignment=VerticalAlignment.Center;_pitch.Visibility=Visibility.Collapsed;
        _note.FontWeight=FontWeights.SemiBold;_frequency.FontWeight=FontWeights.SemiBold;_note.Height=72;_note.CharacterSpacing=0;_pitch.Children.Add(_note);var hz=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5,Margin=new Thickness(0,10,0,0),Height=26};hz.Children.Add(_frequency);hz.Children.Add(Design.Text("Hz",12,Design.Muted));_pitch.Children.Add(hz);_pitch.Children.Add(new Border{Height=64});_monitor.Children.Add(_pitch);
        AutomationProperties.SetName(_number,"当前输出 RMS，dBFS");AutomationProperties.SetLiveSetting(_sessionLabel,AutomationLiveSetting.Polite);
    }
    private void BuildInspector()
    {
        _inspector.RowDefinitions.Add(new(){Height=new GridLength(48)});_inspector.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var top=new Grid{Padding=new Thickness(24,0,24,0),ColumnSpacing=8};
        top.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        top.ColumnDefinitions.Add(new(){Width=new GridLength(32)});
        _paneSelector.HorizontalAlignment=HorizontalAlignment.Left;
        top.Children.Add(_paneSelector);
        var close=Design.Button(Design.Icon("\uE711",14,Design.Hex("747476")),"关闭设置",Colors.Transparent);
        close.Width=32;close.Height=32;close.Padding=new Thickness(0);
        Grid.SetColumn(close,1);close.Click+=(_,_)=>TogglePane();top.Children.Add(close);
        _inspector.Children.Add(top);
        var headerRule=new Border{Height=1,Background=Design.Brush(MaterialSurface.Tint(Colors.White,14)),VerticalAlignment=VerticalAlignment.Bottom,IsHitTestVisible=false};
        _inspector.Children.Add(headerRule);Grid.SetRow(headerRule,0);
        var pages=new Grid{Padding=new Thickness(24,12,8,12)};
        foreach(var host in new[]{_inputHost,_fileHost})
        {
            host.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;host.VerticalScrollMode=ScrollMode.Auto;
            host.HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;host.HorizontalScrollMode=ScrollMode.Disabled;
            host.IsTabStop=false;host.Padding=new Thickness(0);host.BorderThickness=new Thickness(0);
            host.Background=Design.Brush(Colors.Transparent);pages.Children.Add(host);
        }
        _inputPage.Margin=new Thickness(0,0,16,0);_filePage.Margin=new Thickness(0,0,16,0);
        _inputHost.Content=_inputPage;_fileHost.Content=_filePage;_fileHost.Visibility=Visibility.Collapsed;
        _inspector.Children.Add(pages);Grid.SetRow(pages,1);
        BuildListeningInspector(top,pages);
        // 384 DIP drawer: 48 header + 12 top padding + 312 DIP content viewport + 12 bottom padding.
        // Primary modules use 16 DIP breathing room; 8 and 4 DIP separate the format sub-field
        // and its label. The page scrolls only when the conditional calibration file row pushes
        // content past the fixed viewport instead of clipping it.
        _inputPage.Spacing=16;
        StyleField(_recordModeBox);_recordModeBox.ItemsSource=new[]{"独占","共享"};
        _recordModeBox.SelectedIndex=_state.RecordMode==RecordingMode.Exclusive?0:1;
        _recordModeBox.SelectionChanged+=(_,_)=>{SetRecordingMode(_recordModeBox.SelectedIndex==0?RecordingMode.Exclusive:RecordingMode.Shared);_inputFormatViewKey=null;SyncInputFormat();};
        AutomationProperties.SetName(_recordModeBox,"录制模式");
        var modeGroup=Group("录制模式","\uE713",_recordModeBox,4);
        _modeGainHint.TextWrapping=TextWrapping.Wrap;modeGroup.Children.Add(_modeGainHint);_inputPage.Children.Add(modeGroup);
        StyleField(_deviceBox);_deviceBox.ItemsSource=_state.Devices;_deviceBox.DisplayMemberPath="FriendlyName";
        AutomationProperties.SetName(_deviceBox,"输入设备");
        _deviceBox.SelectionChanged+=async(_,_)=>
        {
            if(_updatingForm || _audioBusy || _deviceBox.SelectedItem is not InputDevice device)return;
            _state.SelectedInput=device;
            await AudioCommand(()=>_state.Audio.SelectDeviceAsync(new(device.Id,device.FriendlyName)));
            await LoadInputFormats();
        };
        var deviceGroup=new StackPanel{Spacing=0};
        var deviceHeading=new Grid{Height=24};var deviceLabel=Label("输入设备","\uE720");deviceLabel.VerticalAlignment=VerticalAlignment.Center;deviceHeading.Children.Add(deviceLabel);
        var refresh=LinkButton("刷新");refresh.HorizontalAlignment=HorizontalAlignment.Right;
        ToolTipService.SetToolTip(refresh,"刷新输入设备并重新连接监看");refresh.Click+=async(_,_)=>await LoadDevices();deviceHeading.Children.Add(refresh);
        deviceGroup.Children.Add(deviceHeading);
        _deviceBox.Margin=new Thickness(0,4,0,0);deviceGroup.Children.Add(_deviceBox);
        var formatTitle=Design.Text("输入格式",12,Design.Muted);formatTitle.Height=16;formatTitle.Margin=new Thickness(0,8,0,0);deviceGroup.Children.Add(formatTitle);
        StyleField(_inputFormatBox);_inputFormatBox.PlaceholderText="正在读取格式";
        AutomationProperties.SetName(_inputFormatBox,"输入格式");
        _inputFormatBox.Resources["ComboBoxForegroundDisabled"]=Design.Brush(Design.Label);
        _inputFormatBox.Resources["ComboBoxBackgroundDisabled"]=Design.Brush(Design.Grid);
        _inputFormatBox.Resources["ComboBoxBorderBrushDisabled"]=Design.Brush(Colors.Transparent);
        _inputFormatBox.Resources["ComboBoxDropDownGlyphForegroundDisabled"]=Design.Brush(Design.Muted);
        _inputFormatBox.Margin=new Thickness(0,4,0,0);_inputFormatGroup.Children.Add(_inputFormatBox);deviceGroup.Children.Add(_inputFormatGroup);
        _inputFormatHint.TextWrapping=TextWrapping.Wrap;_inputFormatHint.Margin=new Thickness(0,4,0,0);
        _inputFormatHint.Visibility=_state.RecordMode==RecordingMode.Shared?Visibility.Visible:Visibility.Collapsed;
        deviceGroup.Children.Add(_inputFormatHint);
        _inputFormatBox.SelectionChanged+=async(_,_)=>
        {
            if(_updatingInputFormat||_audioBusy||_state.RecordMode!=RecordingMode.Exclusive||_inputFormatBox.SelectedItem is not ComboBoxItem item||!item.IsEnabled)return;
            await AudioCommand(()=>_state.Audio.SetInputFormatAsync(item.Tag as AudioFormat));
            _inputFormatViewKey=null;SyncInputFormat();
        };
        _inputPage.Children.Add(deviceGroup);
        StyleField(_calibrationEnabledBox);_calibrationEnabledBox.ItemsSource=new[]{"启用","禁用"};
        var calibrationLabel=Label("校准","\uE9E9");calibrationLabel.VerticalAlignment=VerticalAlignment.Center;_calibrationHeading.Children.Add(calibrationLabel);
        var manage=LinkButton("管理校准");manage.HorizontalAlignment=HorizontalAlignment.Right;manage.VerticalAlignment=VerticalAlignment.Center;
        manage.Click+=async(_,_)=>await ManageCalibrations();_calibrationHeading.Children.Add(manage);
        AutomationProperties.SetName(_calibrationEnabledBox,"校准");
        _calibrationSection.Children.Add(_calibrationHeading);
        _calibrationSection.Children.Add(_calibrationEnabledBox);
        StyleField(_calibrationBox);_calibrationBox.ItemsSource=_state.Calibrations;_calibrationBox.DisplayMemberPath="Name";
        _calibrationBox.PlaceholderText="请选择校准文件";_calibrationFileRow.Child=_calibrationBox;_calibrationFileRow.Margin=new Thickness(0,4,0,0);_calibrationFileRow.Visibility=Visibility.Collapsed;
        _calibrationSection.Children.Add(_calibrationFileRow);
        AutomationProperties.SetName(_calibrationBox,"校准文件");
        _inputPage.Children.Add(_calibrationSection);
        _calibrationEnabledBox.SelectionChanged+=async(_,_)=>
        {
            if(_updatingCalibration||_audioBusy)return;
            await AudioCommand(()=>_state.Audio.SetCalibrationEnabledAsync(_calibrationEnabledBox.SelectedIndex==0));SyncCalibration();
        };
        _calibrationBox.SelectionChanged+=async(_,_)=>
        {
            if(_updatingCalibration||_audioBusy||_calibrationBox.SelectedItem is not CalibrationFile file)return;
            await AudioCommand(()=>_state.Audio.SetCalibrationAsync(file.Id));SyncCalibration();
        };
        SyncCalibration();
        _inputPage.Children.Add(BuildVuReferenceGroup(listening:false));
        StyleField(_path);_path.VerticalContentAlignment=VerticalAlignment.Center;_path.Padding=new Thickness(12,8,12,8);_path.TextChanged+=(_,_)=>ToolTipService.SetToolTip(_path,_path.Text);
        _path.LostFocus+=(_,_)=>SaveOutputFolder();
        var pathRow=new Grid{ColumnSpacing=8};pathRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});pathRow.ColumnDefinitions.Add(new(){Width=new GridLength(36)});pathRow.Children.Add(_path);
        var browse=Design.Button(Design.Icon("\uE8B7",16,Design.White),"浏览保存文件夹",Design.Field);browse.Width=36;browse.Height=36;browse.CornerRadius=new(3);browse.Padding=new Thickness(0);Grid.SetColumn(browse,1);pathRow.Children.Add(browse);_configuration.Add(browse);browse.Click+=async(_,_)=>await BrowseFolder();
        var pathGroup=Group("保存路径","\uE8B7",pathRow);_pathError.TextWrapping=TextWrapping.Wrap;_pathError.Visibility=Visibility.Collapsed;pathGroup.Children.Add(_pathError);_filePage.Children.Add(pathGroup);
        _savedOutputFolder=OutputFolderPreferences.Load(out var folderWarning);_path.Text=OutputFolderPreferences.DisplayFolder(_savedOutputFolder);
        if(folderWarning!=null){_pathError.Text=folderWarning;_pathError.Visibility=Visibility.Visible;StartupLog.Write(folderWarning);}
        StyleField(_formatBox);_formatBox.Items.Add(new ComboBoxItem{Content="WAV · 32 位浮点",Tag="WAV"});
        _formatBox.Items.Add(new ComboBoxItem{Content="FLAC · 24 位 PCM",Tag="FLAC"});
        _formatBox.Items.Add(new ComboBoxItem{Content="MP3 · LAME VBR",Tag="MP3"});_formatBox.SelectedIndex=(int)_encodingPreferences.Codec;
        _formatBox.SelectionChanged+=(_,_)=>{if(_formatBox.SelectedIndex<0)return;_encodingPreferences=_encodingPreferences with{Codec=(RecordingCodec)_formatBox.SelectedIndex};SaveEncoding();UpdateQuality();};
        var recordingFormatGroup=Group("录制格式","\uE8A5",_formatBox);_filePage.Children.Add(recordingFormatGroup);
        StyleField(_qualityBox);_qualityTitle.Height=16;_qualityBox.Margin=new Thickness(0,4,0,0);
        _qualityGroup.Children.Add(_qualityTitle);_qualityGroup.Children.Add(_qualityBox);recordingFormatGroup.Children.Add(_qualityGroup);
        _qualityBox.SelectionChanged+=(_,_)=>
        {
            if(_updatingQuality||_qualityBox.SelectedItem is not ComboBoxItem item||item.Tag is not int value)return;
            _encodingPreferences=_encodingPreferences.Codec==RecordingCodec.Flac?_encodingPreferences with{FlacLevel=value}:_encodingPreferences with{Mp3Quality=value};SaveEncoding();
        };
        _filePage.Spacing=18;
        if(_settings.AnimationsEnabled)_qualityGroup.Transitions=new TransitionCollection{new EntranceThemeTransition()};

        _fileHint.TextWrapping=TextWrapping.Wrap;_filePage.Children.Add(_fileHint);UpdateQuality();
    }
    private static StackPanel Label(string text,string glyph){var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Height=20};row.Children.Add(Design.Icon(glyph,16,Design.Label));row.Children.Add(Design.Text(text,13,Design.Label));return row;}
    private static StackPanel Group(string title,string glyph,UIElement field,double spacing=8){var panel=new StackPanel{Spacing=spacing};panel.Children.Add(Label(title,glyph));panel.Children.Add(field);return panel;}
    private Button LinkButton(string text)
    {
        var label=Design.Text(text,13);label.Foreground=_linkInk;
        var button=Design.Button(label,text,Colors.Transparent);ToolTipService.SetToolTip(button,null);button.Height=24;button.Padding=new Thickness(0);
        button.HorizontalAlignment=HorizontalAlignment.Left;
        button.IsEnabledChanged+=(_,_)=>label.Foreground=button.IsEnabled?_linkInk:Design.Brush(Design.Muted);
        _configuration.Add(button);return button;
    }
    private void OnSystemColorsChanged(UISettings sender,object args)
        =>DispatcherQueue.TryEnqueue(()=>{if(!_closing)UpdateSystemAccent();});
    private void UpdateSystemAccent()
    {
        // A Windows-generated light accent shade stays legible on the fixed dark surfaces.
        try{_linkInk.Color=_settings.GetColorValue(UIColorType.AccentLight2);}
        catch{_linkInk.Color=Design.Blue;}
        _gain.SetAccent(_linkInk.Color);
    }
    private void StyleField(Control field)
    {
        field.Height=36;field.MinHeight=36;field.MinWidth=0;field.FontSize=13;field.Foreground=Design.Brush(Design.White);field.Background=Design.Brush(Design.Field);field.BorderThickness=new Thickness(0);field.CornerRadius=new CornerRadius(3);field.Padding=new Thickness(12,0,12,0);field.HorizontalAlignment=HorizontalAlignment.Stretch;
        foreach(var key in new[]{"ComboBoxBackground","ComboBoxBackgroundPointerOver","ComboBoxBackgroundPressed","ComboBoxBackgroundDisabled","TextControlBackground","TextControlBackgroundPointerOver","TextControlBackgroundFocused","TextControlBackgroundDisabled"})field.Resources[key]=Design.Brush(Design.Field);
        foreach(var key in new[]{"ComboBoxBorderBrush","ComboBoxBorderBrushPointerOver","ComboBoxBorderBrushPressed","TextControlBorderBrush","TextControlBorderBrushPointerOver"})field.Resources[key]=Design.Brush(Colors.Transparent);
        field.Resources["TextControlBorderBrushFocused"]=Design.Brush(Design.White);field.Resources["TextControlPlaceholderForeground"]=Design.Brush(Design.Hex("BDBDC2"));_configuration.Add(field);
    }
    private void UpdateQuality()
    {
        var codec=_encodingPreferences.Codec;_state.Encoding=_encodingPreferences.Encoding;
        _qualityGroup.Visibility=codec==RecordingCodec.Wav?Visibility.Collapsed:Visibility.Visible;
        _qualityTitle.Text=codec==RecordingCodec.Mp3?"压缩品质":"压缩等级";
        AutomationProperties.SetName(_qualityBox,_qualityTitle.Text);
        if(codec!=RecordingCodec.Wav&&_qualityCodec!=codec)
        {
            _updatingQuality=true;
            try
            {
                _qualityBox.Items.Clear();
                for(int value=0;value<=(codec==RecordingCodec.Flac?8:9);value++)
                {
                    string label=codec==RecordingCodec.Flac?$"等级 {value}"+(value==0?" · 最快":value==5?" · 标准":value==8?" · 最小体积":"")
                        :$"V{value}"+(value==0?" · 最高品质":value==2?" · 推荐":value==9?" · 最小体积":"");
                    var item=new ComboBoxItem{Content=label,Tag=value};_qualityBox.Items.Add(item);
                    if(value==(codec==RecordingCodec.Flac?_encodingPreferences.FlacLevel:_encodingPreferences.Mp3Quality))_qualityBox.SelectedItem=item;
                }
            }
            finally{_updatingQuality=false;}
        }
        _qualityCodec=codec;
        _fileHint.Text=codec switch
        {
            RecordingCodec.Flac=>"等级影响体积与编码耗时，音质相同。\n浮点量化至 24 位；超限削波并提示，继续录制。",
            RecordingCodec.Mp3=>"有损 VBR；V 值越小，品质越高。\n限单／双声道、标准 8–48 kHz 输入。\n超限削波并提示，继续录制；不自动混音或重采样。",
            _=>"32 位浮点 WAV / RF64，保留超限数值。\n采样率、声道数沿用实际输入。"
        };
        ToolTipService.SetToolTip(_fileHint,"未设置路径时保存到‘音乐\\Portable Recorder’。\n压缩格式会在停止时完成编码和文件收尾；保存成功前请等待。\nFLAC 对量化后的 24 位 PCM 无损；MP3 有损，监看仍基于编码前信号。");
    }
    private void SaveEncoding()
    {
        _state.Encoding=_encodingPreferences.Encoding;
        try{_encodingPreferences.Save();_encodingPreferenceWarning=null;}catch(Exception error){_encodingPreferenceWarning="录制格式偏好未保存："+error.Message;StartupLog.Write(_encodingPreferenceWarning);}
    }
    private async Task BrowseFolder()
    {
        if(_state.Session.Active||_closing)return;
        try{var result=await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();if(_closing)return;if(result!=null){_path.Text=result.Path;SaveOutputFolder();}}
        catch(Exception error){StartupLog.Write("Folder picker: "+error);if(_closing)return;_pathError.Text="暂时无法打开文件夹选择器，请手动输入路径。";_pathError.Visibility=Visibility.Visible;}
    }
    private void SaveOutputFolder()
    {
        if(string.IsNullOrWhiteSpace(_path.Text))
        {
            try{OutputFolderPreferences.Save("");_savedOutputFolder="";_path.Text=OutputFolderPreferences.DisplayFolder("");_pathError.Visibility=Visibility.Collapsed;}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {_pathError.Text="保存路径未记住："+error.Message;_pathError.Visibility=Visibility.Visible;StartupLog.Write(_pathError.Text);}
            return;
        }
        if(_savedOutputFolder==""&&_path.Text==OutputFolderPreferences.DisplayFolder(""))return;
        if(_path.Text==_savedOutputFolder)return;
        try
        {
            OutputFolderPreferences.Save(_path.Text);_savedOutputFolder=_path.Text;
            _pathError.Visibility=Visibility.Collapsed;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _pathError.Text="保存路径未记住："+error.Message;_pathError.Visibility=Visibility.Visible;
            StartupLog.Write(_pathError.Text);
        }
    }
    private async Task AddCalibration()
    {
        if(_state.Session.Active||_closing)return;
        try
        {
            var picker=new FileOpenPicker(AppWindow.Id);picker.FileTypeFilter.Add(".txt");picker.FileTypeFilter.Add(".cal");var files=await picker.PickMultipleFilesAsync();if(_closing || _state.Session.Active || files is null)return;
            foreach(var file in files)
            {
                await _state.Audio.ImportCalibrationAsync(file.Path);
                if(_closing)return;
                SyncCalibration();
            }
        }
        catch(Exception error){await Message("校准文件",error.Message);}
    }
    private async Task ManageCalibrations()
    {
        if(_closing||_state.Session.Active||_dialogOpen||_dialogs.IsBusy)return;_dialogOpen=true;
        try
        {
            while(!_closing&&!_state.Session.Active)
            {
                var managerError=Design.Text("",12,Design.Alert);managerError.TextWrapping=TextWrapping.Wrap;managerError.Visibility=Visibility.Collapsed;_managerError=managerError;
                var list=new ListView{Height=160,SelectionMode=ListViewSelectionMode.None,IsItemClickEnabled=false,
                    HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(0)};
                ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
                ScrollViewer.SetVerticalScrollBarVisibility(list,ScrollBarVisibility.Auto);
                void Rebuild()
                {
                    list.Items.Clear();
                    foreach(var file in _state.Calibrations.ToArray())
                    {
                        var row=new Grid{Height=40,ColumnSpacing=8,Padding=new Thickness(12,0,8,0)};
                        var rowSurface=new Border{CornerRadius=new CornerRadius(4),Background=Design.Brush(Colors.Transparent),Child=row};
                        row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
                        row.ColumnDefinitions.Add(new(){Width=new GridLength(52)});
                        var name=Design.Text(file.Name,13);name.TextTrimming=TextTrimming.CharacterEllipsis;
                        ToolTipService.SetToolTip(name,file.Path);row.Children.Add(name);
                        var remove=new Button{Content="移除",Width=52,Height=28,MinHeight=0,MinWidth=0,Padding=new Thickness(10,0,10,0),
                            FontSize=12,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right,
                            Opacity=0,IsHitTestVisible=false};
                        ToolTipService.SetToolTip(remove,"移除校准文件");AutomationProperties.SetName(remove,"移除校准文件："+file.Name);
                        Grid.SetColumn(remove,1);row.Children.Add(remove);
                        var item=new ListViewItem{Content=rowSurface,Padding=new Thickness(0),MinHeight=40,CornerRadius=new CornerRadius(4),HorizontalContentAlignment=HorizontalAlignment.Stretch};
                        item.Resources["ListViewItemBackgroundPointerOver"]=Design.Brush(Colors.Transparent);
                        item.Resources["ListViewItemBackgroundSelected"]=Design.Brush(Colors.Transparent);
                        item.Resources["ListViewItemBackgroundSelectedPointerOver"]=Design.Brush(Colors.Transparent);
                        bool hovered=false;
                        void ShowRemove(bool show){remove.Opacity=show?1:0;remove.IsHitTestVisible=show;}
                        void ShowHover(bool show)=>rowSurface.Background=Design.Brush(show?Design.Selected:Colors.Transparent);
                        item.PointerEntered+=(_,_)=>{hovered=true;ShowRemove(true);ShowHover(true);};
                        item.PointerExited+=(_,_)=>{hovered=false;if(remove.FocusState==FocusState.Unfocused)ShowRemove(false);ShowHover(false);};
                        item.LostFocus+=(_,_)=>{if(!hovered&&remove.FocusState==FocusState.Unfocused)ShowRemove(false);};
                        remove.GotFocus+=(_,_)=>ShowRemove(true);
                        remove.LostFocus+=(_,_)=>{if(!hovered)ShowRemove(false);};
                        remove.Click+=async(_,_)=>
                        {
                            if(_closing||_state.Session.Active||_audioBusy||!ReferenceEquals(_managerError,managerError))return;
                            managerError.Visibility=Visibility.Collapsed;
                            await AudioCommand(()=>_state.Audio.RemoveCalibrationAsync(file.Id!));
                            if(_closing||!ReferenceEquals(_managerError,managerError))return;SyncCalibration();Rebuild();
                        };
                        list.Items.Add(item);
                    }
                    if(list.Items.Count==0)list.Items.Add(new ListViewItem{Content=Design.Text("尚未添加校准文件",13,Design.Muted),IsEnabled=false});
                }
                SyncCalibration();Rebuild();var content=new StackPanel{Spacing=8};content.Children.Add(_managerError);content.Children.Add(list);
                var dialog=new ContentDialog{XamlRoot=_root.XamlRoot,Title="已添加的校准",Content=content,
                    PrimaryButtonText="添加校准",CloseButtonText="关闭",RequestedTheme=ElementTheme.Dark};
                if(await ShowDialog(dialog)!=ContentDialogResult.Primary||_closing)return;
                _managerError=null;await AddCalibration();
            }
        }
        catch(OperationCanceledException)when(_closing){}
        catch(Exception error){StartupLog.Write("Calibration manager: "+error);await Message("管理校准",error.Message);}
        finally{_dialogOpen=false;_managerError=null;}
    }
    private async Task<ContentDialogResult> ShowDialog(ContentDialog dialog,bool duringClose=false)
    {
        if(_closeAllowed||(_closing&&!duringClose))return ContentDialogResult.None;
        try
        {
            return await _dialogs.ShowAsync(async cancellation=>
            {
                if(_closeAllowed||(_closing&&!duringClose))return ContentDialogResult.None;
                _activeDialog=dialog;
                using var registration=cancellation.Register(()=>DispatcherQueue.TryEnqueue(()=>dialog.Hide()));
                try{return await dialog.ShowAsync();}
                finally{if(ReferenceEquals(_activeDialog,dialog))_activeDialog=null;}
            });
        }
        catch(OperationCanceledException)when(_closing){return ContentDialogResult.None;}
    }
    private async Task Message(string title,string text,bool duringClose=false)
    {
        try{await ShowDialog(new ContentDialog{XamlRoot=_root.XamlRoot,Title=title,Content=text,CloseButtonText="关闭",RequestedTheme=ElementTheme.Dark},duringClose);}
        catch(Exception error){StartupLog.Write("Message dialog failed: "+error);if(!_closing)_serviceNotice.SetNotice(text,text);}
    }
    private async Task ActivateNotice(NoticeData notice)
    {
        if(notice.Folder!=null)
        {
            try
            {
                if(!System.IO.Directory.Exists(notice.Folder)) throw new System.IO.DirectoryNotFoundException("保存文件夹不存在："+notice.Folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName=notice.Folder, UseShellExecute=true });
                return;
            }
            catch(Exception error) { await Message("无法打开文件夹", error.Message+"\n"+notice.Details); return; }
        }
        await Message("通知详情",notice.Details);
    }
    private void Tick()
    {
        if(_closing)return;
        _state.Refresh();
        if(_state.IsListening){RefreshListening();RefreshValues();return;}
        var status=_state.Audio.Status;
        if(status.State!=_lastAudioState){_lastAudioState=status.State;RefreshSession(true);}
        RefreshValues();_clock.Text=_state.Session.DisplayTime;
        var transport=TransportPresentation.From(status.State,_audioBusy,_state.Audio.CalibrationReadyToRecord);
        _stopButton.IsEnabled=transport.CanStop;_discardButton.IsEnabled=transport.CanDiscard;_pauseButton.IsEnabled=transport.CanPause;
        var capture=status.Capture;
        _inputName.SetChannels(capture?.Format.Channels??0);
        EnsureVolumeWatcher();SyncCalibration();SyncInputFormat();
        if(status.Message!=_lastAudioMessage){_lastAudioMessage=status.Message;StartupLog.Write("Audio: "+status.State+" / "+status.Message);}
        string preferenceWarning=string.Join("；",new[]{_modePreferenceWarning,_encodingPreferenceWarning}.Where(w=>!string.IsNullOrWhiteSpace(w)));
        bool clipped=_state.Audio.RecordingClippedSamples>0;
        bool unconfirmed=_state.Audio.RecordingUnconfirmedDiscontinuities>0 && status.State is AudioRunState.Recording or AudioRunState.Paused;
        string noticeKey=$"{status.State}|{status.Message}|{status.FilePath}|{status.Partial}|{preferenceWarning}|{_state.Audio.CalibrationReadyToRecord}|{clipped}|{unconfirmed}";
        if(noticeKey!=_lastNoticeKey)
        {
            _lastNoticeKey=noticeKey;
            string notice=unconfirmed?"缺帧未确认":clipped?"录音继续中：压缩文件出现削波。降低增益或使用浮点 WAV 可避免超限损失。":status.State==AudioRunState.Monitoring&&!_state.Audio.CalibrationReadyToRecord
                ?"校准尚未生效，请选择有效文件或禁用校准。":status.State is AudioRunState.Faulted or AudioRunState.Stopped || status.FilePath!=null && ! _state.Session.Active
                ?status.Message:status.State==AudioRunState.Monitoring && _state.Audio.Latest.RecoveredDiscontinuities>0
                ?$"监看已恢复（短暂不连续 {_state.Audio.Latest.RecoveredDiscontinuities} 次）":"";
            string shown=notice.Length==0?preferenceWarning:notice;
            string details=shown+(shown==status.Message?"":"\n"+status.Message)+(preferenceWarning.Length==0?"":"\n"+preferenceWarning)+(status.FilePath==null?"":"\n"+status.FilePath)+(status.Partial?"\n文件可能不完整。":"")+(status.State==AudioRunState.Monitoring&&!_state.Audio.CalibrationReadyToRecord?"\n"+CalibrationDescription():"");
            bool saved=status.State!=AudioRunState.Faulted && !status.Partial && !clipped && !unconfirmed && preferenceWarning.Length==0 && status.Message.StartsWith("已保存：",StringComparison.Ordinal) && !status.Message.Contains("；") && status.FilePath!=null && !_state.Session.Active;
            _serviceNotice.SetNotice(shown,details,saved?System.IO.Path.GetDirectoryName(status.FilePath):null);
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
        catch(Exception error){StartupLog.Write("Audio command: "+error);if(_dialogOpen&&_managerError!=null){_managerError.Text=AudioEngine.Friendly(error);_managerError.Visibility=Visibility.Visible;}else await Message("音频服务",AudioEngine.Friendly(error));}
        finally{_audioBusy=false;if(!_closing){_state.Refresh();RefreshSession(true);RefreshValues();SyncCalibration();SyncInputFormat();}}
    }
    private void SetRecordingMode(RecordingMode mode)
    {
        _state.RecordMode=mode;
        try{RecordingPreferences.Save(mode);_modePreferenceWarning=null;}catch(Exception error){_modePreferenceWarning="录制模式偏好未保存："+error.Message;StartupLog.Write(_modePreferenceWarning);}
    }
    private void ReleaseRenderSurface()
    {
        _canvas.SwapChain=null;_swapChain?.Dispose();_swapChain=null;
        _renderer.ResetDeviceResources();_renderDevice?.Dispose();_renderDevice=null;
    }
    private void RenderMonitor()
    {
        if(!_started||_closing||_canvas.ActualWidth<=0||_canvas.ActualHeight<=0)return;
        float width=(float)_canvas.ActualWidth,height=(float)_canvas.ActualHeight;
        float dpi=96*(float)(_root.XamlRoot?.RasterizationScale??1);
        try
        {
            _renderDevice??=new CanvasDevice();
            if(_swapChain==null)
            {
                _swapChain=new CanvasSwapChain(_renderDevice,width,height,dpi,Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,2,CanvasAlphaMode.Premultiplied);
                _canvas.SwapChain=_swapChain;
            }
            else if(_swapChain.Size.Width!=width||_swapChain.Size.Height!=height||_swapChain.Dpi!=dpi)
                _swapChain.ResizeBuffers(width,height,dpi);
            using(var drawing=_swapChain.CreateDrawingSession(Colors.Transparent))
                _renderer.Draw(_swapChain,drawing,_state);
            // CompositionTarget already supplies display pacing; do not wait for another vblank on the UI thread.
            _swapChain.Present(0);_drawFrames++;
            if(!_firstFrameLogged){_firstFrameLogged=true;StartupLog.Write($"First swap-chain monitor frame drawn: {width} x {height}");_startupPresentation.FrameSubmitted();}
        }
        catch(Exception error)when(_renderDevice?.IsDeviceLost(error.HResult)==true)
        {StartupLog.Write("Monitor graphics device lost; recreating resources: "+error.Message);ReleaseRenderSurface();}
        catch(Exception error){StartupLog.Write("Monitor draw failed: "+error);throw;}
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
        // Rendering is already paced by the compositor. A second near-vblank deadline
        // test skips otherwise usable frames when the callback arrives slightly early.
        _maxRenderGap=Math.Max(_maxRenderGap,now-_lastRender);_lastRender=now;
        _state.Refresh();RenderMonitor();_renderFrames++;
        if(now-_renderReportTime>=10)
        {
            StartupLog.Write($"Monitor composition ticks: {_renderFrames/(now-_renderReportTime):F1}/s; Presented frames: {_drawFrames/(now-_renderReportTime):F1}/s; maximum gap {_maxRenderGap*1000:F1} ms (includes stalls); screen/compositor limited, target {_displayRefresh.Hertz?.ToString("F3")??"compositor"} Hz");
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
                _displayRefresh=display;
                StartupLog.Write($"Display refresh: {display.Device}, {display.Hertz?.ToString("F3")??"unknown"} Hz, {display.Source}");
            }
        }
        catch(Exception error){StartupLog.Write("Display refresh query failed: "+error);}
        finally{_displayProbePending=false;}
    }
    private async Task LoadDevices(bool manual=true)
    {
        if(_closing)return;
        if(_state.IsListening){_devicesDirty=false;_deviceRefreshTimer.Stop();return;}
        int query=++_deviceQueryVersion,version=_deviceVersion;
        IReadOnlyList<AudioDevice> devices;
        try{devices=await Task.Run(_state.Audio.EnumerateDevices);}
        catch(Exception error){if(_closing||query!=_deviceQueryVersion)return;if(manual)await Message("输入设备",AudioEngine.Friendly(error));else StartupLog.Write("Device refresh failed: "+error);return;}
        if(_closing||query!=_deviceQueryVersion)return;
        bool same=_state.Devices.OrderBy(d=>d.Id).Select(d=>(d.Id,d.FriendlyName))
            .SequenceEqual(devices.OrderBy(d=>d.Id).Select(d=>(d.Id,d.Name)));
        // Output-only/default-marker notifications must not flash/disable input controls or reopen capture.
        if(!manual && _devicesLoaded && same)
        {
            _devicesDirty=version!=_deviceVersion;if(_devicesDirty)_deviceRefreshTimer.Start();return;
        }
        if(_audioBusy || _dialogOpen || _dialogs.IsBusy){_devicesDirty=true;_deviceRefreshTimer.Start();return;}
        await AudioCommand(async()=>
        {
            await _state.Audio.ReconcileDevicesAsync(devices,manual);
            if(_closing||query!=_deviceQueryVersion)return;
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
        if(!_closing&&!_state.Session.Active&&(manual||_formatsDeviceId!=_state.Audio.SelectedDevice?.Id||_inputFormats==null))await LoadInputFormats();
    }
    private async Task CheckRawDecision()
    {
        var decision=_state.Audio.RawDecisionId;
        if(_closing||_dialogOpen||_dialogs.IsBusy||_audioBusy||!_state.Audio.RawDecisionPending||!_rawPrompt.TryBegin(decision))return;
        try
        {
            var result=await ShowDialog(new ContentDialog{XamlRoot=_root.XamlRoot,Title="此设备未接受 RAW 请求",
                Content="可允许当前设备在本次运行中使用普通共享监看，或停止监看。此许可不适用于正式录制；设备忙、权限和断开等错误不会降级。",
                PrimaryButtonText="允许本次普通共享监看",CloseButtonText="停止监看",RequestedTheme=ElementTheme.Dark});
            if(_closing||decision!=_state.Audio.RawDecisionId)return;
            await _state.Audio.DecideRawFallbackAsync(result==ContentDialogResult.Primary);
            _rawPrompt.Complete(decision);
        }
        catch(Exception error){StartupLog.Write("RAW decision: "+error);}
        finally{_rawPrompt.Abandon(decision);}
    }
    private void SyncCalibration()
    {
        if(_closing)return;
        if(_calibrationMotionEnabled!=_settings.AnimationsEnabled)
        {
            _calibrationMotionEnabled=_settings.AnimationsEnabled;
            _calibrationSection.ChildrenTransitions=_settings.AnimationsEnabled?new TransitionCollection{new RepositionThemeTransition()}:new TransitionCollection();
            _calibrationFileRow.Transitions=_settings.AnimationsEnabled?new TransitionCollection{new EntranceThemeTransition{IsStaggeringEnabled=false}}:new TransitionCollection();
        }
        var library=_state.Audio.Calibrations.Snapshot;var choice=_state.Audio.CalibrationSelection;var status=_state.Audio.Calibration;
        var key=(library.Revision,_state.Audio.SelectedDevice?.Id,choice,status);
        if(_calibrationViewKey!=key)
        {
            _calibrationViewKey=key;_updatingCalibration=true;
            try
            {
                _state.RefreshCalibrations();
                _calibrationEnabledBox.SelectedIndex=choice.Enabled?0:1;
                _calibrationBox.SelectedItem=_state.SelectedCalibration;
                _calibrationBox.PlaceholderText=_state.Calibrations.Count==0?"请先添加校准文件":"请选择校准文件";
                bool wasVisible=_calibrationFileRow.Visibility==Visibility.Visible;
                _calibrationFileRow.Visibility=choice.Enabled?Visibility.Visible:Visibility.Collapsed;
                if(choice.Enabled&&!wasVisible&&_calibrationFileRow.IsLoaded)
                    DispatcherQueue.TryEnqueue(()=>{if(!_closing&&_calibrationFileRow.Visibility==Visibility.Visible)_calibrationFileRow.StartBringIntoView(new BringIntoViewOptions{AnimationDesired=_settings.AnimationsEnabled});});
            }
            finally{_updatingCalibration=false;}
            _calibrationDescription=DescribeCalibration(status,choice);
            ToolTipService.SetToolTip(_calibrationEnabledBox,_calibrationDescription);
            ToolTipService.SetToolTip(_calibrationHeading,_calibrationDescription);
        }
        _calibrationBox.IsEnabled=choice.Enabled&&_state.Calibrations.Count>0&&!_state.Session.Active&&!_audioBusy;
    }
    private string CalibrationDescription()=>_calibrationDescription;
    private static string DescribeCalibration(CalibrationStatus status,CalibrationLibrary.Selection choice)
    {
        string details=status.Applied?$"{status.Name}\n频响补偿已应用于全部 {status.Channels} 声道、全部仪表及录音文件。\n固定处理延迟 {status.LatencyMs:0.0} ms · 拟合误差 {status.MaximumFitErrorDb:0.000} dB":choice.Enabled?"校准尚未生效，请选择有效文件。":"校准已禁用；保留上次文件选择。";
        if(status.Applied)details+=$"\n频段 {status.MinimumHz:0.###}～{status.MaximumHz:0.###} Hz";
        return details+"\n"+status.Notice+"\n录制和暂停期间配置锁定；文件记录麦克风响应，处理时反向补偿。";
    }
    private async Task LoadInputFormats()
    {
        if(_closing)return;
        int version=++_formatQueryVersion;var device=_state.Audio.SelectedDevice;
        _formatsDeviceId=device?.Id;_inputFormats=null;_formatError=null;_formatsBusy=device!=null;_inputFormatViewKey=null;SyncInputFormat();
        if(device==null||_noCapture){_formatsBusy=false;SyncInputFormat();return;}
        try
        {
            var options=await _state.Audio.QueryInputFormatsAsync();
            if(!_closing&&version==_formatQueryVersion&&device.Id==_state.Audio.SelectedDevice?.Id)_inputFormats=options;
        }
        catch(Exception e)
        {
            StartupLog.Write("Input format query: "+e);
            if(!_closing&&version==_formatQueryVersion)_formatError="输入格式读取失败："+AudioEngine.Friendly(e);
        }
        finally{if(!_closing&&version==_formatQueryVersion){_formatsBusy=false;_inputFormatViewKey=null;SyncInputFormat();}}
    }
    private static string FormatName(AudioFormat format)=>InputFormatPresentation.Name(format);
    private void SyncInputFormat()
    {
        if(_closing)return;
        var audio=_state.Audio;bool shared=_state.RecordMode==RecordingMode.Shared;
        var hintVisibility=Visibility.Visible;
        if(_inputFormatHint.Visibility!=hintVisibility)_inputFormatHint.Visibility=hintVisibility;
        var actual=audio.Status.Capture;
        var live=actual is{Exclusive:false}?actual.Format:_inputFormats?.Shared;
        var preferred=audio.PreferredInputFormat;
        string key=$"{shared}|{_formatsDeviceId}|{_formatsBusy}|{_inputFormats?.GetHashCode()}|{live}|{preferred}|{actual?.Exclusive}|{actual?.Raw}|{_liveEndpointVolume}|{_formatError}";
        bool available=audio.SelectedDevice!=null&&!_formatsBusy;
        _inputFormatBox.IsEnabled=InputFormatPresentation.CanSelect(_state.RecordMode,available,_formatsBusy,_inputFormats!=null,_audioBusy,_state.Session.Active);
        if(key==_inputFormatViewKey)return;_inputFormatViewKey=key;_updatingInputFormat=true;
        string endpointGain=InputFormatPresentation.EndpointGain(_liveEndpointVolume,actual?.Exclusive==true);
        _inputFormatHint.Text=shared?"共享格式沿用 Windows 设置。":"仅当录制时切换为独占。";
        _modeGainHint.Inlines.Clear();var volume=_liveEndpointVolume;
        string? unavailable=InputFormatPresentation.AlignmentUnavailable(volume);
        if(unavailable!=null)_modeGainHint.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run{Text=unavailable+"。"});
        else if(shared)_modeGainHint.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run{Text=InputFormatPresentation.FormatGain(volume!.Decibels)+" 的软件增益由 Windows 音频管理控制。"});
        else
        {
            _modeGainHint.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run{Text="欲对齐 Windows 软件增益后的电平，需 "});
            var link=new Microsoft.UI.Xaml.Documents.Hyperlink{Foreground=_linkInk,UnderlineStyle=Microsoft.UI.Xaml.Documents.UnderlineStyle.None};
            link.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run{Text=InputFormatPresentation.FormatGain(volume!.Decibels)});
            link.Click+=async(_,_)=>await ApplyWindowsGain();_modeGainHint.Inlines.Add(link);
            _modeGainHint.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run{Text="。"});
        }
        ToolTipService.SetToolTip(_modeGainHint,"点击设置软件增益会立即影响监看与录音，可能造成削波。其他处理差异可能使电平不完全一致。");
        try
        {
            _inputFormatBox.Items.Clear();_inputFormatBox.PlaceholderText=_formatsBusy?"正在读取格式":"无法读取输入格式";
            if(shared)
            {
                if(live!=null){_inputFormatBox.Items.Add(new ComboBoxItem{Content=FormatName(live)});_inputFormatBox.SelectedIndex=0;}
            }
            else if(_inputFormats!=null)
            {
                var auto=new ComboBoxItem{Content="自动（设备推荐）"};_inputFormatBox.Items.Add(auto);ComboBoxItem selected=auto;
                foreach(var format in _inputFormats.Exclusive)
                {
                    var item=new ComboBoxItem{Content=FormatName(format),Tag=format};ToolTipService.SetToolTip(item,format.Description);_inputFormatBox.Items.Add(item);
                    if(preferred==format)selected=item;
                }
                if(preferred!=null&&!_inputFormats.Exclusive.Contains(preferred))
                {selected=new ComboBoxItem{Content="所选格式已不可用",Tag=preferred,IsEnabled=false};_inputFormatBox.Items.Add(selected);}
                _inputFormatBox.SelectedItem=selected;
            }
        }
        finally{_updatingInputFormat=false;}
        string explanation=shared?"共享模式沿用 Windows 当前格式；请在 Windows 设置中更改系统格式。":"此处选择独占录制的目标格式；待机和停止后仍为共享监看。自动以外的选择不会静默改用其他格式。";
        if(!shared)explanation+="\n独占录制设置："+(preferred?.Description??"自动（设备推荐）");
        if(actual!=null)explanation+=$"\n当前实际{(actual.Exclusive?"独占":"共享")} · {actual.Format.Description} · {(actual.Raw?"RAW 请求已接受":"普通输入（仅本次许可）")}";
        else explanation+="\n当前没有有效监看输入；若显示格式，它是最近查询的系统格式，不代表正在采集。";
        explanation+="\n"+endpointGain+"\nRAW 请求不代表绕过共享软件音量。";
        if(audio.InputFormats.Warning!=null)explanation+="\n"+audio.InputFormats.Warning;
        if(_formatError!=null)explanation+="\n"+_formatError;
        ToolTipService.SetToolTip(_inputFormatGroup,explanation);ToolTipService.SetToolTip(_inputFormatBox,explanation);
    }
    private void StopVolumeWatcher()
    {
        _volumeGeneration++;_volumeDeviceId=null;_liveEndpointVolume=null;
        try{if(_volumeEndpoint!=null&&_volumeHandler!=null)_volumeEndpoint.OnVolumeNotification-=_volumeHandler;}
        catch(Exception error){StartupLog.Write("Volume notification unsubscribe: "+error);}
        _volumeHandler=null;_volumeEndpoint=null;
        try{_volumeDevice?.Dispose();}catch(Exception error){StartupLog.Write("Volume device dispose: "+error);}
        _volumeDevice=null;
        try{_volumeEnumerator?.Dispose();}catch(Exception error){StartupLog.Write("Volume enumerator dispose: "+error);}
        _volumeEnumerator=null;
    }
    private void EnsureVolumeWatcher()
    {
        string? id=_noCapture?null:_state.Audio.SelectedDevice?.Id;
        if(id==_volumeDeviceId)return;
        StopVolumeWatcher();_inputFormatViewKey=null;
        if(id==null)return;
        _volumeDeviceId=id;
        try
        {
            _volumeEnumerator=new MMDeviceEnumerator();_volumeDevice=_volumeEnumerator.GetDevice(id);
            _volumeEndpoint=_volumeDevice.AudioEndpointVolume;
            _volumeHandler=_=>DispatcherQueue.TryEnqueue(()=>{if(!_closing&&_volumeDeviceId==id)RefreshEndpointVolume(id);});
            _volumeEndpoint.OnVolumeNotification+=_volumeHandler;
            RefreshEndpointVolume(id);
        }
        catch(Exception error)
        {
            StartupLog.Write("Volume watcher unavailable: "+error);
            StopVolumeWatcher();_volumeDeviceId=id;_inputFormatViewKey=null;
        }
    }
    private async void RefreshEndpointVolume(string id)
    {
        var device=_state.Audio.SelectedDevice;if(device==null||device.Id!=id)return;
        long generation=++_volumeGeneration;
        try
        {
            var volume=await _state.Audio.QueryEndpointVolumeAsync(device);
            if(_closing||generation!=_volumeGeneration||_volumeDeviceId!=id||_state.Audio.SelectedDevice?.Id!=id)return;
            _liveEndpointVolume=volume;_inputFormatViewKey=null;SyncInputFormat();
        }
        catch(Exception error)
        {
            if(_closing||generation!=_volumeGeneration||_volumeDeviceId!=id)return;
            StartupLog.Write("Volume refresh failed: "+error);_liveEndpointVolume=null;_inputFormatViewKey=null;SyncInputFormat();
        }
    }
    private async Task ApplyWindowsGain()
    {
        var device=_state.Audio.SelectedDevice;
        if(device==null||_state.RecordMode!=RecordingMode.Exclusive||_closing)return;
        try
        {
            var volume=await _state.Audio.QueryEndpointVolumeAsync(device);
            if(_closing)return;
            if(_state.Audio.SelectedDevice?.Id!=device.Id||_state.RecordMode!=RecordingMode.Exclusive){await Message("无法对齐增益","设备或录制模式已改变，请重新选择。");return;}
            var error=InputFormatPresentation.AlignmentUnavailable(volume);
            if(error!=null){await Message("无法对齐增益",error);return;}
            _state.SetGain(volume.Decibels);_gain.Refresh();
        }
        catch(Exception error){if(!_closing)await Message("无法对齐增益","读取 Windows 输入增益失败："+error.Message);}
    }
    private void RefreshValues()
    {
        if(_closing)return;
        if(!_state.IsListening)_inputName.SetName(_state.SelectedInput.FriendlyName);
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
                if(name.Length>1)_note.Inlines.Add(new Run{Text="♯ ",FontSize=42,FontFamily=new FontFamily("Segoe UI Symbol")});
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
        Layout();RenderMonitor();
        if(_started && !keepReadout)_monitorFade.In(120);
        else{_monitorFade.Stop();_monitor.Opacity=1;}
    }

    private void RefreshSession(bool animate)
    {
        if(_closing)return;
        if(_state.IsListening){RefreshListening();return;}
        _workModeBox.IsEnabled=!_audioBusy&&!_state.Session.Active;
        var stage=_state.Session.Stage;
        var active=_state.Session.Active;
        var paused=stage==RecordingStage.Paused;
        var wasActive=_lastStage is RecordingStage.Recording or RecordingStage.Paused;
        var changed=stage!=_lastStage;
        bool refreshMotion=TransportPresentation.NeedsMotionUpdate(_lastStage,stage,reset:!animate);
        animate &= _settings.AnimationsEnabled;
        _lastStage=stage;
        _clock.Text=_state.Session.DisplayTime;
        foreach(var control in _configuration)control.IsEnabled=!active && !_audioBusy;
        SyncCalibration();SyncInputFormat();UpdateQuality();
        var presentation=TransportPresentation.From(_state.Audio.Status.State,_audioBusy,_state.Audio.CalibrationReadyToRecord);
        _stopButton.IsEnabled=presentation.CanStop;_pauseButton.IsEnabled=presentation.CanPause;_discardButton.IsEnabled=presentation.CanDiscard;
        if(!refreshMotion)
        {
            // Calibration makes preparation observable for longer. Keep status and command
            // availability current without cancelling the button/clock transition at each refresh.
            _sessionLabel.Text=presentation.StatusText;
            return;
        }
        var generation=++_transportGeneration;
        _inputName.SetRecording(active,animate);
        if(active)DispatcherQueue.TryEnqueue(LogClockCenters);
        _stopButton.CornerRadius=active?new CornerRadius(0,5,5,0):new CornerRadius(5);
        AutomationProperties.SetName(_stopButton,active?"停止录制":"开始录制");
        ToolTipService.SetToolTip(_stopButton,active&&paused?"停止录制":null);
        AutomationProperties.SetName(_pauseButton,paused?"继续录制":"暂停录制");
        ToolTipService.SetToolTip(_pauseButton,active&&!paused?"暂停录制":null);
        _discardButton.Visibility=paused?Visibility.Visible:Visibility.Collapsed;
        _discardButton.IsEnabled=paused && !_audioBusy;
        _pauseButton.CornerRadius=paused?new CornerRadius(0):new CornerRadius(5,0,0,5);
        _pauseButton.Content=Design.ActionContent(paused?"play":"pause",paused?"继续":null);
        Design.SetBaseFill(_stopButton,active?Design.TransportAction:Design.Record,animate&&changed);

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
        var left=presentation.PauseWidth;var right=presentation.StopWidth;
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
            _sessionLabel.Text=TransportPresentation.From(_state.Audio.Status.State,_audioBusy,_state.Audio.CalibrationReadyToRecord).StatusText;
            _statusFade.To(1,animate?100:0,()=>
            {
                if(_state.Session.Stage==RecordingStage.Paused)_statusFade.Breathe();
            });
        }
        if(animate&&changed&&active&&wasActive)_statusFade.To(0,75,StatusContent);
        else
        {
            _statusFade.Stop();_sessionLabel.Opacity=1;
            _sessionLabel.Text=TransportPresentation.From(_state.Audio.Status.State,_audioBusy,_state.Audio.CalibrationReadyToRecord).StatusText;
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

    private void ApplyTypography(bool alternate)
    {
        _alternateTypography=alternate;
        var level=new FontFamily(alternate?"Bahnschrift":"ms-appx:///Assets/TypeStudy/Barlow-SemiBold.ttf#Barlow SemiBold");
        var pitch=new FontFamily(alternate?"Bahnschrift":"ms-appx:///Assets/TypeStudy/Barlow-SemiBold.ttf#Barlow SemiBold");
        _number.FontFamily=level;_peak.FontFamily=level;
        _note.FontFamily=pitch;_frequency.FontFamily=pitch;_clock.FontFamily=new FontFamily("Bahnschrift");
        foreach(var text in new[]{_number,_peak,_note,_frequency,_clock})
            Typography.SetNumeralAlignment(text,FontNumeralAlignment.Tabular);
    }

    private void UpdateTextScale()
    {
        _renderer.TextScale=1;
        _renderer.ReducedMotion=!_settings.AnimationsEnabled;
    }

    private void Layout()
    {
        if(!_started||_closing)return;
        _main.Width=820;_main.Height=384;
        _monitor.Height=236;_monitor.MinHeight=0;
        _renderer.CentsTop=154;
        _level.Width=225;_pitch.Width=200;
        _level.VerticalAlignment=VerticalAlignment.Center;_pitch.VerticalAlignment=VerticalAlignment.Center;
        _level.Margin=new Thickness(28,0,0,0);_pitch.Margin=_level.Margin;
        _clockSlot.Width=240;
        RenderMonitor();
    }

    private void LogClockCenters()
    {
        if(!_state.Session.Active||_clockSlot.ActualWidth<1)return;
        var status=_sessionLabel.TransformToVisual(_main).TransformPoint(new Point(_sessionLabel.ActualWidth/2,0));
        var clock=_clock.TransformToVisual(_main).TransformPoint(new Point(_clock.ActualWidth/2,0));
        var dpi=_root.XamlRoot?.RasterizationScale??1;
        StartupLog.Write($"Clock alignment: status center={status.X:F3} DIP, clock center={clock.X:F3} DIP, delta={(status.X-clock.X)*dpi:F3} physical px");
    }

    private void TogglePane() => _drawer.Toggle();

    private void OnKeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key==VirtualKey.Escape&&_drawer.IsOpen){_drawer.Close();e.Handled=true;}
        var ctrl=(InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0;var alt=(InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0;
        if(e.Key==VirtualKey.T&&ctrl&&alt){e.Handled=true;ApplyTypography(!_alternateTypography);}
    }
}
