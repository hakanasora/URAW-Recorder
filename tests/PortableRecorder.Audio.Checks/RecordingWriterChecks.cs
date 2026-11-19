using System.Runtime.InteropServices;
using NAudio.Wave;
using PortableRecorder.Audio;

internal static class RecordingWriterChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        var format=new AudioFormat(48000,1,32,32,PcmEncoding.Float);
        var timeout=TimeSpan.FromSeconds(5);
        foreach(string scenario in new[]{"graceful","byte budget","block limit"})
        {
            var sink=new BlockedWaveSink();
            var writer=new FloatWaveWriter(scenario,format,null,sink);
            sink.BlockNextWrite();
            var accepted=new List<float>{.125f};
            try
            {
                check(writer.TryWrite([.125f]),scenario+": first block accepted");
                await sink.Entered.Task.WaitAsync(timeout);
                if(scenario=="byte budget")
                {
                    var tail=Enumerable.Repeat(.25f,format.SampleRate*2-1).ToArray();
                    check(writer.TryWrite(tail),"Exactly the byte budget fits while the first sink write is blocked");
                    accepted.AddRange(tail);
                }
                else
                {
                    int blocks=scenario=="block limit"?512:3;
                    for(int i=0;i<blocks;i++)
                    {
                        float sample=i/1024f;
                        if(!writer.TryWrite([sample]))throw new Exception($"{scenario}: accepted prefix ended at block {i}");
                        accepted.Add(sample);
                    }
                }
                bool saturated=scenario!="graceful";
                if(saturated)check(!writer.TryWrite([.9f]) && writer.Failure is IOException,scenario+": saturation latches a failure");
                var originalFailure=writer.Failure;
                var disposeCall=Task.Factory.StartNew(()=>writer.DisposeAsync().AsTask(),CancellationToken.None,TaskCreationOptions.None,TaskScheduler.Default);
                var complete=writer.CompleteAsync();
                var dispose=await disposeCall.WaitAsync(timeout);
                check(ReferenceEquals(complete,dispose) && !complete.IsCompleted,scenario+": concurrent completion and disposal share the still-draining result");
                check(!writer.TryWrite([.8f]) && ReferenceEquals(writer.Failure,originalFailure),
                    scenario+": late write is rejected without changing the outcome");
                if(scenario=="block limit")sink.FailDisposal();
                sink.Release.TrySetResult();

                var completionError=await Observe(complete);
                var disposalError=await Observe(dispose);
                check(ReferenceEquals(completionError,disposalError) && (completionError!=null)==saturated,
                    scenario+": both callers observe the same terminal outcome");
                if(scenario=="block limit")
                    check(writer.Failure is AggregateException aggregate
                        && ReferenceEquals(aggregate.InnerExceptions[0],originalFailure)
                        && ReferenceEquals(aggregate.InnerExceptions[1],sink.CleanupError),
                        "Sink cleanup remains secondary to the original saturation");
                var finalFailure=writer.Failure;
                check(ReferenceEquals(complete,writer.CompleteAsync()) && !writer.TryWrite([.7f]) && ReferenceEquals(finalFailure,writer.Failure),
                    scenario+": terminal writers reject without changing the result");
                using var wave=new WaveFileReader(new MemoryStream(sink.ToArray()));
                var data=new byte[accepted.Count*sizeof(float)];
                wave.ReadExactly(data);
                check(wave.Length==data.Length && writer.WrittenFrames==accepted.Count
                    && MemoryMarshal.Cast<byte,float>(data).SequenceEqual(CollectionsMarshal.AsSpan(accepted)),
                    scenario+": WAV contains exactly the contiguous accepted prefix");
            }
            finally
            {
                sink.Release.TrySetResult();
                _=await Observe(writer.CompleteAsync());
            }
        }
        await SinkFailure();

        async Task SinkFailure()
        {
            var sink=new BlockedWaveSink();
            var writer=new FloatWaveWriter("sink failure",format,null,sink);
            sink.BlockNextWrite();
            try
            {
                check(writer.TryWrite([.125f]),"Sink fault fixture admits the first block");
                await sink.Entered.Task.WaitAsync(timeout);
                check(writer.TryWrite([.25f]),"Sink fault fixture has a queued block behind the failed write");
                sink.FailWrite();
                sink.Release.TrySetResult();
                await TestSupport.Until(()=>writer.Failure!=null,"WAV sink failure published before explicit completion");
                check(ReferenceEquals(writer.Failure,sink.WriteError) && !writer.TryWrite([.9f]),"Sink error closes admission before completion");
                check(ReferenceEquals((await Observe(writer.CompleteAsync()))?.InnerException,sink.WriteError),
                    "WAV completion keeps the original sink exception");
                using var wave=new WaveFileReader(new MemoryStream(sink.ToArray()));
                check(writer.WrittenFrames==0 && wave.Length==0,"No queued samples are written after an earlier sink write failed");
            }
            finally
            {
                sink.Release.TrySetResult();
                _=await Observe(writer.CompleteAsync());
            }
        }

        async Task<Exception?> Observe(Task task)
        {
            try{await task.WaitAsync(timeout);return null;}
            catch(IOException error){return error;}
        }
    }

    private sealed class BlockedWaveSink:MemoryStream
    {
        private int _blockWrite,_failDisposal,_failWrite;
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IOException CleanupError {get;}=new("injected WAV stream cleanup failure");
        public IOException WriteError {get;}=new("injected WAV stream write failure");
        public void BlockNextWrite()=>Volatile.Write(ref _blockWrite,1);
        public void FailDisposal()=>Volatile.Write(ref _failDisposal,1);
        public void FailWrite()=>Volatile.Write(ref _failWrite,1);

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WaitForRelease();
            base.Write(buffer);
        }

        public override void Write(byte[] buffer,int offset,int count)
        {
            WaitForRelease();
            base.Write(buffer,offset,count);
        }

        private void WaitForRelease()
        {
            if(Interlocked.Exchange(ref _blockWrite,0)==0)return;
            Entered.TrySetResult();
            Release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if(Interlocked.Exchange(ref _failWrite,0)==1)throw WriteError;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if(disposing&&Interlocked.Exchange(ref _failDisposal,0)==1)throw CleanupError;
        }
    }
}
