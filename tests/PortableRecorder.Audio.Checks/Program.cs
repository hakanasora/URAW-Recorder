if(args.Contains("--stall-output"))
{
    Console.WriteLine("controlled stdout is open");
    await Console.Out.FlushAsync();
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}
var groups=new Dictionary<string,Func<string,Action<bool,string>,Task>>(StringComparer.OrdinalIgnoreCase)
{
    ["dsp"]=MeasurementChecks.Run,
    ["capture"]=LifecycleChecks.Run,
    ["display"]=DisplayChecks.Run,
    ["continuity"]=ContinuityChecks.Run,
    ["preferences"]=PreferencesChecks.Run,
    ["calibration"]=CalibrationChecks.Run,
    ["calibration-boundaries"]=CalibrationRobustnessChecks.Run,
    ["settings"]=InputSettingsChecks.Run,
    ["encoding"]=EncodingChecks.Run,
    ["harness"]=HarnessChecks.Run,
    ["regression-audio"]=AudioReliabilityChecks.Run,
    ["regression-storage"]=StorageReliabilityChecks.Run,
    ["regression-ui"]=UiStateChecks.Run,
};
if(args.Contains("--list")){Console.WriteLine(string.Join(Environment.NewLine,groups.Keys));return;}
var selected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
for(int i=0;i<args.Length;i++)
{
    if(args[i]!="--group"||i+1>=args.Length)throw new ArgumentException("Usage: --group dsp,capture,... | --list");
    foreach(var name in args[++i].Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
    {
        if(!groups.ContainsKey(name))throw new ArgumentException("Unknown check group: "+name);
        selected.Add(name);
    }
}
if(selected.Count==0&&args.Length>0)throw new ArgumentException("Select at least one non-empty check group.");
if(selected.Count==0)selected.UnionWith(groups.Keys);
string root=Path.GetFullPath(Path.Combine("artifacts","audio-checks",DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")));
Directory.CreateDirectory(root);
int assertions=0,passed=0,failed=0;
foreach(var(name,run)in groups)
{
    if(!selected.Contains(name))continue;
    string directory=Path.Combine(root,name);Directory.CreateDirectory(directory);
    int groupAssertions=0;
    var elapsed=System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await run(directory,(condition,message)=>
        {
            if(!condition)throw new Exception(message);
            groupAssertions++;assertions++;
        });
        passed++;Console.WriteLine($"PASS [{name}]: {groupAssertions} assertions; {elapsed.Elapsed.TotalSeconds:F2}s");
    }
    catch(Exception error)
    {
        failed++;Console.Error.WriteLine($"FAIL [{name}] after {groupAssertions} assertions: {error}");
    }
}
Console.WriteLine($"Groups: {passed} passed / {failed} failed; {assertions} assertions (not distinct scenarios or coverage). No microphone, desktop, or playback. Fixtures: {root}");
Environment.ExitCode=failed==0?0:1;
