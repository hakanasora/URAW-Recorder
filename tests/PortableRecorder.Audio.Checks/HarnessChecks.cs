using System.Diagnostics;

static class HarnessChecks
{
    public static async Task Run(string root,Action<bool,string> check)
    {
        string executable=Environment.ProcessPath??throw new InvalidOperationException("No current test executable.");
        var start=new ProcessStartInfo(executable);
        if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(HarnessChecks).Assembly.Location);
        start.ArgumentList.Add("--stall-output");
        int childId=0;
        bool timedOut=false;
        var elapsed=Stopwatch.StartNew();
        try{await ProcessCapture.RunAsync(start,TimeSpan.FromMilliseconds(500),id=>childId=id);}
        catch(TimeoutException){timedOut=true;}
        check(timedOut,"Deadline also covers a child holding stdout open");
        check(elapsed.Elapsed<TimeSpan.FromSeconds(5),"Hung output test fails promptly instead of hanging the runner");
        bool exited;
        try{using var child=Process.GetProcessById(childId);exited=child.HasExited;}
        catch(ArgumentException){exited=true;}
        check(childId>0&&exited,"Timed-out helper PID is terminated and reaped");
    }
}
