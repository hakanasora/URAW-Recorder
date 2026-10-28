using System.Diagnostics;
using System.Text;

internal sealed record ProcessOutput(int ExitCode,byte[] Bytes,string Error);

/// <summary>Deadline covers pipe reads and exit, and always reaps the process we started.</summary>
internal static class ProcessCapture
{
    public static async Task<ProcessOutput> RunAsync(ProcessStartInfo start,TimeSpan timeout,Action<int>? started=null)
    {
        start.UseShellExecute=false;
        start.CreateNoWindow=true;
        start.RedirectStandardOutput=true;
        start.RedirectStandardError=true;
        using var process=Process.Start(start)??throw new IOException("Test process did not start.");
        using var deadline=new CancellationTokenSource(timeout);
        started?.Invoke(process.Id);
        Task<byte[]> stdout=ReadBounded(process.StandardOutput.BaseStream,64*1024*1024,deadline.Token);
        Task<byte[]> stderr=ReadBounded(process.StandardError.BaseStream,64*1024,deadline.Token);
        Task exit=process.WaitForExitAsync(deadline.Token);
        try
        {
            var pending=new List<Task>{stdout,stderr,exit};
            while(pending.Count>0)
            {
                var completed=await Task.WhenAny(pending).WaitAsync(deadline.Token);
                await completed;
                pending.Remove(completed);
            }
            return new(process.ExitCode,await stdout,Encoding.UTF8.GetString(await stderr));
        }
        catch(OperationCanceledException error)when(deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Test process {process.Id} exceeded {timeout.TotalSeconds:0.###} seconds, including redirected output.",error);
        }
        finally
        {
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            await process.WaitForExitAsync();
            // Termination closes both pipes. Observe read/cancellation failures after cleanup.
            try{await Task.WhenAll(stdout,stderr);}catch(Exception error)when(error is IOException or OperationCanceledException or InvalidDataException){}
        }
    }

    private static async Task<byte[]> ReadBounded(Stream stream,int limit,CancellationToken token)
    {
        using var output=new MemoryStream();
        var buffer=new byte[16384];
        int count;
        while((count=await stream.ReadAsync(buffer,token))>0)
        {
            if(output.Length+count>limit)throw new InvalidDataException("Test process output exceeded its memory budget.");
            output.Write(buffer,0,count);
        }
        return output.ToArray();
    }
}
