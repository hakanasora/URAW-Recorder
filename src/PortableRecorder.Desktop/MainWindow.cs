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
    private readonly Grid _root=new(),_body=new(),_shell=new(),_main=new(),_header=new(),_monitor=new(),_transport=new();
    private readonly Grid _overlay=new(){Visibility=Visibility.Collapsed};
    private readonly Grid _inspector=new(){Background=Design.Brush(Design.Inspector),Width=264};
    private readonly Border _paneHost=new(){Visibility=Visibility.Collapsed};
    private readonly CanvasControl _canvas=new(){IsHitTestVisible=false};
    private readonly ScrollViewer _monitorScroll=new(){VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    private readonly StackPanel _level=new(),_pitch=new(),_inputPage=new(){Spacing=24},_filePage=new(){Spacing=24};
    private readonly TextBlock _inputName=Design.Text(""),_number=Design.Text("32.0",69),_peak=Design.Text("",14,Design.Muted);
    private readonly TextBlock _note=Design.Text("B2",69),_frequency=Design.Text("123.47",20),_clock=Design.Text("00:00.00",32),_sessionLabel=Design.Text("录制中",12,Design.Zero);
    private readonly TextBlock _fileHint=Design.Text("",12,Design.Label),_pathError=Design.Text("",12,Design.Alert);
    private readonly StackPanel _clip=new(){Orientation=Orientation.Horizontal,Spacing=5,Visibility=Visibility.Collapsed};
    private readonly Border _minus=new(){Width=18,Height=5,Background=Design.Brush(Design.White),VerticalAlignment=VerticalAlignment.Center};
    private readonly Button _settingsButton,_recordButton,_pauseButton,_stopButton;
    private readonly GainControl _gain;
    private readonly Grid _actions=new(){HorizontalAlignment=HorizontalAlignment.Right};
    private readonly StackPanel _activeButtons=new(){Orientation=Orientation.Horizontal,Spacing=3,Visibility=Visibility.Collapsed};
    private readonly StackPanel _clockSlot=new(){HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed};
    private readonly SelectorBar _modeSelector,_paneSelector;
    private readonly ComboBox _deviceBox=new(),_calibrationBox=new(),_formatBox=new(),_qualityBox=new();
    private readonly TextBox _path=new(){PlaceholderText="选择文件夹",MinWidth=0};
    private readonly StackPanel _qualityGroup=new(){Spacing=8};
    private readonly List<Control> _configuration=[];
    private Storyboard? _widthAnimation;
    private bool _firstFrameLogged, _paneOpen,_inlinePane,_userResized,_updatingForm,_started;
    private double _lastSnapshot=-1,_programmaticResizeUntil,_unexpandedWidth=820;
    private float _textScale=1;

    public MainWindow()
    {
        Title="Portable Recorder · UI Preview";ExtendsContentIntoTitleBar=true;
        _root.Background=Design.Brush(Design.Display);_root.RequestedTheme=ElementTheme.Dark;
        _root.RowDefinitions.Add(new(){Height=new GridLength(32)});_root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var title=new Grid{Background=Design.Brush(Design.Display),Height=32};
        title.Children.Add(new TextBlock{Text=Title,FontSize=12,Foreground=Design.Brush(Design.Muted),Margin=new Thickness(16,0,140,0),VerticalAlignment=VerticalAlignment.Center});
        _root.Children.Add(title);SetTitleBar(title);
        AppWindow.TitleBar.BackgroundColor=Design.Display;AppWindow.TitleBar.ButtonBackgroundColor=Design.Display;
        AppWindow.TitleBar.ButtonForegroundColor=Design.Muted;AppWindow.TitleBar.ButtonInactiveBackgroundColor=Design.Display;
        AppWindow.TitleBar.ButtonHoverBackgroundColor=Design.Selected;
        _root.Children.Add(_body);Grid.SetRow(_body,1);_body.Children.Add(_shell);
        _shell.ColumnDefinitions.Add(new(){Width=new GridLength(0)});_shell.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        _shell.Children.Add(_paneHost);_shell.Children.Add(_main);Grid.SetColumn(_main,1);
        _main.RowDefinitions.Add(new(){Height=new GridLength(64)});_main.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});_main.RowDefinitions.Add(new(){Height=new GridLength(84)});
        _main.Children.Add(_header);_main.Children.Add(_monitorScroll);Grid.SetRow(_monitorScroll,1);_main.Children.Add(_transport);Grid.SetRow(_transport,2);
        _settingsButton=Design.Button(MenuGlyph(),"输入与文件设置",Colors.Transparent);_settingsButton.Width=40;_settingsButton.Height=44;
        _settingsButton.HorizontalAlignment=HorizontalAlignment.Left;_settingsButton.VerticalAlignment=VerticalAlignment.Center;_settingsButton.Margin=new Thickness(28,0,0,0);_settingsButton.TabIndex=0;
        _settingsButton.Click+=(_,_)=>TogglePane();_header.Children.Add(_settingsButton);
        _inputName.Margin=new Thickness(76,0,0,0);_inputName.Height=20;_inputName.TextTrimming=TextTrimming.CharacterEllipsis;_inputName.HorizontalAlignment=HorizontalAlignment.Left;_header.Children.Add(_inputName);
        _modeSelector=Design.Selector(["电平","频谱","音高"],76,2,index=>{_state.Mode=(MonitorMode)index;if(_started)RefreshMode();});_modeSelector.TabIndex=1;
        _header.Children.Add(new Border{Background=Design.Brush(Design.Hex("0A0A0A")),CornerRadius=new CornerRadius(4),Padding=new Thickness(4),Child=_modeSelector,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});
        _monitorScroll.Content=_monitor;_monitor.Children.Add(_canvas);BuildReadouts();
        _transport.Background=Design.Brush(Design.Transport);_transport.Children.Add(new Border{Height=1,Background=Design.Brush(Design.Line),VerticalAlignment=VerticalAlignment.Top});
        _gain=new(_state){HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(28,0,0,0)};_transport.Children.Add(_gain);
        _clockSlot.Children.Add(_sessionLabel);_clockSlot.Children.Add(_clock);_sessionLabel.HorizontalAlignment=HorizontalAlignment.Center;_clock.HorizontalAlignment=HorizontalAlignment.Center;_clock.CharacterSpacing=-10;_transport.Children.Add(_clockSlot);
        _actions.Margin=new Thickness(0,0,28,0);_actions.VerticalAlignment=VerticalAlignment.Center;
        _recordButton=Design.Button(Design.ActionContent("record","录制"),"开始录制",Design.Record);_recordButton.Width=100;_recordButton.TabIndex=6;
        _pauseButton=Design.Button(Design.ActionContent("pause"),"暂停录制");_pauseButton.Width=42;_pauseButton.CornerRadius=new(5,0,0,5);_pauseButton.TabIndex=5;
        _stopButton=Design.Button(Design.ActionContent("stop","停止",Design.Alert),"停止录制");_stopButton.Width=100;_stopButton.CornerRadius=new(0,5,5,0);_stopButton.TabIndex=6;
        _recordButton.Click+=(_,_)=>{_state.Session.Start();RefreshSession(true);};
        _pauseButton.Click+=(_,_)=>{if(_state.Session.Stage==RecordingStage.Paused)_state.Session.Resume();else _state.Session.Pause();RefreshSession(true);};
        _stopButton.Click+=(_,_)=>{_state.Session.Stop();RefreshSession(true);};
        _activeButtons.Children.Add(_pauseButton);_activeButtons.Children.Add(_stopButton);_actions.Children.Add(_recordButton);_actions.Children.Add(_activeButtons);_transport.Children.Add(_actions);
        _paneSelector=Design.Selector(["INPUT","FILE"],84,8,index=>{_inputPage.Visibility=index==0?Visibility.Visible:Visibility.Collapsed;_filePage.Visibility=index==1?Visibility.Visible:Visibility.Collapsed;});
        BuildInspector();
        var scrim=new Border{Background=new SolidColorBrush(Windows.UI.Color.FromArgb(61,0,0,0))};scrim.Tapped+=(_,_)=>TogglePane();_overlay.Children.Add(scrim);_body.Children.Add(_overlay);
        _canvas.Draw+=(sender,args)=>
        {
            try { _renderer.Draw(sender,args,_state); if(!_firstFrameLogged){_firstFrameLogged=true;StartupLog.Write($"First monitor frame drawn: {sender.ActualWidth} x {sender.ActualHeight}");} }
            catch(Exception error){StartupLog.Write("Draw failed: "+error);throw;}
        };_state.PropertyChanged+=(_,_)=>RefreshValues();
        _root.Loaded+=(_,_)=>
        {
            try
            {
                StartupLog.Write("Root Loaded");_started=true;
                UpdateTextScale();StartupLog.Write("Text scale ready");
                ResizeClient(820,416);StartupLog.Write("Client resized");
                SetMinimumSize();StartupLog.Write("Minimum size set");
                RefreshMode();StartupLog.Write("Mode ready");
                RefreshSession(false);StartupLog.Write("Session ready");
                RefreshValues();StartupLog.Write("Values ready");
                _timer.Start();StartupLog.Write("Timer started");
            }
            catch(Exception error){StartupLog.Write("Loaded failed: "+error);throw;}
        };
        _main.SizeChanged+=(_,_)=>Layout();_root.KeyDown+=OnKeyDown;_timer.Tick+=(_,_)=>Tick();
        AppWindow.Changed+=(_,args)=>{if(args.DidSizeChange&&_paneOpen&&_inlinePane&&_time.Elapsed.TotalSeconds>_programmaticResizeUntil)_userResized=true;};
        _settings.AnimationsEnabledChanged+=(_,_)=>DispatcherQueue.TryEnqueue(()=>_renderer.ReducedMotion=!_settings.AnimationsEnabled);
        _settings.TextScaleFactorChanged+=(_,_)=>DispatcherQueue.TryEnqueue(()=>{UpdateTextScale();Layout();});
        Closed+=(_,_)=>{_timer.Stop();_canvas.RemoveFromVisualTree();_renderer.Dispose();};Content=_root;
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
        var units=new Grid{Height=16,Margin=new Thickness(0,10,0,0)};units.Children.Add(Design.Text("dBFS · RMS",12,Design.Muted));_clip.HorizontalAlignment=HorizontalAlignment.Right;
        _clip.Children.Add(new Ellipse{Width=6,Height=6,Fill=Design.Brush(Design.Alert),VerticalAlignment=VerticalAlignment.Center});_clip.Children.Add(Design.Text("过载",12,Design.Alert));units.Children.Add(_clip);_level.Children.Add(units);
        var peakRow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Height=28,Margin=new Thickness(0,16,0,0)};
        var reset=Design.Button(Design.Icon("\uE777",14),"清除历史输出峰值",Colors.Transparent);reset.Width=24;reset.Height=28;reset.Padding=new Thickness(0);reset.TabIndex=3;
        reset.Click+=(_,_)=>{_state.ResetPeak();RefreshValues();};peakRow.Children.Add(_peak);peakRow.Children.Add(reset);_level.Children.Add(peakRow);_monitor.Children.Add(_level);
        _pitch.Width=200;_pitch.Margin=new Thickness(28,0,0,0);_pitch.HorizontalAlignment=HorizontalAlignment.Left;_pitch.VerticalAlignment=VerticalAlignment.Center;_pitch.Visibility=Visibility.Collapsed;
        _note.Height=72;_note.CharacterSpacing=-20;_pitch.Children.Add(_note);var hz=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5,Margin=new Thickness(0,10,0,0),Height=26};hz.Children.Add(_frequency);hz.Children.Add(Design.Text("Hz",12,Design.Muted));_pitch.Children.Add(hz);_pitch.Children.Add(new Border{Height=64});_monitor.Children.Add(_pitch);
        AutomationProperties.SetName(_number,"当前输出 RMS，dBFS");AutomationProperties.SetLiveSetting(_sessionLabel,AutomationLiveSetting.Polite);AutomationProperties.SetLiveSetting(_clip,AutomationLiveSetting.Polite);
    }
    private void BuildInspector()
    {
        _inspector.RowDefinitions.Add(new(){Height=new GridLength(48)});_inspector.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var top=new Grid();_paneSelector.HorizontalAlignment=HorizontalAlignment.Left;_paneSelector.Margin=new Thickness(20,0,0,0);top.Children.Add(_paneSelector);
        var close=Design.Button(Design.Icon("\uE711",14,Design.Hex("747476")),"关闭设置",Colors.Transparent);close.Width=32;close.Height=32;close.HorizontalAlignment=HorizontalAlignment.Right;close.Margin=new Thickness(0,0,24,0);close.Click+=(_,_)=>TogglePane();top.Children.Add(close);_inspector.Children.Add(top);
        var pages=new Grid{Padding=new Thickness(24,16,24,24)};pages.Children.Add(_inputPage);pages.Children.Add(_filePage);_filePage.Visibility=Visibility.Collapsed;
        var scroll=new ScrollViewer{Content=pages,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};_inspector.Children.Add(scroll);Grid.SetRow(scroll,1);
        StyleField(_deviceBox);_deviceBox.ItemsSource=_state.Devices;_deviceBox.DisplayMemberPath="FriendlyName";_deviceBox.SelectedItem=_state.SelectedInput;_deviceBox.SelectionChanged+=(_,_)=>{if(_deviceBox.SelectedItem is InputDevice d){_state.SelectedInput=d;RefreshValues();}};_inputPage.Children.Add(Group("输入设备","\uE720",_deviceBox));
        StyleField(_calibrationBox);_calibrationBox.ItemsSource=_state.Calibrations;_calibrationBox.DisplayMemberPath="Name";_calibrationBox.SelectedItem=_state.SelectedCalibration;_calibrationBox.SelectionChanged+=(_,_)=>{if(_calibrationBox.SelectedItem is CalibrationFile f)_state.SelectedCalibration=f;};
        var calibration=Group("校准文件","\uE8A5",_calibrationBox);var add=LinkButton("添加校准文件");add.Click+=async(_,_)=>await AddCalibration();var manage=LinkButton("管理已添加的校准");manage.Click+=async(_,_)=>await ManageCalibrations();calibration.Children.Add(add);calibration.Children.Add(manage);_inputPage.Children.Add(calibration);
        StyleField(_path);_path.TextChanged+=(_,_)=>{_state.OutputPath=_path.Text;ToolTipService.SetToolTip(_path,_path.Text);};
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
        _inputName.Text=_state.SelectedInput.FriendlyName;ToolTipService.SetToolTip(_inputName,_inputName.Text);
        var db=_state.Snapshot.RmsDbfs;_number.Text=$"{(db>0?"+":"")}{Math.Abs(db):0.0}";_minus.Visibility=db<0?Visibility.Visible:Visibility.Collapsed;
        _peak.Text=_state.HeldPeak is double p?$"PEAK {(p>=0?"+":"−")}{Math.Abs(p):0.0} dBFS":"PEAK — dBFS";_peak.Foreground=Design.Brush(_state.HeldPeak>=0?Design.Alert:Design.Muted);
        _clip.Visibility=_state.CurrentClipping?Visibility.Visible:Visibility.Collapsed;ToolTipService.SetToolTip(_clip,_state.Snapshot.SourcePeakDbfs>=0?"当前输入已过载":"增益后的当前输出已过载");_gain.Refresh();
        _note.Inlines.Clear();
        if(_state.Snapshot.MidiNote is double midi){string[] names=["C","C♯","D","D♯","E","F","F♯","G","G♯","A","A♯","B"];var n=(int)Math.Round(midi);_note.Inlines.Add(new Run{Text=names[((n%12)+12)%12]});_note.Inlines.Add(new Run{Text=$"{n/12-1}",FontSize=36});_frequency.Text=$"{_state.Snapshot.FrequencyHz:0.00}";}
        else{_note.Text="—";_frequency.Text="—";}
    }
    private void RefreshMode(){_level.Visibility=_state.Mode==MonitorMode.Level?Visibility.Visible:Visibility.Collapsed;_pitch.Visibility=_state.Mode==MonitorMode.Pitch?Visibility.Visible:Visibility.Collapsed;Layout();_canvas.Invalidate();}
    private void RefreshSession(bool animate)
    {
        var active=_state.Session.Active;var paused=_state.Session.Stage==RecordingStage.Paused;
        _recordButton.Visibility=active?Visibility.Collapsed:Visibility.Visible;_activeButtons.Visibility=active?Visibility.Visible:Visibility.Collapsed;_clockSlot.Visibility=active?Visibility.Visible:Visibility.Collapsed;_sessionLabel.Text=paused?"已暂停":"录制中";_clock.Text=_state.Session.DisplayTime;
        _pauseButton.Content=Design.ActionContent(paused?"play":"pause",paused?"继续":null);_stopButton.Content=Design.ActionContent("stop",paused?null:"停止",Design.Alert);AutomationProperties.SetName(_pauseButton,paused?"继续录制":"暂停录制");ToolTipService.SetToolTip(_pauseButton,paused?"继续录制":"暂停录制");
        foreach(var c in _configuration)c.IsEnabled=!active;UpdateQuality();
        if(animate&&!_renderer.ReducedMotion&&active)AnimateWidths((paused?100:42)*_textScale,(paused?42:100)*_textScale);else{_widthAnimation?.Stop();_pauseButton.Width=(paused?100:42)*_textScale;_stopButton.Width=(paused?42:100)*_textScale;}Layout();
    }
    private void AnimateWidths(double left,double right)
    {
        var startLeft=_pauseButton.ActualWidth>0?_pauseButton.ActualWidth:_pauseButton.Width;var startRight=_stopButton.ActualWidth>0?_stopButton.ActualWidth:_stopButton.Width;
        _widthAnimation?.Stop();_pauseButton.Width=left;_stopButton.Width=right;var sb=new Storyboard();
        foreach(var (target,start,end) in new[]{(_pauseButton,startLeft,left),(_stopButton,startRight,right)})
        {
            var animation=new DoubleAnimationUsingKeyFrames{EnableDependentAnimation=true,FillBehavior=FillBehavior.Stop};animation.KeyFrames.Add(new DiscreteDoubleKeyFrame{KeyTime=TimeSpan.Zero,Value=start});animation.KeyFrames.Add(new SplineDoubleKeyFrame{KeyTime=TimeSpan.FromMilliseconds(200),Value=end,KeySpline=new KeySpline{ControlPoint1=new Point(.22,1),ControlPoint2=new Point(.36,1)}});Storyboard.SetTarget(animation,target);Storyboard.SetTargetProperty(animation,"Width");sb.Children.Add(animation);
        }
        _widthAnimation=sb;sb.Begin();
    }
    private void UpdateTextScale(){_textScale=(float)_settings.TextScaleFactor;_renderer.TextScale=_textScale;_renderer.ReducedMotion=!_settings.AnimationsEnabled;}
    private void Layout()
    {
        if(!_started||_main.ActualWidth<1)return;var w=_main.ActualWidth;var s=_textScale;
        _main.RowDefinitions[0].Height=new GridLength(Math.Max(64,Math.Ceiling(36*s)+28));
        for(var i=0;i<_modeSelector.Items.Count;i++){_modeSelector.Items[i].Width=76*s;_modeSelector.Items[i].Height=28*s;}
        var nameWidth=(w-240*s)/2-16-76;_inputName.MaxWidth=Math.Max(0,nameWidth);_inputName.Visibility=nameWidth>=48?Visibility.Visible:Visibility.Collapsed;
        var clockWidth=Math.Ceiling(240*s);var groupWidth=Math.Ceiling(142*s)+3;var wrap=28+Math.Ceiling(88*s)+16>(w-clockWidth)/2||(w+clockWidth)/2+16>w-28-groupWidth;
        var first=Math.Max(44,Math.Ceiling(60*s));var second=Math.Ceiling(42*s);_main.RowDefinitions[2].Height=new GridLength(wrap?24+first+8+second:Math.Max(84,Math.Ceiling(60*s)+24));
        _gain.Width=Math.Max(112,Math.Ceiling(88*s));_clockSlot.Width=clockWidth;_gain.VerticalAlignment=wrap?VerticalAlignment.Top:VerticalAlignment.Center;_gain.Margin=new Thickness(28,wrap?12:0,0,0);_clockSlot.VerticalAlignment=wrap?VerticalAlignment.Top:VerticalAlignment.Center;_clockSlot.Margin=new Thickness(0,wrap?12:0,0,0);_actions.VerticalAlignment=wrap?VerticalAlignment.Bottom:VerticalAlignment.Center;_actions.Margin=new Thickness(0,0,28,wrap?12:0);
        var stacked=s>1&&w<84+200*s+536;_renderer.Stacked=stacked&&_state.Mode!=MonitorMode.Spectrum;var block=_state.Mode==MonitorMode.Pitch?Math.Ceiling(172*s):Math.Ceiling(114*s+Math.Max(24*s,28));var vuBlock=Math.Ceiling(172+16*s);_renderer.StackedVuTop=(float)(24+block+24);
        var minimum=_renderer.Stacked?24+block+24+vuBlock+24:Math.Max(236,block+48);_monitor.MinHeight=minimum;_monitor.Height=Math.Max(minimum,_monitorScroll.ActualHeight);
        ((FrameworkElement)_level.Children[0]).Height=72*s;
        ((FrameworkElement)_level.Children[1]).Height=16*s;((FrameworkElement)_level.Children[1]).Margin=new Thickness(0,10*s,0,0);
        ((FrameworkElement)_level.Children[2]).Height=Math.Max(28,24*s);((FrameworkElement)_level.Children[2]).Margin=new Thickness(0,16*s,0,0);
        _minus.Width=18*s;_minus.Height=5*s;_note.Height=72*s;
        ((FrameworkElement)_pitch.Children[1]).Height=26*s;((FrameworkElement)_pitch.Children[1]).Margin=new Thickness(0,10*s,0,0);((FrameworkElement)_pitch.Children[2]).Height=64*s;
        _renderer.CentsTop=(float)((stacked?24:(_monitor.Height-172*s)/2)+122*s);
        _recordButton.Width=100*s;_recordButton.Height=42*s;_pauseButton.Height=42*s;_stopButton.Height=42*s;
        _level.Width=200*s;_pitch.Width=200*s;_level.VerticalAlignment=stacked?VerticalAlignment.Top:VerticalAlignment.Center;_pitch.VerticalAlignment=_level.VerticalAlignment;_level.Margin=new Thickness(28,stacked?24:0,0,0);_pitch.Margin=_level.Margin;_canvas.Invalidate();
    }
    private void SetMinimumSize()
    {
        if(AppWindow.Presenter is not OverlappedPresenter presenter)return;
        var dpi=_root.XamlRoot?.RasterizationScale??1;
        presenter.PreferredMinimumWidth=(int)Math.Ceiling(820*dpi)+AppWindow.Size.Width-AppWindow.ClientSize.Width;
        presenter.PreferredMinimumHeight=(int)Math.Ceiling(416*dpi)+AppWindow.Size.Height-AppWindow.ClientSize.Height;
    }
    private void ResizeClient(double width,double height){_programmaticResizeUntil=_time.Elapsed.TotalSeconds+.5;var dpi=_root.XamlRoot?.RasterizationScale??1;AppWindow.ResizeClient(new SizeInt32((int)Math.Round(width*dpi),(int)Math.Round(height*dpi)));}
    private void TogglePane()
    {
        if(_paneOpen)
        {
            _paneHost.Child=null;_overlay.Children.Remove(_inspector);_overlay.Visibility=Visibility.Collapsed;_paneHost.Visibility=Visibility.Collapsed;_shell.ColumnDefinitions[0].Width=new GridLength(0);if(_inlinePane&&!_userResized)ResizeClient(_unexpandedWidth,Math.Max(416,_root.ActualHeight));_paneOpen=false;_settingsButton.Focus(FocusState.Programmatic);return;
        }
        _paneOpen=true;_userResized=false;_unexpandedWidth=_root.ActualWidth;var dpi=_root.XamlRoot?.RasterizationScale??1;var area=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Nearest).WorkArea;
        _inlinePane=_textScale<=1&&AppWindow.Presenter is OverlappedPresenter{State:OverlappedPresenterState.Restored}&&area.Width/dpi>=_root.ActualWidth+264;
        if(_inlinePane){_paneHost.Child=_inspector;_paneHost.Visibility=Visibility.Visible;_shell.ColumnDefinitions[0].Width=new GridLength(264);ResizeClient(_root.ActualWidth+264,Math.Max(416,_root.ActualHeight));}
        else{_inspector.HorizontalAlignment=HorizontalAlignment.Left;_overlay.Children.Add(_inspector);_overlay.Visibility=Visibility.Visible;_inspector.TabFocusNavigation=KeyboardNavigationMode.Cycle;}
        _paneSelector.Focus(FocusState.Programmatic);
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
