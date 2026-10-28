using System.Diagnostics;
using PortableRecorder.Audio;

static class TestSupport
{
    public static AudioEngine CreateEngine(string root,FakeFactory factory,Func<string,AudioFormat,RecordingEncoding,IRecordingWriter>? writerFactory=null)
    {
        string directory=Path.Combine(root,"engine-"+Guid.NewGuid().ToString("N"));
        return new AudioEngine(factory,new CalibrationLibrary(Path.Combine(directory,"calibrations")),writerFactory,new InputFormatPreferences(Path.Combine(directory,"formats.json")));
    }
    public static float[] Sine(int rate,int frames,double frequency,double peak,int channels=1,bool inverted=false)
    {
        var samples=new float[frames*channels];
        for(int i=0;i<frames;i++)
            for(int c=0;c<channels;c++)
                samples[i*channels+c]=(float)(peak*Math.Sin(2*Math.PI*frequency*i/rate)*(inverted && c%2==1?-1:1));
        return samples;
    }
    public static async Task Until(Func<bool> condition,string name)
    {
        var elapsed=Stopwatch.StartNew();
        while(!condition())
        {
            if(elapsed.Elapsed.TotalSeconds>5)throw new TimeoutException("Timeout: "+name);
            await Task.Delay(10);
        }
    }
}
