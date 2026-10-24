using System.Diagnostics;
using Microsoft.Graphics.Canvas.UI.Xaml;
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
    private readonly Stopwatch _time=Stopwatch.StartNew();
    private readonly UISettings _settings=new();
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(33)};
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
    private readonly TextBlock _number=Design.Text("32.0",69),_peak=Design.Text("",14,Design.Muted);
    private readonly TextBlock _note=Design.Text("B2",69),_frequency=Design.Text("123.47",20),_clock=Design.Text("00:00.00",32),_sessionLabel=Design.Text("录制中",12,Design.Zero);
    private readonly TextBlock _fileHint=Design.Text("",12,Design.Label),_pathError=Design.Text("",12,Design.Alert);
    private readonly Border _minus=new(){Width=18,Height=5,Background=Design.Brush(Design.White),VerticalAlignment=VerticalAlignment.Center};
    private readonly Button _settingsButton,_pauseButton,_stopButton;
    private readonly GainControl _gain;
    private readonly Grid _actions=new(){HorizontalAlignment=HorizontalAlignment.Right};
    private readonly StackPanel _activeButtons=new(){Orientation=Orientation.Horizontal,Spacing=3,Visibility=Visibility.Collapsed};
    private readonly Grid _clockSlot=new(){HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed};
    private readonly SegmentedSelector _modeSelector,_paneSelector;
    private readonly ComboBox _deviceBox=new(),_calibrationBox=new(),_formatBox=new(),_qualityBox=new();
    private readonly TextBox _path=new(){PlaceholderText="选择文件夹",MinWidth=0};
    private readonly StackPanel _qualityGroup=new(){Spacing=8};
    private readonly List<Control> _configuration=[];
    private Storyboard? _widthAnimation;
    private bool _firstFrameLogged,_paneOpen,_updatingForm,_started;
    private double _lastSnapshot=-1;
    private int? _displayedNote;
    private readonly SolidColorBrush _normalPeakInk=Design.Brush(Design.Muted),_overloadPeakInk=Design.Brush(Design.Alert);
    private static readonly bool OverlayDrawer=false;

    public MainWindow()
    {
        Title="Portable Recorder · UI Preview";ExtendsContentIntoTitleBar=true;
        _frame=new FixedWindowFrame(this,()=>_root.XamlRoot?.RasterizationScale??1);
        _monitorFade=new NativeFade(_monitor,()=>_settings.AnimationsEnabled);
        _clockFade=new NativeFade(_clockSlot,()=>_settings.AnimationsEnabled);
        _statusFade=new NativeFade(_sessionLabel,()=>_settings.AnimationsEnabled);
        _primaryFade=new NativeFade(_primaryContent,()=>_settings.AnimationsEnabled);
        _root.Background=Design.Brush(Design.Display);_root.RequestedTheme=ElementTheme.Dark;
        _root.RowDefinitions.Add(new(){Height=new GridLength(32)});_root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var title=new Grid{Background=Design.Brush(Design.Display),Height=32};
        title.Children.Add(new TextBlock{Text=Title,FontSize=12,Foreground=Design.Brush(Design.Muted),Margin=new Thickness(16,0,140,0),VerticalAlignment=VerticalAlignment.Center});
        _root.Children.Add(title);SetTitleBar(title);
        AppWindow.TitleBar.BackgroundColor=Design.Display;AppWindow.TitleBar.ButtonBackgroundColor=Design.Display;
        AppWindow.TitleBar.ButtonForegroundColor=Design.Muted;AppWindow.TitleBar.ButtonInactiveBackgroundColor=Design.Display;
        AppWindow.TitleBar.ButtonHoverBackgroundColor=Design.Selected;
        _root.Children.Add(_body);Grid.SetRow(_body,1);_body.Children.Add(_splitView);
        _main.Width=FixedWindowFrame.MainWidth;_main.Height=384;
        _main.HorizontalAlignment=HorizontalAlignment.Right;
        _main.Clip=new RectangleGeometry{Rect=new Rect(0,0,820,384)};
        _main.RowDefinitions.Add(new(){Height=new GridLength(64)});
        _main.RowDefinitions.Add(new(){Height=new GridLength(236)});
        _main.RowDefinitions.Add(new(){Height=new GridLength(84)});
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
        _header.Children.Add(new Border{Background=Design.Brush(Design.Hex("0A0A0A")),CornerRadius=new CornerRadius(4),Padding=new Thickness(4),Child=_modeSelector,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});
        _monitor.Children.Add(_canvas);BuildReadouts();
        _transport.Background=Design.Brush(Design.Transport);_transport.Children.Add(new Border{Height=1,Background=Design.Brush(Design.Line),VerticalAlignment=VerticalAlignment.Top});
        _gain=new(_state){Width=96,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(172,0,0,0)};_transport.Children.Add(_gain);
        _inputName.HorizontalAlignment=HorizontalAlignment.Left;
        _inputName.VerticalAlignment=VerticalAlignment.Center;
        _inputName.Margin=new Thickness(28,0,0,0);
        _transport.Children.Add(_inputName);
        _clockSlot.Width=240;_clockSlot.Height=60;
        _clockSlot.RowDefinitions.Add(new(){Height=new GridLength(20)});
        _clockSlot.RowDefinitions.Add(new(){Height=new GridLength(40)});
        _sessionLabel.HorizontalAlignment=HorizontalAlignment.Stretch;
        _clock.HorizontalAlignment=HorizontalAlignment.Stretch;
        _sessionLabel.TextAlignment=TextAlignment.Center;_clock.TextAlignment=TextAlignment.Center;
        _clockSlot.Children.Add(_sessionLabel);_clockSlot.Children.Add(_clock);Grid.SetRow(_clock,1);
        _clock.CharacterSpacing=-10;_transport.Children.Add(_clockSlot);
        _clockSlot.SizeChanged+=(_,_)=>LogClockCenters();
        _actions.Margin=new Thickness(0,0,28,0);_actions.VerticalAlignment=VerticalAlignment.Center;
        _pauseButton=Design.Button(Design.ActionContent("pause"),"暂停录制");
        _pauseButton.Width=0;_pauseButton.Opacity=0;_pauseButton.Visibility=Visibility.Collapsed;
        _pauseButton.CornerRadius=new(5,0,0,5);_pauseButton.TabIndex=5;
        _pauseFade=new NativeFade(_pauseButton,()=>_settings.AnimationsEnabled);
        _primaryContent.Children.Add(Design.ActionContent("record","录制"));
        _stopButton=Design.Button(_primaryContent,"开始录制",Design.Record);
        _stopButton.Width=100;_stopButton.TabIndex=6;
        _pauseButton.Click+=(_,_)=>
        {
            if(_state.Session.Stage==RecordingStage.Paused)_state.Session.Resume();
            else _state.Session.Pause();
            RefreshSession(true);
        };
        _stopButton.Click+=(_,_)=>
        {
            if(_state.Session.Active)_state.Session.Stop();else _state.Session.Start();
            RefreshSession(true);
        };
        _activeButtons.Visibility=Visibility.Visible;_activeButtons.Spacing=0;
        _activeButtons.Children.Add(_pauseButton);_activeButtons.Children.Add(_stopButton);
        _actions.Children.Add(_activeButtons);_transport.Children.Add(_actions);
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
            try { _renderer.Draw(sender,args,_state); if(!_firstFrameLogged){_firstFrameLogged=true;StartupLog.Write($"First monitor frame drawn: {sender.ActualWidth} x {sender.ActualHeight}");} }
            catch(Exception error){StartupLog.Write("Draw failed: "+error);throw;}
        };_state.PropertyChanged+=(_,_)=>RefreshValues();
        _root.Loaded+=(_,_)=>
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
            }
            catch(Exception error){StartupLog.Write("Loaded failed: "+error);throw;}
        };
        _main.SizeChanged+=(_,_)=>Layout();_root.KeyDown+=OnKeyDown;_timer.Tick+=(_,_)=>Tick();
        _settings.AnimationsEnabledChanged+=(_,_)=>DispatcherQueue.TryEnqueue(()=>{UpdateTextScale();RefreshSession(false);});
        _settings.TextScaleFactorChanged+=(_,_)=>DispatcherQueue.TryEnqueue(()=>{UpdateTextScale();Layout();});
        Closed+=(_,_)=>{_timer.Stop();_widthAnimation?.Stop();_monitorFade.Stop();_clockFade.Stop();_statusFade.Stop();_primaryFade.Stop();_pauseFade.Stop();_canvas.RemoveFromVisualTree();_renderer.Dispose();};Content=_root;
    }
    private static Grid MenuGlyph()
    {
        var grid=new Grid{Width=18,Height=12};var canvas=new Canvas{Width=18,Height=12};
        foreach(var y in new[]{0,5.25,10.5}){var line=new Border{Width=18,Height=1.5,Background=Design.Brush(Design.Muted)};Canvas.SetTop(line,y);canvas.Children.Add(line);}grid.Children.Add(canvas);return grid;
    }
    private void BuildReadouts()
    {
        _level.Width=200;_level.Margin=new Thickness(28,0,0,0);_level.HorizontalAlignment=HorizontalAlignment.Left;_level.VerticalAlignment=VerticalAlignment.Center;
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4,Height=72};_number.FontWeight=FontWeights.Bold;_number.CharacterSpacing=-20;row.Children.Add(_minus);row.Children.Add(_number);_level.Children.Add(row);
        var units=Design.Text("dBFS · RMS",12,Design.Muted);units.Height=16;units.Margin=new Thickness(0,10,0,0);_level.Children.Add(units);
        var peakRow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Height=28,Margin=new Thickness(0,16,0,0)};
        var reset=Design.Button(Design.Icon("\uE777",14),"清除历史输出峰值",Colors.Transparent);reset.Width=24;reset.Height=28;reset.Padding=new Thickness(0);reset.TabIndex=3;
        reset.Click+=(_,_)=>{_state.ResetPeak();RefreshValues();};peakRow.Children.Add(_peak);peakRow.Children.Add(reset);_level.Children.Add(peakRow);_monitor.Children.Add(_level);
        _pitch.Width=200;_pitch.Margin=new Thickness(28,0,0,0);_pitch.HorizontalAlignment=HorizontalAlignment.Left;_pitch.VerticalAlignment=VerticalAlignment.Center;_pitch.Visibility=Visibility.Collapsed;
        _note.Height=72;_note.CharacterSpacing=-20;_pitch.Children.Add(_note);var hz=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5,Margin=new Thickness(0,10,0,0),Height=26};hz.Children.Add(_frequency);hz.Children.Add(Design.Text("Hz",12,Design.Muted));_pitch.Children.Add(hz);_pitch.Children.Add(new Border{Height=64});_monitor.Children.Add(_pitch);
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
        StyleField(_deviceBox);_deviceBox.ItemsSource=_state.Devices;_deviceBox.DisplayMemberPath="FriendlyName";_deviceBox.SelectedItem=_state.SelectedInput;_deviceBox.SelectionChanged+=(_,_)=>{if(_deviceBox.SelectedItem is InputDevice d){_state.SelectedInput=d;RefreshValues();}};_inputPage.Children.Add(Group("输入设备","\uE720",_deviceBox));
        StyleField(_calibrationBox);_calibrationBox.ItemsSource=_state.Calibrations;_calibrationBox.DisplayMemberPath="Name";_calibrationBox.SelectedItem=_state.SelectedCalibration;_calibrationBox.SelectionChanged+=(_,_)=>{if(_calibrationBox.SelectedItem is CalibrationFile f)_state.SelectedCalibration=f;};
        var calibration=Group("校准文件","\uE8A5",_calibrationBox);var add=LinkButton("添加校准文件");add.Click+=async(_,_)=>await AddCalibration();var manage=LinkButton("管理已添加的校准");manage.Click+=async(_,_)=>await ManageCalibrations();calibration.Children.Add(add);calibration.Children.Add(manage);_inputPage.Children.Add(calibration);
        StyleField(_path);_path.VerticalContentAlignment=VerticalAlignment.Center;_path.Padding=new Thickness(12,8,12,8);_path.TextChanged+=(_,_)=>{_state.OutputPath=_path.Text;ToolTipService.SetToolTip(_path,_path.Text);};
        var pathRow=new Grid{ColumnSpacing=8};pathRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});pathRow.ColumnDefinitions.Add(new(){Width=new GridLength(36)});pathRow.Children.Add(_path);
        var browse=Design.Button(Design.Icon("\uE8B7",16,Design.White),"浏览保存文件夹",Design.Field);browse.Width=36;browse.Height=36;browse.CornerRadius=new(3);browse.Padding=new Thickness(0);Grid.SetColumn(browse,1);pathRow.Children.Add(browse);_configuration.Add(browse);browse.Click+=async(_,_)=>await BrowseFolder();
        var pathGroup=Group("保存路径","\uE8B7",pathRow);_pathError.TextWrapping=TextWrapping.Wrap;_pathError.Visibility=Visibility.Collapsed;pathGroup.Children.Add(_pathError);_filePage.Children.Add(pathGroup);
        StyleField(_formatBox);_formatBox.ItemsSource=new[]{"WAV","FLAC","MP3"};_formatBox.SelectedItem="WAV";_formatBox.SelectionChanged+=(_,_)=>UpdateQuality();_filePage.Children.Add(Group("录制格式","\uE8A5",_formatBox));
        StyleField(_qualityBox);_qualityGroup.Children.Add(Label("压缩品质","\uE713"));_qualityGroup.Children.Add(_qualityBox);_filePage.Children.Add(_qualityGroup);
        _qualityBox.SelectionChanged+=(_,_)=>{if(_updatingForm||_qualityBox.SelectedItem is not string v)return;if(_state.Format=="MP3")_state.Mp3Quality=v;else _state.FlacQuality=v;};
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
        if(_formatBox.SelectedItem is not string f)return;_updatingForm=true;_state.Format=f;_qualityGroup.Visibility=f=="WAV"?Visibility.Collapsed:Visibility.Visible;
        _qualityBox.ItemsSource=f=="MP3"?new[]{"320 kbps","256 kbps","192 kbps","128 kbps"}:new[]{"快速 · 等级 0","标准 · 等级 5","紧凑 · 等级 8"};_qualityBox.SelectedItem=f=="MP3"?_state.Mp3Quality:_state.FlacQuality;
        _fileHint.Text=_state.Session.Active?"停止后可更改":f=="FLAC"?"无损压缩，等级影响体积与耗时。":"";_updatingForm=false;
    }
    private async Task BrowseFolder()
    {
        if(_state.Session.Active)return;
        try{var result=await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();if(result!=null)_path.Text=result.Path;_pathError.Visibility=Visibility.Collapsed;}
        catch{_pathError.Text="暂时无法打开文件夹选择器，请手动输入路径。";_pathError.Visibility=Visibility.Visible;}
    }
    private async Task AddCalibration()
    {
        if(_state.Session.Active)return;
        try
        {
            var picker=new FileOpenPicker(AppWindow.Id);picker.FileTypeFilter.Add(".txt");var files=await picker.PickMultipleFilesAsync();if(_state.Session.Active || files is null)return;
            foreach(var file in files){var existing=_state.Calibrations.FirstOrDefault(c=>string.Equals(c.Path,file.Path,StringComparison.OrdinalIgnoreCase));var item=existing??new CalibrationFile(file.Path,System.IO.Path.GetFileName(file.Path));if(existing==null)_state.Calibrations.Add(item);_calibrationBox.SelectedItem=item;}
        }
        catch{await Message("校准文件","暂时无法打开文件选择器，请稍后再试。");}
    }
    private async Task ManageCalibrations()
    {
        if(_state.Session.Active)return;
        var list=new ListView{ItemsSource=_state.Calibrations,DisplayMemberPath="Name",Height=160,SelectedItem=_state.SelectedCalibration};var panel=new StackPanel{Spacing=12};panel.Children.Add(list);var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        var remove=Design.Button(Design.Text("移除",13),"从列表移除");var add=Design.Button(Design.Text("添加",13),"添加校准文件");actions.Children.Add(remove);actions.Children.Add(add);panel.Children.Add(actions);
        remove.Click+=(_,_)=>{if(_state.Session.Active||list.SelectedItem is not CalibrationFile item||item.Path=="")return;if(item==_state.SelectedCalibration)_calibrationBox.SelectedItem=_state.Calibrations[0];_state.Calibrations.Remove(item);};add.Click+=async(_,_)=>await AddCalibration();
        var dialog=new ContentDialog{XamlRoot=_root.XamlRoot,Title="已添加的校准",Content=panel,PrimaryButtonText="使用所选",CloseButtonText="关闭",RequestedTheme=ElementTheme.Dark};
        if(await dialog.ShowAsync()==ContentDialogResult.Primary&&!_state.Session.Active&&list.SelectedItem is CalibrationFile chosen)_calibrationBox.SelectedItem=chosen;
    }
    private async Task Message(string title,string text){await new ContentDialog{XamlRoot=_root.XamlRoot,Title=title,Content=text,CloseButtonText="关闭",RequestedTheme=ElementTheme.Dark}.ShowAsync();}
    private void Tick()
    {
        var t=_time.Elapsed.TotalSeconds;if(t-_lastSnapshot>=.05){_state.Refresh(t);_lastSnapshot=t;RefreshValues();}if(_state.Session.Active)_clock.Text=_state.Session.DisplayTime;
        if(AppWindow.Presenter is not OverlappedPresenter{State:OverlappedPresenterState.Minimized})_canvas.Invalidate();
    }
    private void RefreshValues()
    {
        _inputName.SetName(_state.SelectedInput.FriendlyName);
        var db=_state.Snapshot.RmsDbfs;_number.Text=$"{(db>0?"+":"")}{Math.Abs(db):0.0}";_minus.Visibility=db<0?Visibility.Visible:Visibility.Collapsed;
        _peak.Text=_state.HeldPeak is double p?$"PEAK {(p>=0?"+":"−")}{Math.Abs(p):0.0} dBFS":"PEAK — dBFS";_peak.Foreground=_state.HeldPeak>=0?_overloadPeakInk:_normalPeakInk;
        _gain.Refresh();
        if(_state.Snapshot.MidiNote is double midi)
        {
            var n=(int)Math.Round(midi);
            if(_displayedNote!=n)
            {
                string[] names=["C","C♯","D","D♯","E","F","F♯","G","G♯","A","A♯","B"];
                _note.Inlines.Clear();
                _note.Inlines.Add(new Run{Text=names[((n%12)+12)%12]});
                _note.Inlines.Add(new Run{Text=$"{n/12-1}",FontSize=36});_displayedNote=n;
            }
            _frequency.Text=$"{_state.Snapshot.FrequencyHz:0.00}";
        }
        else{_displayedNote=null;_note.Text="—";_frequency.Text="—";}
    }
    private void RefreshMode()
    {
        _level.Visibility=_state.Mode==MonitorMode.Level?Visibility.Visible:Visibility.Collapsed;
        _pitch.Visibility=_state.Mode==MonitorMode.Pitch?Visibility.Visible:Visibility.Collapsed;
        Layout();_canvas.Invalidate();
        if(_started)_monitorFade.In(120);
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
        if(active)DispatcherQueue.TryEnqueue(LogClockCenters);
        foreach(var control in _configuration)control.IsEnabled=!active;
        UpdateQuality();
        _stopButton.CornerRadius=active?new CornerRadius(0,5,5,0):new CornerRadius(5);
        AutomationProperties.SetName(_stopButton,active?"停止录制":"开始录制");
        ToolTipService.SetToolTip(_stopButton,active?"停止录制":"开始录制");
        AutomationProperties.SetName(_pauseButton,paused?"继续录制":"暂停录制");
        ToolTipService.SetToolTip(_pauseButton,paused?"继续录制":"暂停录制");
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
            _sessionLabel.Text=paused?"暂停中":"录制中";
            _statusFade.To(1,animate?100:0,()=>
            {
                if(_state.Session.Stage==RecordingStage.Paused)_statusFade.Breathe();
            });
        }
        if(animate&&changed&&active&&wasActive)_statusFade.To(0,75,StatusContent);
        else
        {
            _statusFade.Stop();_sessionLabel.Opacity=1;
            _sessionLabel.Text=paused?"暂停中":"录制中";
            if(paused)_statusFade.Breathe();
        }
    }

    private void AnimateWidths(double left,double right,double spacing,int duration,Action completed)
    {
        var fromLeft=_pauseButton.Width;
        var fromRight=_stopButton.Width;
        var fromSpacing=_activeButtons.Spacing;
        _widthAnimation?.Stop();
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

    private async void OnKeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key==VirtualKey.Escape&&_paneOpen){TogglePane();e.Handled=true;}
        if(e.Key==VirtualKey.F12){e.Handled=true;await SceneDialog();}
        var ctrl=(InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0;var alt=(InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0;
        if(e.Key==VirtualKey.T&&ctrl&&alt){e.Handled=true;var family=_number.FontFamily.Source=="Bahnschrift"?"Segoe UI Variable":"Bahnschrift";foreach(var text in new[]{_number,_note,_frequency,_clock})text.FontFamily=new FontFamily(family);}
    }
    private async Task SceneDialog()
    {
        var list=new ComboBox{ItemsSource=new[]{"正常信号","输出过载（增益 +24 dB）","输入过载（增益 −12 dB）","无有效音高"},SelectedIndex=(int)_state.Scenario,MinWidth=260};var dialog=new ContentDialog{XamlRoot=_root.XamlRoot,Title="模拟场景",Content=list,PrimaryButtonText="应用",CloseButtonText="取消",RequestedTheme=ElementTheme.Dark};
        if(await dialog.ShowAsync()==ContentDialogResult.Primary){_state.Scenario=(MockScenario)list.SelectedIndex;_state.SetGain(_state.Scenario==MockScenario.OutputClip?24:_state.Scenario==MockScenario.SourceClipOnly?-12:0);_state.ResetPeak();_state.Refresh(_time.Elapsed.TotalSeconds);RefreshValues();}
    }
}
