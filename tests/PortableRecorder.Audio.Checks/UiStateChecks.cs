using PortableRecorder.Audio;
using PortableRecorder.Desktop;

static class UiStateChecks
{
    public static async Task Run(string root,Action<bool,string> check)
    {
        string calibration=NoticePresentation.Details("校准尚未生效，请选择有效文件或禁用校准。","共享监看 · RAW 请求已接受；校准尚未生效：请选择校准文件，或禁用校准后录制。","校准尚未生效，请选择有效文件。\n校准尚未生效：请选择校准文件，或禁用校准后录制。\n未应用校准；保留原始频响。未启用绝对声压。");
        check(NoticePresentation.Details("校准尚未生效，请选择有效文件。","独占监看 · RAW 请求已接受；校准尚未生效：请选择校准文件，或禁用校准后录制。")
            =="校准尚未生效，请选择有效文件或禁用校准。","Exclusive monitor calibration notice deduplicates the same missing calibration");
        check(calibration.Split("校准尚未生效").Length==2,"Equivalent calibration notices appear once");
        check(calibration.Contains("未启用绝对声压"),"Distinct calibration limitations survive deduplication");
        check(NoticePresentation.Details("原因甲","原因乙","原因甲")=="原因甲\n原因乙","Distinct causes preserved and exact duplicates removed");
        check(NoticePresentation.Details(@"C:\录音；备份\test.wav")==@"C:\录音；备份\test.wav","Paths are preserved verbatim");
        check(NoticePresentation.Title("校准尚未生效，请选择有效文件或禁用校准。")=="校准尚未生效，请选择有效文件或禁用校准","Dialog title describes its notice");
        check(NoticePresentation.Title("录音继续中：压缩文件出现削波。降低增益或使用浮点 WAV 可避免超限损失。")=="录音出现削波","Clipping gets concise specific title");
        const string denied="无法访问输入或文件，请检查 Windows 麦克风隐私权限及路径权限。";
        check(NoticePresentation.RepeatsBody(NoticePresentation.Title(denied),denied),"Permission error body already carries the title, even without its final punctuation");
        check(NoticePresentation.RepeatsBody("等待音频播放","等待音频播放"),"Listening status does not duplicate the body as its heading");
        check(NoticePresentation.RepeatsBody("缺帧未确认","缺帧未确认\n文件可能不完整。"),"A heading repeated as a standalone detail line is redundant");
        check(!NoticePresentation.RepeatsBody("录音出现削波","录音继续中：压缩文件出现削波。请降低增益。"),"A distinct summary remains available");
        {
            var factory=new FakeFactory();await using var engine=TestSupport.CreateEngine(root,factory);
            var model=new ShellViewModel(engine);await using var listener=model.Listener;
            model.SetGain(12);model.SetWorkMode(true);
            check(model.GainDb==0,"Listening gain starts at zero instead of inheriting recording gain");
            model.SetGain(-6);check(listener.GainDb==-6,"Listening knob drives independent analysis gain");
            model.SetWorkMode(false);check(model.GainDb==12,"Returning to recording restores its own gain");
            model.SetWorkMode(true);check(model.GainDb==-6&&!model.Snapshot.Valid,"Listening gain is retained and old microphone samples are not shown");
        }
        check(InputFormatPresentation.EndpointGain(new(30,false),false).Contains("+30.0 dB")&&InputFormatPresentation.EndpointGain(new(30,false),false).Contains("独占绕过"),"Shared software endpoint gain is disclosed");
        check(InputFormatPresentation.EndpointGain(new(30,false),true).Contains("已绕过"),"Exclusive mode does not claim to apply Windows software gain");
        check(!InputFormatPresentation.EndpointGain(new(6,true),true).Contains("已绕过"),"Hardware volume must not be described as bypassed");
        check(InputFormatPresentation.EndpointGain(null,false).Contains("未知"),"Unavailable volume metadata is not fabricated as zero gain");
        check(InputFormatPresentation.AlignmentUnavailable(new(30,false))==null&&InputFormatPresentation.AlignmentUnavailable(new(-60,false))==null,"Supported software gain endpoints can be aligned");
        check(InputFormatPresentation.AlignmentUnavailable(new(0,false,true))!=null&&InputFormatPresentation.AlignmentUnavailable(new(0,true))!=null,"Muted and hardware endpoints cannot be aligned");
        check(InputFormatPresentation.AlignmentUnavailable(null)!=null&&InputFormatPresentation.AlignmentUnavailable(new(61,false))!=null&&InputFormatPresentation.AlignmentUnavailable(new(-61,false))!=null,"Unknown and out-of-range gain cannot be aligned");
        var transportCases=new[]{
            (AudioRunState.Stopped,false,false,false,false,0,100),
            (AudioRunState.Opening,false,false,false,false,0,100),
            (AudioRunState.Monitoring,false,false,true,false,0,100),
            (AudioRunState.Preparing,true,false,true,false,42,100),
            (AudioRunState.Recording,true,false,true,true,42,100),
            (AudioRunState.Pausing,true,false,true,false,42,100),
            (AudioRunState.Paused,true,true,true,true,100,42),
            (AudioRunState.Finalizing,true,false,false,false,42,100),
            (AudioRunState.Faulted,false,false,false,false,0,100)};
        foreach(var (state,active,paused,canStop,canPause,pauseWidth,stopWidth) in transportCases)
        foreach(bool busy in new[]{false,true})
        {
            var view=TransportPresentation.From(state,busy,true);
            check(view.Active==active&&view.Paused==paused,$"Transport activity projection {state}/{busy}");
            check(view.CanStop==(!busy&&canStop)&&view.CanPause==(!busy&&canPause),"Transport commands follow service state and command lock");
            check(view.CanDiscard==(!busy&&paused),"Discard enabled only in idle-command paused state");
            check(view.PauseWidth==pauseWidth&&view.StopWidth==stopWidth,"Existing paired button widths preserved");
        }
        check(TransportPresentation.From(AudioRunState.Monitoring,false,true).CanStop,"Ready capture can start recording");
        check(!TransportPresentation.From(AudioRunState.Monitoring,false,false).CanStop,"Incomplete calibration blocks recording");
        check(TransportPresentation.From(AudioRunState.Pausing,false,true).StatusText=="正在暂停"&&
            TransportPresentation.From(AudioRunState.Paused,false,true).StatusText=="暂停中"&&
            TransportPresentation.From(AudioRunState.Finalizing,false,true).StatusText=="正在保存","Native status labels retained");

        // Calibration keeps Preparing visible while delayed samples reach the writer. Those
        // status changes and command completions must not restart the same transport motion.
        var session=new TransportSession();RecordingStage? previous=null;int motions=0;
        foreach(var state in new[]{AudioRunState.Monitoring,AudioRunState.Preparing,
            AudioRunState.Preparing,AudioRunState.Recording,AudioRunState.Recording,AudioRunState.Pausing,AudioRunState.Finalizing})
        {
            session.Update(state,TimeSpan.Zero);
            if(TransportPresentation.NeedsMotionUpdate(previous,session.Stage,false))motions++;
            previous=session.Stage;
        }
        check(motions==2,"Calibration preparation, first samples and finalization preserve the existing recording motion");
        check(TransportPresentation.NeedsMotionUpdate(previous,RecordingStage.Paused,false)&&
            TransportPresentation.NeedsMotionUpdate(RecordingStage.Paused,RecordingStage.Recording,false),"Pause and resume each get a transport transition");
        check(!TransportPresentation.NeedsMotionUpdate(previous,previous!.Value,false)&&
            TransportPresentation.NeedsMotionUpdate(previous,previous.Value,true),"Command completion preserves motion; explicit accessibility reset settles it");

        check(InputFormatPresentation.CanSelect(RecordingMode.Exclusive,true,false,true,false,false),"Exclusive idle format selection enabled");
        foreach(var args in new[]{(RecordingMode.Shared,true,false,true,false,false),(RecordingMode.Exclusive,false,false,true,false,false),
            (RecordingMode.Exclusive,true,true,true,false,false),(RecordingMode.Exclusive,true,false,false,false,false),
            (RecordingMode.Exclusive,true,false,true,true,false),(RecordingMode.Exclusive,true,false,true,false,true)})
            check(!InputFormatPresentation.CanSelect(args.Item1,args.Item2,args.Item3,args.Item4,args.Item5,args.Item6),"Unavailable input formats remain locked");
        check(InputFormatPresentation.Name(new(48000,2,32,32,PcmEncoding.Float))=="2 声道 · float32 · 48 kHz","Float format label");
        check(InputFormatPresentation.Name(new(44100,1,32,24,PcmEncoding.SignedInteger)).Contains("24/32 bit"),"Valid bits distinguished from container bits");

        var prompt=new RawDecisionPrompt();
        check(prompt.TryBegin(1)&&!prompt.TryBegin(1)&&!prompt.TryBegin(2),"Only one RAW prompt in flight");
        prompt.Abandon(1);check(prompt.TryBegin(1),"Display failure leaves RAW decision retryable");
        prompt.Complete(1);check(!prompt.TryBegin(1)&&prompt.TryBegin(2),"Completed RAW request is not replayed, new request is accepted");
        prompt.Abandon(1);check(!prompt.TryBegin(3),"Stale completion cannot clear newer RAW prompt");
        prompt.Abandon(2);check(prompt.TryBegin(3),"Current prompt abandonment releases it");

        var queue=new DialogQueue();var firstRelease=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered=0;
        var first=queue.ShowAsync(_=>{entered++;return firstRelease.Task;});
        var second=queue.ShowAsync(_=>{entered++;return Task.FromResult(2);});
        check(queue.IsBusy&&entered==1&&!second.IsCompleted,"Error and consent dialogs serialize before entering presenter");
        firstRelease.SetResult(1);check(await first==1&&await second==2&&entered==2&&!queue.IsBusy,"Queued dialog runs after first releases");
        bool failed=false;try{await queue.ShowAsync<int>(_=>throw new InvalidOperationException("injected ShowAsync failure"));}catch(InvalidOperationException){failed=true;}
        check(failed&&!queue.IsBusy&&await queue.ShowAsync(_=>Task.FromResult(3))==3,"Failed presenter releases modal gate");

        queue=new DialogQueue();var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);bool staleOpened=false;
        first=queue.ShowAsync(async token=>{started.SetResult();await Task.Delay(Timeout.InfiniteTimeSpan,token);return 1;});await started.Task;
        second=queue.ShowAsync(_=>{staleOpened=true;return Task.FromResult(2);});queue.Close();queue.Close();
        foreach(var pending in new[]{first,second})
        {bool cancelled=false;try{await pending;}catch(OperationCanceledException){cancelled=true;}check(cancelled,"Window close cancels visible and queued dialog work");}
        check(!staleOpened&&!queue.IsBusy,"Closing cannot create a late dialog or keep busy state");
        bool afterClose=false;try{await queue.ShowAsync(_=>Task.FromResult(4));}catch(OperationCanceledException){afterClose=true;}
        check(afterClose&&!queue.IsBusy,"Closed queue rejects later completions");

        queue=new DialogQueue();
        var dismissed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowExit=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first=queue.ShowAsync(async token=>
        {
            using var registration=token.Register(()=>dismissed.TrySetResult());
            await allowExit.Task;
            return 1;
        });
        staleOpened=false;
        second=queue.ShowAsync(_=>{staleOpened=true;return Task.FromResult(2);});
        queue.BeginCleanup();queue.BeginCleanup();
        await dismissed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        check(!await queue.WaitForPresenterAsync(TimeSpan.Zero),"Shutdown can detect a presenter that has not released");
        bool cleanupOpened=false;
        var cleanup=queue.ShowAsync(_=>{cleanupOpened=true;return Task.FromResult(3);},cleanupError:true);
        check(!cleanupOpened,"Cleanup error waits for the previous presenter to exit");
        allowExit.SetResult();
        check(await first==1&&await cleanup==3&&cleanupOpened&&!staleOpened,"Only the cleanup error may follow the active dialog");
        foreach(var pending in new[]{second,queue.ShowAsync(_=>Task.FromResult(4)),queue.ShowAsync(_=>Task.FromResult(5),cleanupError:true)})
        {
            bool cancelled=false;try{await pending;}catch(OperationCanceledException){cancelled=true;}
            check(cancelled,"Cleanup rejects ordinary dialogs and duplicate cleanup errors");
        }
        queue.Close();
        check(!queue.IsBusy&&await queue.WaitForPresenterAsync(TimeSpan.Zero),"Cleanup leaves no active or queued dialog");

        var coordinator=new MainWindowCoordinator();
        coordinator.TryMarkStarted();
        check(!coordinator.TryBeginModeSwitch(WorkMode.Listening,sessionActive:true),"An active recording blocks mode changes");
        check(coordinator.TryBeginModeSwitch(WorkMode.Listening,sessionActive:false),"Idle recording accepts a mode change");
        check(!coordinator.TryBeginModeSwitch(WorkMode.Listening,sessionActive:false),"A rapid repeat cannot replace the pending target");
        coordinator.EndModeSwitch();
        check(coordinator.CommittedMode==WorkMode.Recording,"Ending failed switch before commit preserves committed mode");

        check(coordinator.TryBeginModeSwitch(WorkMode.Listening,sessionActive:false),"Failed switch can be retried");
        coordinator.CommitModeSwitch(WorkMode.Listening);
        check(coordinator.CommittedMode==WorkMode.Listening&&coordinator.IsModeSwitching,"Committed Listening holds initialization gate until EndModeSwitch");
        coordinator.EndModeSwitch();
        check(coordinator.CommittedMode==WorkMode.Listening&&!coordinator.IsModeSwitching,"Failed or completed refresh preserves the new committed mode");

        check(coordinator.TryBeginModeSwitch(WorkMode.Recording,sessionActive:false),"Return to recording can begin");
        coordinator.TryBeginCleanup();
        check(coordinator.IsClosing&&coordinator.CommittedMode==WorkMode.Listening,"Closing while old service stops suppresses commit");

        await CheckInputQueries(check);
        await CalibrationCollectionChecks(root,check);
        CoordinatorChecks(check);
        MonitorToggleVisualChecks(check);
        await ShellViewModelSnapshotChecks(root,check);
    }

    private static void MonitorToggleVisualChecks(Action<bool,string> check)
    {
        var transparent=Windows.UI.Color.FromArgb(0,255,255,255);
        var muted=Windows.UI.Color.FromArgb(255,0x86,0x86,0x8B);
        var grid=Windows.UI.Color.FromArgb(255,0x29,0x29,0x2C);
        var pressed=Windows.UI.Color.FromArgb(255,0x1F,0x1F,0x22);
        var white=Windows.UI.Color.FromArgb(255,0xF5,0xF5,0xF7);

        var unlitDefault=RecordingMonitorPresentation.ResolveToggleVisual(enabled:false,hovering:false,pressed:false);
        check(unlitDefault==new ToggleVisualColors(transparent,transparent,muted),"Unlit toggle default uses transparent surface, no border, and muted icon");

        var unlitHover=RecordingMonitorPresentation.ResolveToggleVisual(enabled:false,hovering:true,pressed:false);
        check(unlitHover==new ToggleVisualColors(grid,transparent,muted),"Unlit toggle hover fills Grid background without border while icon stays muted");

        foreach(bool hovering in new[]{false,true})
        {
            var unlitPressed=RecordingMonitorPresentation.ResolveToggleVisual(enabled:false,hovering,pressed:true);
            check(unlitPressed==new ToggleVisualColors(pressed,transparent,muted),"Unlit toggle pressed darkens fill without border and keeps muted icon");
        }

        foreach(bool hovering in new[]{false,true})
        {
            var lit=RecordingMonitorPresentation.ResolveToggleVisual(enabled:true,hovering,pressed:false);
            check(lit==new ToggleVisualColors(grid,transparent,white),"Lit toggle permanently fills Grid background without border and illuminates icon");

            var litPressed=RecordingMonitorPresentation.ResolveToggleVisual(enabled:true,hovering,pressed:true);
            check(litPressed==new ToggleVisualColors(pressed,transparent,white),"Lit toggle pressed darkens fill without border while icon stays illuminated");
        }
    }

    private static async Task CheckInputQueries(Action<bool,string> check)
    {
        var queue=new DeviceRefreshQueue();
        queue.RestartDebounce();
        check(queue.TryBegin(true,true)==null,"A topology notification waits for debounce");
        queue.DebounceElapsed();
        check(queue.TryBegin(false,false)==null,"An expired debounce retains work while blocked");
        var first=queue.TryBegin(true,true)!;
        queue.RestartDebounce();queue.DebounceElapsed();
        queue.RestartDebounce();
        check(queue.TryBegin(true,true)==null,"Changes during enumeration never start another enumeration");
        var readyToReconcile=first.WaitUntilReadyAsync();
        check(queue.TryBegin(false,false)==null&&!readyToReconcile.IsCompleted,"Audio or modal work holds an enumerated result");
        check(queue.TryBegin(true,true)==null&&readyToReconcile.IsCompleted,"The active reconciliation resumes before a new enumeration starts");
        queue.Finish(first,Task.CompletedTask);
        check(queue.TryBegin(true,true)==null,"A change during enumeration waits for the latest debounce");
        queue.DebounceElapsed();
        var followUp=queue.TryBegin(true,true)!;
        check(!followUp.Manual,"Coalesced changes run one automatic follow-up");
        queue.Finish(followUp,Task.CompletedTask);
        check(queue.TryBegin(true,true)==null,"Finishing an automatic enumeration does not retry the same notification");

        var priorCompletion=queue.Enqueue(manual:false,modeInitialization:false);
        var prior=queue.TryBegin(true,true)!;
        queue.Invalidate();
        var manual=queue.Enqueue(manual:true,modeInitialization:false);
        var initialize=queue.Enqueue(manual:true,modeInitialization:true);
        check(prior.Invalidated&&priorCompletion.IsCompleted,"Changing workspace invalidates callers waiting on the old enumeration");
        check(queue.TryBegin(false,true)==null&&!initialize.IsCompleted,"Mode initialization waits for the prior enumeration to return");
        queue.Finish(prior,Task.CompletedTask);
        var force=queue.TryBegin(false,true)!;
        check(force.Manual&&force.ModeInitialization&&ReferenceEquals(manual,initialize),"Manual and mode initialization intent coalesce into one forced pass");
        var formatGate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Finish(force,formatGate.Task);
        check(!initialize.IsCompleted,"Mode initialization still awaits its format refresh");
        queue.DebounceElapsed();
        var independent=queue.TryBegin(true,true)!;
        check(!independent.Manual,"A topology enumeration can proceed while input formats are loading");
        queue.Finish(independent,Task.CompletedTask);
        formatGate.SetResult();
        await initialize.WaitAsync(TimeSpan.FromSeconds(5));

        var format=new AudioFormat(48000,2,32,32,PcmEncoding.Float);
        var options=new InputFormatOptions(format,[format]);
        var query=new InputFormatQuery();
        var a=query.Begin("a");
        var b=query.Begin("b");
        check(!query.TryComplete(a,"b",true,null,"late error")&&query.Loading&&query.Error==null,"A stale failure cannot clear the newer request's loading state");
        check(query.TryComplete(b,"b",true,options,null)&&query.Options==options&&!query.Loading,"The current device can finish and publish its formats first");
        check(!query.TryComplete(a,"b",true,null,"late error")&&query.Options==options&&query.Error==null,"An older request finishing later cannot replace the current result");

        var switched=query.Begin("a");
        check(!query.TryComplete(switched,"b",true,null,"wrong device")&&query.Loading&&query.Error==null,"A matching request ID still requires the current selected device");
        check(!query.TryComplete(switched,"a",false,options,null)&&query.Loading,"A current device still requires a current recording workspace");
        query.Invalidate();
        var reopened=query.Begin("a");
        check(!query.TryComplete(switched,"a",true,options,null)&&query.Loading,"Returning to the same device cannot revive an earlier workspace's result");
        check(query.TryComplete(reopened,"a",true,null,"current error")&&query.Error=="current error"&&!query.Loading,"Only the current request publishes its error and clears loading");
        var closing=query.Begin("a");
        query.Invalidate();
        check(!query.TryComplete(closing,"a",false,options,null)&&query.Options==null&&!query.Loading,"An input format response arriving after close has no effect");
    }

    private static async Task CalibrationCollectionChecks(string root,Action<bool,string> check)
    {
        string directory=Path.Combine(root,"ui-calibration-collection");Directory.CreateDirectory(directory);
        var library=new CalibrationLibrary(Path.Combine(directory,"library"));
        var factory=new FakeFactory();
        await using var engine=new AudioEngine(factory,library,inputFormats:new(Path.Combine(directory,"formats.json")),liveMonitorFactory:new FakeLiveMonitorFactory());
        await engine.SelectDeviceAsync(factory.Device);
        CalibrationLibrary.Entry Import(string name)
        {
            string path=Path.Combine(directory,name+".cal");
            File.WriteAllText(path,$"# {name}\n10 0\n24000 0");
            return library.Import(path);
        }
        var first=Import("first");var second=Import("second");
        var model=new ShellViewModel(engine);var collection=model.Calibrations;
        var firstItem=collection.Single(c=>c.Id==first.Id);var secondItem=collection.Single(c=>c.Id==second.Id);
        var changes=new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        collection.CollectionChanged+=(_,args)=>changes.Add(args.Action);

        for(int i=0;i<100;i++)model.RefreshCalibrations();
        check(ReferenceEquals(collection,model.Calibrations)&&changes.Count==0,"Unchanged calibration polling preserves collection without notifications");
        check(ReferenceEquals(firstItem,collection[0])&&ReferenceEquals(secondItem,collection[1]),"Unchanged calibration polling preserves item identity and order");
        check(model.SelectedCalibration==null,"New endpoint has no accidental calibration selection");

        library.Configure(factory.Device.Id,new(true,first.Id));model.RefreshCalibrations();
        check(ReferenceEquals(model.SelectedCalibration,firstItem)&&changes.Count==0,"Selection-only revision reuses existing calibration item");
        library.Configure(factory.Device.Id,new(false,second.Id));model.RefreshCalibrations();
        check(ReferenceEquals(model.SelectedCalibration,secondItem)&&changes.Count==0,"Disabled calibration remembers the selected file without rebuilding rows");

        var third=Import("third");model.RefreshCalibrations();
        check(changes.SequenceEqual(new[]{System.Collections.Specialized.NotifyCollectionChangedAction.Add})&&collection.Count==3,"Import incrementally adds one calibration row, never clears the list");
        check(ReferenceEquals(firstItem,collection[0])&&ReferenceEquals(secondItem,collection[1])&&ReferenceEquals(model.SelectedCalibration,secondItem),"Import preserves existing row identities and remembered selection");
        check(collection.Single(c=>c.Id==third.Id).Path==library.GetLocalPath(third.Id),"Incremental row uses owned-copy path from its library snapshot");

        changes.Clear();library.Remove(first.Id);model.RefreshCalibrations();
        check(changes.SequenceEqual(new[]{System.Collections.Specialized.NotifyCollectionChangedAction.Remove})&&ReferenceEquals(collection[0],secondItem),"Removal updates one row and preserves surviving instances");
        library.Remove(second.Id);model.RefreshCalibrations();
        check(model.SelectedCalibration==null&&collection.Count==1&&collection[0].Id==third.Id,"Removing selected calibration never automatically picks another file");
    }

    private static async Task ShellViewModelSnapshotChecks(string root,Action<bool,string> check)
    {
        var factory=new FakeFactory();var monitorFactory=new FakeLiveMonitorFactory();
        await using var audio=TestSupport.CreateEngine(root,factory,liveMonitorFactory:monitorFactory);
        await audio.SelectDeviceAsync(factory.Device);
        var vm=new ShellViewModel(audio,Path.Combine(root,"initial-monitor.json"));await using var listener=vm.Listener;
        check(!vm.IsListening && vm.CurrentAudioSnapshot.Epoch==audio.Latest.Epoch,"Recording mode exposes Audio.Latest directly");
        vm.Session.Update(AudioRunState.Paused,TimeSpan.FromSeconds(12));
        vm.SetWorkMode(true);
        check(vm.IsListening && vm.CurrentAudioSnapshot.Epoch==vm.Listener.Latest.Epoch,"Listening mode preserves raw Epoch without bit-packing");
        check(vm.Session.Stage==RecordingStage.Paused && vm.Session.Elapsed==TimeSpan.FromSeconds(12),"Listening refresh leaves recording TransportSession untouched");
        vm.SetWorkMode(false);
        vm.Refresh();
        check(!vm.IsListening && vm.Session.Stage==RecordingStage.Ready,"Returning to recording mode resumes syncing TransportSession from Audio");
        check(!vm.RecordingMonitorEnabled && vm.RecordingMonitorDb==-30 && monitorFactory.OpenCount==0,"Recording monitor defaults to disabled at -30 dB without opening playback");

        await vm.SetRecordingMonitorEnabledAsync(true);
        check(vm.RecordingMonitorEnabled && audio.LiveMonitor.Enabled && monitorFactory.OpenCount==1,"SetRecordingMonitorEnabledAsync(true) activates backend output");
        vm.SetRecordingMonitorGain(72);
        check(vm.RecordingMonitorEnabled && vm.RecordingMonitorDb==60,"Recording monitor gain clamps upper bound to +60 dB");
        vm.SetRecordingMonitorGain(-6.54);
        check(vm.RecordingMonitorDb==-6.5,"Recording monitor gain rounds cleanly to 0.1 dB within -60..+60");
        factory.Push([0.5f,0.5f,0.5f,0.5f]);
        await TestSupport.Until(()=>monitorFactory.Current!.WriteCount>0,"monitor receives packet after gain change");
        check(Math.Abs(monitorFactory.Current!.LastLinearGain-Math.Pow(10,-6.5/20))<1e-6,"SetRecordingMonitorGain pushes linear gain to AudioEngine backend");
        vm.SetRecordingMonitorGain(-75);
        check(vm.RecordingMonitorDb==-60,"Recording monitor gain clamps lower bound to -60 dB");
        vm.SetRecordingMonitorGain(double.NaN);
        check(vm.RecordingMonitorDb==-60,"Non-finite recording monitor gain is ignored");
        await vm.SetRecordingMonitorEnabledAsync(false);
        check(!vm.RecordingMonitorEnabled && !audio.LiveMonitor.Enabled && monitorFactory.Current.Disposed,"SetRecordingMonitorEnabledAsync(false) closes backend output");

        await vm.SetRecordingMonitorEnabledAsync(true);
        await audio.SuspendMonitoringAsync();
        check(!vm.RecordingMonitorEnabled && !audio.LiveMonitor.Enabled,"RecordingMonitorEnabled reflects auto-off dynamically after SuspendMonitoringAsync");

        string monitorPrefPath=Path.Combine(root,"recording-monitor.json");
        var prefCapture=new FakeFactory();var prefMonitorFactory=new FakeLiveMonitorFactory();
        await using var prefAudio=TestSupport.CreateEngine(root,prefCapture,liveMonitorFactory:prefMonitorFactory);
        await prefAudio.SelectDeviceAsync(prefCapture.Device);
        var prefVm=new ShellViewModel(prefAudio,monitorPrefPath);await using var prefListener=prefVm.Listener;
        check(!prefVm.RecordingMonitorEnabled && prefVm.RecordingMonitorDb==-30 && prefMonitorFactory.OpenCount==0,"Missing monitor preference starts disabled at -30 dB");
        await prefVm.SetRecordingMonitorEnabledAsync(true);
        prefCapture.Push([0.5f,0.5f,0.5f,0.5f]);
        await TestSupport.Until(()=>prefMonitorFactory.Current!.WriteCount>0,"cold-start monitor receives initial packet");
        check(Math.Abs(prefMonitorFactory.Current!.LastLinearGain-Math.Pow(10,-30.0/20))<1e-6,"Cold start without preference file pushes -30 dB linear gain to AudioEngine when monitoring opens");
        prefVm.SetRecordingMonitorGain(-8.5);
        prefVm.SaveRecordingMonitorPreferences();
        check(MonitorPreferences.Load(monitorPrefPath)==-8.5,"Saved -8.5 dB preference loads back cleanly");

        var restartCapture=new FakeFactory();var restartMonitor=new FakeLiveMonitorFactory();
        await using var restartAudio=TestSupport.CreateEngine(root,restartCapture,liveMonitorFactory:restartMonitor);
        await restartAudio.SelectDeviceAsync(restartCapture.Device);
        var restartedVm=new ShellViewModel(restartAudio,monitorPrefPath);await using var restartedListener=restartedVm.Listener;
        check(!restartedVm.RecordingMonitorEnabled && restartedVm.RecordingMonitorDb==-8.5 && restartMonitor.OpenCount==0,
            "Restart resets monitor toggle to disabled while restoring saved -8.5 dB gain");
        await restartedVm.SetRecordingMonitorEnabledAsync(true);
        restartCapture.Push([0.5f,0.5f,0.5f,0.5f]);
        await TestSupport.Until(()=>restartMonitor.Current!.WriteCount>0,"restarted monitor receives initial packet");
        check(Math.Abs(restartMonitor.Current!.LastLinearGain-Math.Pow(10,-8.5/20))<1e-6,
            "Cold start pushes saved RecordingMonitorDb to backend before monitor is enabled");

        restartedVm.SetRecordingMonitorGain(0);
        restartedVm.SaveRecordingMonitorPreferences();
        check(MonitorPreferences.Load(monitorPrefPath)==0,"Explicitly saved 0 dB preference loads back as 0 dB instead of default");
        await using var zeroAudio=TestSupport.CreateEngine(root,new FakeFactory(),liveMonitorFactory:new FakeLiveMonitorFactory());
        var zeroVm=new ShellViewModel(zeroAudio,monitorPrefPath);await using var zeroListener=zeroVm.Listener;
        check(!zeroVm.RecordingMonitorEnabled && zeroVm.RecordingMonitorDb==0,"ShellViewModel reload preserves explicit 0 dB user preference");

        File.WriteAllText(monitorPrefPath,"{broken-json");
        check(MonitorPreferences.Load(monitorPrefPath)==-30,"Corrupt monitor preference safely resets to -30 dB");
        await using var corruptAudio=TestSupport.CreateEngine(root,new FakeFactory(),liveMonitorFactory:new FakeLiveMonitorFactory());
        var corruptVm=new ShellViewModel(corruptAudio,monitorPrefPath);await using var corruptListener=corruptVm.Listener;
        check(corruptVm.RecordingMonitorDb==-30,"ShellViewModel falls back to -30 dB on corrupt monitor preference");
    }

    private static void CoordinatorChecks(Action<bool,string> check)
    {
        var coord=new MainWindowCoordinator();
        check(!coord.IsStarted&&!coord.IsClosing,"Coordinator begins in Starting phase");
        check(!coord.TryBeginModeSwitch(WorkMode.Listening,false),"Cannot switch modes before window starts");
        check(coord.TryMarkStarted()&&coord.IsStarted,"TryMarkStarted enters Running once");
        check(!coord.TryMarkStarted(),"Repeated startup cannot re-enter Running");

        check(!coord.TryBeginCalibrationManager(recordingActive:false,dialogsBusy:true)&&coord.Overlay is OverlayState.None,"Busy DialogQueue rejects CalibrationManager without wedging Overlay");
        check(coord.TryBeginCalibrationManager(recordingActive:false)&&coord.CalibrationManagerOpen,"Calibration manager opens in idle recording mode");
        check(coord.TryEnterModal(ModalKind.CalibrationManagerDialog,false,out var prevCal)&&coord.Overlay is OverlayState.CalibrationManager{InnerDialogOpen:true},"Calibration dialog nests inside CalibrationManager");
        check(!coord.CanEnterModal(ModalKind.Message,false),"Background Message blocked while inner CalibrationManager dialog open");
        check(!coord.TryBeginModeSwitch(WorkMode.Listening,false),"Mode switch blocked while calibration modal open");
        check(coord.TryBeginAudioCommand(false),"Removing calibration inside CalibrationManager gets normal audio lease");
        coord.EndAudioCommand();
        coord.ExitModal(prevCal);
        check(coord.TryBeginFilePicker(recordingActive:false,out bool inMgr)&&inMgr,"File picker enters from idle CalibrationManager");
        check(!coord.CanEnterModal(ModalKind.Message,false),"Message blocked until FileOpenPicker finishes");
        check(!coord.TryBeginModeSwitch(WorkMode.Listening,false),"Mode switch blocked while FileOpenPicker active");
        check(!coord.CanOpenRawDecision()&&!coord.CanEnqueueRecordingNotice(),"Raw and recording notice modals blocked while FileOpenPicker active");
        coord.EndFilePicker(inMgr);
        check(coord.TryEnterModal(ModalKind.Message,false,out var errPrev),"Import error Message permitted after EndFilePicker while manager open");
        coord.ExitModal(errPrev);
        check(coord.Overlay is OverlayState.CalibrationManager{InnerDialogOpen:false,InFilePicker:false},"Exiting import error Message restores idle CalibrationManager overlay");
        coord.EndCalibrationManager();
        check(coord.Overlay is OverlayState.None,"Ending calibration manager clears Overlay");

        check(coord.TryBeginFolderPicker(recordingActive:false)&&coord.Overlay is OverlayState.FolderPicker,"FolderPicker enters cleanly in Recording");
        check(!coord.TryBeginModeSwitch(WorkMode.Listening,false),"Mode switch blocked while FolderPicker open");
        coord.EndFolderPicker();

        check(coord.CanEnqueueRecordingNotice(),"RecordingNotice can enqueue in idle Recording mode");
        check(coord.TryEnterModal(ModalKind.RecordingNotice,false,out var prevMonCal),"RecordingNotice enters modal in idle Recording mode");
        check(!coord.CanEnqueueRecordingNotice()&&!coord.CanEnterModal(ModalKind.RecordingNotice,false),"RecordingNotice blocked while another modal is open");
        coord.ExitModal(prevMonCal);
        check(coord.Overlay is OverlayState.None,"Exiting RecordingNotice clears Overlay");

        check(coord.TryBeginModeSwitch(WorkMode.Listening,false),"Switching to Listening permitted when idle");
        check(!coord.CanEnableWorkModeSelector(active:false),"Work mode selector disabled while switching modes");
        check(!coord.TryEnterModal(ModalKind.RawDecision,false,out _),"RawDecision declined cleanly once mode switch begins");
        check(!coord.TryBeginAudioCommand(modeInitialization:false),"Normal audio command rejected during pre-commit mode switch");
        check(!coord.CanOpenRawDecision()&&!coord.CanEnqueueRecordingNotice(),"RAW and recording notices blocked while switching to Listening");
        coord.CommitModeSwitch(WorkMode.Listening);
        check(!coord.InRecordingInputContext,"Committed Listening mode leaves recording input context");
        check(coord.CanRefreshListeningSources(modeInitialization:true,listeningActive:false,refreshInFlight:false),"Listening init refresh permitted after commit");
        check(!coord.CanRefreshListeningSources(modeInitialization:false,listeningActive:false,refreshInFlight:false),"User listening refresh blocked while switching still finishes");
        coord.EndModeSwitch();
        check(coord.CanEnableWorkModeSelector(active:false)&&!coord.CanEnableWorkModeSelector(active:true),"Work mode selector disables while listening or recording is active and re-enables once stopped");
        check(!coord.TryEnterModal(ModalKind.RecordingNotice,false,out _),"Queued RecordingNotice rejected after mode switched to Listening");
        check(!coord.TryBeginCalibrationManager(false)&&!coord.TryBeginFolderPicker(false),"Recording overlays rejected in Listening mode");
        check(coord.CanRefreshListeningSources(modeInitialization:false,listeningActive:false,refreshInFlight:false),"Manual listening refresh permitted once idle in Listening");

        check(coord.TryBeginModeSwitch(WorkMode.Recording,false),"Switching back to Recording permitted");
        coord.CommitModeSwitch(WorkMode.Recording);
        check(coord.InRecordingInputContext,"Committed Recording re-enters recording input context");
        check(coord.TryBeginAudioCommand(modeInitialization:true),"Mode initialization audio lease allowed after commit");
        coord.EndAudioCommand();
        coord.EndModeSwitch();
        check(coord.CanAlignWindowsGain(RecordingMode.Exclusive)&&!coord.CanAlignWindowsGain(RecordingMode.Shared),"Windows gain alignment restricted to idle Exclusive Recording");

        check(coord.TryBeginCleanup()&&coord.IsClosing,"Cleanup transitions cleanly to CleaningUp");
        check(!coord.TryEnterModal(ModalKind.RawDecision,duringClose:true,out _),"Only cleanup Message modal allowed during close");
        check(coord.TryEnterModal(ModalKind.Message,duringClose:true,out var prevClose),"Cleanup error Message permitted during CleaningUp");
        coord.ExitModal(prevClose);
        coord.MarkClosed();
        check(!coord.TryEnterModal(ModalKind.Message,duringClose:true,out _),"No dialogs permitted after Closed");
    }
}
