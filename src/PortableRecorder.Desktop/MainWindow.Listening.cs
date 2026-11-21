using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PortableRecorder.Audio;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace PortableRecorder.Desktop;

public sealed partial class MainWindow
{
    private sealed record ApplicationChoice(ListeningSource Source)
    {
        public string Name=>$"{Source.Name} · PID {Source.ProcessId}";
    }
    private readonly ComboBox _workModeBox=new(),_listenScope=new(),_listenTap=new(),_listenEndpoint=new(),_listenApplication=new();
    private readonly ScrollViewer _listenHost=new();
    private readonly TextBlock _listenHeading=Design.Text("侦听设置",13,Design.White,true);
    private readonly TextBlock _listenFormat=Design.Text("",11,Design.Muted);
    private readonly StackPanel _listenPage=new(){Spacing=16,Margin=new Thickness(0,0,16,0)};
    private StackPanel? _listenTapGroup,_listenEndpointGroup,_listenApplicationGroup;
    private Button? _listenRefresh;
    private bool _updatingListening;
    private bool WorkModeChanging=>_coordinator.IsModeSwitching;
    private WorkMode CurrentWorkMode=>_coordinator.CommittedMode;
    private object? _listenRefreshRequest;
    private int _sourceIconVersion;
    private string? _listenViewKey;
    private ListeningSource? _displayedListeningSource;

    private void BuildWorkModeSelector()
    {
        StyleField(_workModeBox);
        _workModeBox.ItemsSource=new[]{"录制","侦听"};_workModeBox.SelectedIndex=0;
        _workModeBox.Width=double.NaN;_workModeBox.MinWidth=64;_workModeBox.Height=34;_workModeBox.MinHeight=34;
        _workModeBox.Padding=new Thickness(8,0,0,0);
        _workModeBox.Loaded+=(_,_)=>
        {
            // Native ComboBox reserves 38 DIP for its arrow. Compact that column,
            // not the label: intrinsic sizing also accommodates text scaling.
            void CompactArrow(DependencyObject node)
            {
                if(node is FrameworkElement {Name:"DropDownGlyph"} glyph &&
                    Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(glyph) is Grid grid)
                {
                    int column=Grid.GetColumn(glyph);
                    if(column<grid.ColumnDefinitions.Count)grid.ColumnDefinitions[column].Width=new GridLength(24);
                    glyph.Margin=new Thickness(0,0,8,0);return;
                }
                for(int i=0;i<Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);i++)
                    CompactArrow(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node,i));
            }
            CompactArrow(_workModeBox);
        };
        _workModeBox.Background=Design.Brush(Colors.Transparent);
        foreach(var key in new[]{"ComboBoxBackground","ComboBoxBackgroundDisabled"})
            _workModeBox.Resources[key]=Design.Brush(Colors.Transparent);
        _workModeBox.Resources["ComboBoxBackgroundPointerOver"]=Design.Brush(Design.Selected);
        _workModeBox.Resources["ComboBoxBackgroundPressed"]=Design.Brush(Design.Hex("29292C"));
        _workModeBox.HorizontalAlignment=HorizontalAlignment.Left;_workModeBox.VerticalAlignment=VerticalAlignment.Center;
        _workModeBox.Margin=new Thickness(68,0,0,0);_workModeBox.TabIndex=1;
        AutomationProperties.SetName(_workModeBox,"工作模式");
        _workModeBox.SelectionChanged+=(_,_)=>
        {
            if(!_started||_workModeBox.SelectedIndex<0)return;
            var target=_workModeBox.SelectedIndex==1?WorkMode.Listening:WorkMode.Recording;
            bool active=_state.Session.Active||ListeningActive;
            if(!_coordinator.TryBeginModeSwitch(target,active))
            {
                RestoreWorkModeSelection();
                return;
            }
            // Return to the native ComboBox before disabling it or replacing its popup's
            // surrounding visual tree. Selecting an item must complete its own transaction.
            if(!DispatcherQueue.TryEnqueue(async()=>await ApplyWorkMode(target)))
            {
                _coordinator.EndModeSwitch();
                RestoreWorkModeSelection();
            }
        };
        _header.Children.Add(_workModeBox);
    }

    private void RestoreWorkModeSelection()=>_workModeBox.SelectedIndex=_state.IsListening?1:0;

    private async Task ApplyWorkMode(WorkMode target)
    {
        try
        {
            if(_closing||_audioBusy||_state.Session.Active)return;
            _workModeBox.IsEnabled=false;
            _stopButton.IsEnabled=false;
            foreach(var control in _recordingControls)control.IsEnabled=false;
            foreach(var control in _listeningControls)control.IsEnabled=false;
            RestoreWorkModeSelection();
            if(_state.IsListening)await _state.Listener.StopAsync();
            else
            {
                try{await _state.Audio.SuspendMonitoringAsync();}
                finally{StopVolumeWatcher();}
            }
            if(_closing)return;
            _listenRefreshRequest=null;
            _sourceIconVersion++;
            if(target==WorkMode.Listening)InvalidateRecordingInputQueries();
            _coordinator.CommitModeSwitch(target);
            _state.SetWorkMode(target==WorkMode.Listening);
            RestoreWorkModeSelection();
            _lastStage=null;_lastAudioState=null;_listenViewKey=null;_displayedListeningSource=null;
            UpdateWorkModeSurface();
            StartupLog.Write("Work mode: "+target);
            await (target==WorkMode.Listening ? RefreshListeningSources(modeInitialization:true) :
                _noCapture ? Task.CompletedTask : LoadDevices(manual:true,modeInitialization:true));
        }
        catch(Exception error)
        {
            StartupLog.Write("Work mode: "+error);
            if(!_closing)await Message("工作模式",AudioEngine.Friendly(error));
        }
        finally
        {
            _coordinator.EndModeSwitch();
            if(!_closing)
            {
                RestoreWorkModeSelection();
                _state.Refresh();
                if(_state.IsListening){_listenViewKey=null;RefreshListening();}
                else{RefreshSession(true);SyncCalibration();SyncInputFormat();_=DrainDeviceRefreshQueue();}
                RefreshValues();
            }
        }
    }

    private StackPanel BuildVuReferenceGroup(bool listening)
    {
        double[] values=[-18,-14,-10,-8,-6];
        var box=new ComboBox();StyleField(box);
        box.ItemsSource=values.Select(db=>$"−{Math.Abs(db):0} dBFS").ToArray();
        box.SelectedIndex=listening?4:0;
        AutomationProperties.SetName(box,"VU 参考");
        ToolTipService.SetToolTip(box,"0 VU 的正弦参考电平，仅影响表头。");
        box.SelectionChanged+=(_,_)=>
        {
            if(box.SelectedIndex<0||_state.IsListening!=listening)return;
            _state.SetVuReference(values[box.SelectedIndex]);
            RenderMonitor();
        };
        return Group("VU 参考","\uE9D9",box);
    }

    private void BuildListeningInspector(Grid top,Grid pages)
    {
        _listenHeading.Visibility=Visibility.Collapsed;_listenHeading.HorizontalAlignment=HorizontalAlignment.Left;
        top.Children.Add(_listenHeading);
        _listenHost.Visibility=Visibility.Collapsed;_listenHost.IsTabStop=false;
        _listenHost.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        _listenHost.HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
        _listenHost.HorizontalScrollMode=ScrollMode.Disabled;_listenHost.Content=_listenPage;pages.Children.Add(_listenHost);
        foreach(var field in new[]{_listenScope,_listenTap,_listenEndpoint,_listenApplication})StyleField(field,_listeningControls);
        _listenScope.ItemsSource=new[]{"整机侦听","应用侦听"};_listenScope.SelectedIndex=0;
        _listenTap.ItemsSource=new[]{"增强前","增强后 · 输出回环"};_listenTap.SelectedIndex=0;
        _listenEndpoint.DisplayMemberPath="Name";_listenApplication.DisplayMemberPath="Name";
        _listenEndpoint.PlaceholderText="没有可用输出设备";_listenApplication.PlaceholderText="选择应用";
        AutomationProperties.SetName(_listenScope,"侦听范围");AutomationProperties.SetName(_listenTap,"取流位置");
        AutomationProperties.SetName(_listenEndpoint,"输出设备");AutomationProperties.SetName(_listenApplication,"应用进程及其子进程");
        _listenPage.Children.Add(Group("侦听范围","\uE7F4",_listenScope));
        _listenTapGroup=Group("取流位置","\uE8AB",_listenTap);_listenPage.Children.Add(_listenTapGroup);
        _listenEndpointGroup=Group("输出设备","\uE767",_listenEndpoint);_listenPage.Children.Add(_listenEndpointGroup);
        _listenRefresh=LinkButton("刷新",_listeningControls);_listenRefresh.Click+=async(_,_)=>await RefreshListeningSources();
        var applicationHeading=new Grid{Height=24};
        applicationHeading.Children.Add(Label("应用","\uE7C4"));
        _listenRefresh.HorizontalAlignment=HorizontalAlignment.Right;
        _listenRefresh.Height=24;_listenRefresh.Padding=new Thickness(0);
        applicationHeading.Children.Add(_listenRefresh);
        _listenApplicationGroup=new StackPanel{Spacing=8};
        _listenApplicationGroup.Children.Add(applicationHeading);_listenApplicationGroup.Children.Add(_listenApplication);
        _listenPage.Children.Add(_listenApplicationGroup);
        _listenPage.Children.Add(BuildVuReferenceGroup(listening:true));
        _listenFormat.TextWrapping=TextWrapping.Wrap;_listenPage.Children.Add(_listenFormat);
        foreach(var field in new[]{_listenScope,_listenTap,_listenEndpoint,_listenApplication})
            field.SelectionChanged+=(_,_)=>{if(!_updatingListening){_listenViewKey=null;RefreshListening();}};
    }

    private void UpdateWorkModeSurface()
    {
        bool listening=_state.IsListening;
        _paneSelector.Visibility=listening?Visibility.Collapsed:Visibility.Visible;
        _listenHeading.Visibility=listening?Visibility.Visible:Visibility.Collapsed;
        _listenHost.Visibility=listening?Visibility.Visible:Visibility.Collapsed;
        _inputHost.Visibility=!listening&&_paneSelector.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;
        _fileHost.Visibility=!listening&&_paneSelector.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;
        _widthAnimation?.Stop();_primaryFade.Stop();_pauseFade.Stop();_clockFade.Stop();_statusFade.Stop();
        _transportGeneration++;_primaryContent.Opacity=1;
        _pauseButton.Visibility=Visibility.Collapsed;_pauseButton.Width=0;_discardButton.Visibility=Visibility.Collapsed;_discardButton.Width=0;
        _activeButtons.Spacing=0;_clockSlot.Visibility=Visibility.Collapsed;
        _inputName.SetRecording(false,false);
        if(!listening){_lastNoticeKey="";_inputName.UseMicrophone();RefreshSession(false);}
        else
        {
            _displayedListeningSource=SelectedListeningSource();
            _=SetListeningSourceIcon(_displayedListeningSource);
            RefreshListening();
        }
        RefreshValues();
    }

    private async Task RefreshListeningSources(bool modeInitialization=false)
    {
        if(!_coordinator.CanRefreshListeningSources(modeInitialization,ListeningActive,_listenRefreshRequest!=null))return;
        var request=new object();
        _listenRefreshRequest=request;
        bool OwnsRequest()=>!_closing&&_state.IsListening&&ReferenceEquals(_listenRefreshRequest,request);
        try
        {
            RefreshListening();
            var sources=await Task.Run(()=>(Endpoints:ListeningSources.EnumerateEndpoints(),Apps:ListeningSources.EnumerateApplications()));
            if(!OwnsRequest())return;
            string? endpoint=(_listenEndpoint.SelectedItem as AudioDevice)?.Id;
            var application=(_listenApplication.SelectedItem as ApplicationChoice)?.Source;
            _updatingListening=true;
            try
            {
                _listenEndpoint.ItemsSource=sources.Endpoints;
                _listenEndpoint.SelectedItem=sources.Endpoints.FirstOrDefault(d=>d.Id==endpoint)??sources.Endpoints.FirstOrDefault(d=>d.IsDefault)??sources.Endpoints.FirstOrDefault();
                var apps=sources.Apps.Select(s=>new ApplicationChoice(s)).ToArray();
                _listenApplication.ItemsSource=apps;
                _listenApplication.SelectedItem=application==null?null:apps.FirstOrDefault(p=>
                    p.Source.ProcessId==application.ProcessId&&p.Source.ProcessStartTicks==application.ProcessStartTicks);
            }
            finally{_updatingListening=false;}
            _listenViewKey=null;
        }
        catch(Exception error)
        {
            StartupLog.Write("Listening sources: "+error);
            if(OwnsRequest())await Message("侦听来源",AudioEngine.Friendly(error));
        }
        finally
        {
            if(ReferenceEquals(_listenRefreshRequest,request))
            {
                _listenRefreshRequest=null;
                if(!_closing&&_state.IsListening)RefreshListening();
            }
        }
    }

    private ListeningSource? SelectedListeningSource()
    {
        if(_listenScope.SelectedIndex==1)return (_listenApplication.SelectedItem as ApplicationChoice)?.Source;
        if(_listenTap.SelectedIndex==0)return new(ListeningScope.SystemBeforeEffects,"本机回环");
        return _listenEndpoint.SelectedItem is AudioDevice device?new(ListeningScope.SystemEndpoint,"本机回环",EndpointId:device.Id):null;
    }

    private async Task ToggleListening()
    {
        if(ListeningActive)await _state.Listener.StopAsync();
        else
        {
            if(_listenRefreshRequest!=null)return;
            var source=SelectedListeningSource()??throw new InvalidOperationException("请先选择要侦听的应用或输出设备。");
            await _state.Listener.StartAsync(source);
        }
        _listenViewKey=null;
    }

    private bool ListeningActive=>IsListeningActive(_state.Listener.Status.State);
    private static bool IsListeningActive(ListeningState state)=>state is
        ListeningState.Opening or ListeningState.Listening or ListeningState.Waiting;

    private void RefreshListening()
    {
        if(!_state.IsListening||_closing)return;
        var status=_state.Listener.Status;
        bool active=IsListeningActive(status.State);
        bool refreshing=_listenRefreshRequest!=null;
        bool application=_listenScope.SelectedIndex==1,endpoint=!application&&_listenTap.SelectedIndex==1;
        _workModeBox.IsEnabled=_coordinator.CanEnableWorkModeSelector(active);
        foreach(var box in new[]{_listenScope,_listenTap,_listenEndpoint,_listenApplication})box.IsEnabled=!active&&!_audioBusy&&!WorkModeChanging;
        if(_listenRefresh!=null)_listenRefresh.IsEnabled=!refreshing&&!active&&!_audioBusy&&!WorkModeChanging;
        _listenTapGroup!.Visibility=application?Visibility.Collapsed:Visibility.Visible;
        _listenEndpointGroup!.Visibility=endpoint?Visibility.Visible:Visibility.Collapsed;
        _listenApplicationGroup!.Visibility=application?Visibility.Visible:Visibility.Collapsed;
        var selected=SelectedListeningSource();
        var source=active?status.Source:selected;
        if(_displayedListeningSource!=source){_displayedListeningSource=source;_=SetListeningSourceIcon(source);}
        string key=$"{status.State}|{status.Message}|{_audioBusy}|{WorkModeChanging}|{refreshing}|{application}|{endpoint}|{source}|{status.Format}";
        if(key==_listenViewKey)return;_listenViewKey=key;
        _clockSlot.Visibility=Visibility.Collapsed;
        _inputName.SetRecording(false,false);
        _stopButton.Width=116;_stopButton.CornerRadius=new CornerRadius(5);
        _stopButton.IsEnabled=!_audioBusy&&!WorkModeChanging&&(active||!refreshing&&selected!=null);
        _pauseButton.Visibility=Visibility.Collapsed;_discardButton.Visibility=Visibility.Collapsed;_activeButtons.Spacing=0;
        _primaryFade.Stop();_primaryContent.Opacity=1;_primaryContent.Children.Clear();
        _primaryContent.Children.Add(Design.ActionContent(active?"stop":"play",active?"停止侦听":"开始侦听"));
        Design.SetBaseFill(_stopButton,Design.TransportAction,false);
        AutomationProperties.SetName(_stopButton,active?"停止侦听":"开始侦听");ToolTipService.SetToolTip(_stopButton,null);
        string description=application?"侦听所选应用及其子进程。来源不限定输出设备；应用退出后需重新选择。"
            :endpoint?"侦听此输出设备的混音；包含当前设备链路的增强与系统音量。其他设备的声音不在此范围。"
            :"侦听全部应用的增强前混音，不跟随 Windows 总音量；来源可跨输出设备，不含本程序。";
        ToolTipService.SetToolTip(_listenScope,description);
        ToolTipService.SetToolTip(_listenTap,"侦听不保存文件，增益仅影响仪表。");
        _listenFormat.Text=status.Format is {} format?"实际取流："+FormatName(format):"";
        _serviceNotice.SetNotice(status.State is ListeningState.Stopped or ListeningState.Listening?"":status.Message,status.Message);
    }

    private async Task SetListeningSourceIcon(ListeningSource? source)
    {
        int version=++_sourceIconVersion;
        bool app=source?.Scope==ListeningScope.Application;
        _inputName.SetListeningSource(app?source!.Name:source==null&&_listenScope.SelectedIndex==1?"选择应用":"本机回环",app);
        if(!app||string.IsNullOrWhiteSpace(source!.ExecutablePath))return;
        try
        {
            var file=await StorageFile.GetFileFromPathAsync(source.ExecutablePath);
            using var thumbnail=await file.GetThumbnailAsync(ThumbnailMode.SingleItem,32);
            if(thumbnail==null)return;
            var image=new BitmapImage();await image.SetSourceAsync(thumbnail);
            if(!_closing&&_state.IsListening&&version==_sourceIconVersion)_inputName.SetApplicationIcon(image);
        }
        catch(Exception error){StartupLog.Write("Application icon unavailable: "+error.Message);}
    }
}
