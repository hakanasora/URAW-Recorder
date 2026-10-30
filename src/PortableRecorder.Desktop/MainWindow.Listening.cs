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
    private readonly TextBlock _listenDescription=Design.Text("",11,Design.Label);
    private readonly TextBlock _listenFormat=Design.Text("",11,Design.Muted);
    private readonly StackPanel _listenPage=new(){Spacing=16,Margin=new Thickness(0,0,16,0)};
    private StackPanel? _listenTapGroup,_listenEndpointGroup,_listenApplicationGroup;
    private Button? _listenRefresh;
    private bool _updatingListening,_changingWorkMode;
    private int _listenQuery,_sourceIconVersion;
    private string? _listenViewKey;
    private ListeningSource? _displayedListeningSource;

    private void BuildWorkModeSelector()
    {
        StyleField(_workModeBox);
        _workModeBox.ItemsSource=new[]{"录制","侦听"};_workModeBox.SelectedIndex=0;
        _workModeBox.Width=92;_workModeBox.Height=34;_workModeBox.MinHeight=34;
        _workModeBox.HorizontalAlignment=HorizontalAlignment.Left;_workModeBox.VerticalAlignment=VerticalAlignment.Center;
        _workModeBox.Margin=new Thickness(76,0,0,0);_workModeBox.TabIndex=1;
        AutomationProperties.SetName(_workModeBox,"工作模式");
        _workModeBox.SelectionChanged+=(_,_)=>
        {
            if(!_started||_changingWorkMode)return;
            bool listening=_workModeBox.SelectedIndex==1;
            if(listening==_state.IsListening)return;
            if(_audioBusy||_state.Session.Active){RestoreWorkModeSelection();return;}
            _changingWorkMode=true;
            // Return to the native ComboBox before disabling it or replacing its popup's
            // surrounding visual tree. Selecting an item must complete its own transaction.
            if(!DispatcherQueue.TryEnqueue(async()=>
            {
              try
              {
               StartupLog.Write("Work mode: "+(listening?"listening":"recording"));
               await AudioCommand(async()=>
               {
                // Quiesce the old owner before exposing the next mode. Neither source runs hidden.
                if(listening){await _state.Audio.SuspendMonitoringAsync();StopVolumeWatcher();}
                else await _state.Listener.StopAsync();
                if(_closing)return;
                _state.SetWorkMode(listening);
                _lastStage=null;_lastAudioState=null;_listenViewKey=null;_displayedListeningSource=null;
                UpdateWorkModeSurface();
                if(listening)await RefreshListeningSources();
                else if(!_noCapture)
                {
                    var devices=await Task.Run(_state.Audio.EnumerateDevices);
                    if(_closing)return;
                    await _state.Audio.ReconcileDevicesAsync(devices,true);
                    var current=_state.Audio.SelectedDevice;
                    _state.SelectedInput=current==null?new("","未选择输入设备"):new(current.Id,current.Name);
                }
               });
               RestoreWorkModeSelection();
               if(!_state.IsListening&&!_noCapture)await LoadDevices(manual:false);
              }
              finally{_changingWorkMode=false;}
            }))_changingWorkMode=false;
        };
        _header.Children.Add(_workModeBox);
    }

    private void RestoreWorkModeSelection()=>_workModeBox.SelectedIndex=_state.IsListening?1:0;

    private void BuildListeningInspector(Grid top,Grid pages)
    {
        _listenHeading.Visibility=Visibility.Collapsed;_listenHeading.HorizontalAlignment=HorizontalAlignment.Left;
        top.Children.Add(_listenHeading);
        _listenHost.Visibility=Visibility.Collapsed;_listenHost.IsTabStop=false;
        _listenHost.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        _listenHost.HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
        _listenHost.HorizontalScrollMode=ScrollMode.Disabled;_listenHost.Content=_listenPage;pages.Children.Add(_listenHost);
        foreach(var field in new[]{_listenScope,_listenTap,_listenEndpoint,_listenApplication})StyleField(field);
        _listenScope.ItemsSource=new[]{"整机侦听","应用侦听"};_listenScope.SelectedIndex=0;
        _listenTap.ItemsSource=new[]{"增强前","增强后 · 输出回环"};_listenTap.SelectedIndex=0;
        _listenEndpoint.DisplayMemberPath="Name";_listenApplication.DisplayMemberPath="Name";
        _listenEndpoint.PlaceholderText="没有可用输出设备";_listenApplication.PlaceholderText="选择应用";
        AutomationProperties.SetName(_listenScope,"侦听范围");AutomationProperties.SetName(_listenTap,"取流位置");
        AutomationProperties.SetName(_listenEndpoint,"输出设备");AutomationProperties.SetName(_listenApplication,"应用进程及其子进程");
        _listenPage.Children.Add(Group("侦听范围","\uE7F4",_listenScope));
        _listenTapGroup=Group("取流位置","\uE8AB",_listenTap);_listenPage.Children.Add(_listenTapGroup);
        _listenEndpointGroup=Group("输出设备","\uE767",_listenEndpoint);_listenPage.Children.Add(_listenEndpointGroup);
        _listenApplicationGroup=Group("应用","\uE7C4",_listenApplication);_listenPage.Children.Add(_listenApplicationGroup);
        _listenRefresh=LinkButton("刷新");_listenRefresh.Click+=async(_,_)=>await RefreshListeningSources();
        _listenPage.Children.Add(_listenRefresh);
        _listenDescription.TextWrapping=TextWrapping.Wrap;_listenFormat.TextWrapping=TextWrapping.Wrap;
        _listenPage.Children.Add(_listenDescription);_listenPage.Children.Add(_listenFormat);
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
        if(!listening){_inputName.UseMicrophone();RefreshSession(false);}
        else{_=SetListeningSourceIcon(SelectedListeningSource());RefreshListening();}
        RefreshValues();
    }

    private async Task RefreshListeningSources()
    {
        if(_closing||!_state.IsListening)return;
        int query=++_listenQuery;
        string? endpoint=(_listenEndpoint.SelectedItem as AudioDevice)?.Id;
        var application=(_listenApplication.SelectedItem as ApplicationChoice)?.Source;
        _listenRefresh!.IsEnabled=false;
        try
        {
            var sources=await Task.Run(()=>(Endpoints:ListeningSources.EnumerateEndpoints(),Apps:ListeningSources.EnumerateApplications()));
            if(_closing||query!=_listenQuery||!_state.IsListening)return;
            _updatingListening=true;
            try
            {
                _listenEndpoint.ItemsSource=sources.Endpoints;
                _listenEndpoint.SelectedItem=sources.Endpoints.FirstOrDefault(d=>d.Id==endpoint)??sources.Endpoints.FirstOrDefault(d=>d.IsDefault)??sources.Endpoints.FirstOrDefault();
                var apps=sources.Apps.Select(s=>new ApplicationChoice(s)).ToArray();
                _listenApplication.ItemsSource=apps;
                _listenApplication.SelectedItem=apps.FirstOrDefault(p=>p.Source.ProcessId==application?.ProcessId&&p.Source.ProcessStartTicks==application.ProcessStartTicks);
            }
            finally{_updatingListening=false;}
            _listenViewKey=null;RefreshListening();
        }
        catch(Exception error){StartupLog.Write("Listening sources: "+error);if(!_closing)await Message("侦听来源",AudioEngine.Friendly(error));}
        finally{if(!_closing&&query==_listenQuery)_listenRefresh!.IsEnabled=true;}
    }

    private ListeningSource? SelectedListeningSource()
    {
        if(_listenScope.SelectedIndex==1)return (_listenApplication.SelectedItem as ApplicationChoice)?.Source;
        if(_listenTap.SelectedIndex==0)return new(ListeningScope.SystemBeforeEffects,"本机回环");
        return _listenEndpoint.SelectedItem is AudioDevice device?new(ListeningScope.SystemEndpoint,"本机回环",EndpointId:device.Id):null;
    }

    private async Task ToggleListening()
    {
        var state=_state.Listener.Status.State;
        if(state is ListeningState.Listening or ListeningState.Waiting or ListeningState.Opening)await _state.Listener.StopAsync();
        else
        {
            var source=SelectedListeningSource()??throw new InvalidOperationException("请先选择要侦听的应用或输出设备。");
            await _state.Listener.StartAsync(source);
        }
        _listenViewKey=null;
    }

    private void RefreshListening()
    {
        if(!_state.IsListening||_closing)return;
        var status=_state.Listener.Status;
        bool active=status.State is ListeningState.Opening or ListeningState.Listening or ListeningState.Waiting;
        bool application=_listenScope.SelectedIndex==1,endpoint=!application&&_listenTap.SelectedIndex==1;
        _workModeBox.IsEnabled=!_audioBusy;
        foreach(var box in new[]{_listenScope,_listenTap,_listenEndpoint,_listenApplication})box.IsEnabled=!active&&!_audioBusy;
        if(_listenRefresh!=null)_listenRefresh.IsEnabled=!active&&!_audioBusy;
        _listenTapGroup!.Visibility=application?Visibility.Collapsed:Visibility.Visible;
        _listenEndpointGroup!.Visibility=endpoint?Visibility.Visible:Visibility.Collapsed;
        _listenApplicationGroup!.Visibility=application?Visibility.Visible:Visibility.Collapsed;
        var selected=SelectedListeningSource();
        var source=active?status.Source:selected;
        if(_displayedListeningSource!=source){_displayedListeningSource=source;_=SetListeningSourceIcon(source);}
        string key=$"{status.State}|{status.Message}|{_audioBusy}|{application}|{endpoint}|{source}|{status.Format}";
        if(key==_listenViewKey)return;_listenViewKey=key;
        _clockSlot.Visibility=Visibility.Collapsed;
        _inputName.SetRecording(false,false);
        _stopButton.Width=116;_stopButton.CornerRadius=new CornerRadius(5);
        _stopButton.IsEnabled=!_audioBusy&&(active||selected!=null);
        _pauseButton.Visibility=Visibility.Collapsed;_discardButton.Visibility=Visibility.Collapsed;_activeButtons.Spacing=0;
        _primaryFade.Stop();_primaryContent.Opacity=1;_primaryContent.Children.Clear();
        _primaryContent.Children.Add(Design.ActionContent(active?"stop":"play",active?"停止侦听":"开始侦听"));
        Design.SetBaseFill(_stopButton,Design.TransportAction,false);
        AutomationProperties.SetName(_stopButton,active?"停止侦听":"开始侦听");ToolTipService.SetToolTip(_stopButton,active?"停止侦听":"开始侦听");
        _listenDescription.Text=application?"侦听所选应用及其子进程。来源不限定输出设备；应用退出后需重新选择。"
            :endpoint?"侦听此输出设备的混音；包含当前设备链路的增强与系统音量。其他设备的声音不在此范围。"
            :"侦听全部应用的增强前混音，不跟随 Windows 总音量；来源可跨输出设备，不含本程序。";
        ToolTipService.SetToolTip(_listenDescription,"当前 SMSL / Equalizer APO 链路已验证前后取流差异。其他驱动效果、空间音效与受保护内容不作统一保证。侦听只作分析，不写入文件；增益仅影响仪表。");
        _listenFormat.Text=status.Format is {} format?"实际取流："+FormatName(format):"";
        _serviceNotice.Text=status.State==ListeningState.Stopped?"":status.Message;
        ToolTipService.SetToolTip(_serviceNotice,status.Message);
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
