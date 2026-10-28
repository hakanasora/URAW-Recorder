using System.Diagnostics;
using PortableRecorder.Audio;

// Opt-in deterministic CPU/allocation probe. No device capture, playback or file recording.
const int size=8192,rate=48000,iterations=1000;
var channels=Enumerable.Range(0,2).Select(c=>Enumerable.Range(0,size)
    .Select(i=>(float)(.2*Math.Sin(2*Math.PI*(440+c*110)*i/rate))).ToArray()).ToArray();
var estimator=new SpectrumEstimator(size,rate);
for(int i=0;i<1000;i++)estimator.Analyze(channels,1d/60);
for(int run=0;run<5;run++)
{
    long allocated=GC.GetAllocatedBytesForCurrentThread();
    var timer=Stopwatch.StartNew();float[]? result=null;
    for(int i=0;i<iterations;i++)result=estimator.Analyze(channels,1d/60);
    timer.Stop();
    Console.WriteLine($"run={run+1}; iterations={iterations}; ms={timer.Elapsed.TotalMilliseconds:F2}; bytes={GC.GetAllocatedBytesForCurrentThread()-allocated}; peak={result!.Max():F5}");
}
