using PortableRecorder.Audio;
using PortableRecorder.Desktop;

static class UiStateChecks
{
    public static async Task Run(string root,Action<bool,string> check)
    {
        string calibration=NoticePresentation.Details("校准尚未生效，请选择有效文件或禁用校准。","共享监看 · RAW 请求已接受；校准尚未生效：请选择校准文件，或禁用校准后录制。","校准尚未生效，请选择有效文件。\n校准尚未生效：请选择校准文件，或禁用校准后录制。\n未应用校准；保留原始频响。未启用绝对声压。");
        check(calibration.Split("校准尚未生效").Length==2,"Equivalent calibration notices appear once");
        check(calibration.Contains("未启用绝对声压"),"Distinct calibration limitations survive deduplication");
        check(NoticePresentation.Details("原因甲","原因乙","原因甲")=="原因甲\n原因乙","Distinct causes preserved and exact duplicates removed");
        check(NoticePresentation.Details(@"C:\录音；备份\test.wav")==@"C:\录音；备份\test.wav","Paths are preserved verbatim");
        check(NoticePresentation.Title("校准尚未生效，请选择有效文件或禁用校准。")=="校准尚未生效，请选择有效文件或禁用校准","Dialog title describes its notice");
        check(NoticePresentation.Title("录音继续中：压缩文件出现削波。降低增益或使用浮点 WAV 可避免超限损失。")=="录音出现削波","Clipping gets concise specific title");
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
        check(TransportPresentation.From(AudioRunState.Paused,false,true).StatusText=="暂停中"&&
            TransportPresentation.From(AudioRunState.Finalizing,false,true).StatusText=="正在保存","Native status labels retained");

        // Calibration keeps Preparing visible while delayed samples reach the writer. Those
        // status changes and command completions must not restart the same transport motion.
        var session=new TransportSession();RecordingStage? previous=null;int motions=0;
        foreach(var state in new[]{AudioRunState.Monitoring,AudioRunState.Preparing,
            AudioRunState.Preparing,AudioRunState.Recording,AudioRunState.Recording,AudioRunState.Finalizing})
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

        await CalibrationCollectionChecks(root,check);
    }

    private static async Task CalibrationCollectionChecks(string root,Action<bool,string> check)
    {
        string directory=Path.Combine(root,"ui-calibration-collection");Directory.CreateDirectory(directory);
        var library=new CalibrationLibrary(Path.Combine(directory,"library"));
        var factory=new FakeFactory();
        await using var engine=new AudioEngine(factory,library,inputFormats:new(Path.Combine(directory,"formats.json")));
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
}
