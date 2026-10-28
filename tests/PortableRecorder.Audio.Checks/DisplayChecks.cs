using PortableRecorder.Desktop;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using PortableRecorder.Audio;

using static TestSupport;

static class DisplayChecks
{
    public static Task Run(string root,Action<bool,string> Assert)
    {
        void Near(double actual,double expected,double tolerance,string name)=>Assert(Math.Abs(actual-expected)<=tolerance,$"{name}: {actual} expected {expected} ± {tolerance}");
        {
            var motion=new MeterMotion();var low=AudioSnapshot.Empty(10) with{Valid=true,SampleRate=48000,FramePosition=1440,VuLeft=-20,VuRight=-10};
            motion.Push(low,0);var high=low with{FramePosition=2880,VuLeft=0,VuRight=3};motion.Push(high,.030);
            Near(motion.Read(.030,false).Left,-20,.001,"Visual interpolation begins at previous sample");
            double last=-20;
            for(int i=0;i<=4;i++)
            {
                double value=motion.Read(.030+i/120d,false).Left;
                Assert(value>=last && value<=0,"120 Hz interpolation is monotonic, no overshoot");last=value;
            }
            Near(motion.Read(.060,false).Left,0,.001,"Visual interpolation reaches measured target");
            Near(motion.Read(.035,true).Left,0,0,"Reduced motion uses measured target");
            motion.Push(high with{Epoch=11,VuLeft=-8},.070);Near(motion.Read(.070,false).Left,-8,.001,"Epoch change never blends old device");
            motion.Push(AudioSnapshot.Empty(11),.080);Assert(double.IsNegativeInfinity(motion.Read(.080,false).Left),"Failure clears interpolated pointer");
        }
        {
            var motion=new CentsMotion();motion.Advance(1,69,0,0,false);
            double previous=0;
            for(int i=0;i<12;i++)
            {
                double position=motion.Advance(1,69.3,30,1d/240,false)!.Value;
                Assert(position>previous && position<30,"Cents ruler advances each 240 Hz frame without overshoot");previous=position;
            }
            Near(motion.Advance(1,70.49,49,.004,false)!.Value,49,0,"New note resets ruler");
            Near(motion.Advance(1,70.51,-49,.004,false)!.Value,-49,0,"No sweeping across note boundary");
            Assert(motion.Advance(1,null,null,.004,false)==null,"Invalid pitch clears ruler immediately");
            Near(motion.Advance(2,60.2,20,.004,false)!.Value,20,0,"New session starts directly");
            Near(motion.Advance(2,60.3,30,.004,true)!.Value,30,0,"Reduced motion bypasses interpolation");
            var slower=new CentsMotion();var faster=new CentsMotion();slower.Advance(1,69,0,0,false);faster.Advance(1,69,0,0,false);
            double a=0,b=0;for(int i=0;i<6;i++)a=slower.Advance(1,69.2,20,1d/60,false)!.Value;
            for(int i=0;i<24;i++)b=faster.Advance(1,69.2,20,1d/240,false)!.Value;
            Near(a,b,1e-10,"Cents response independent of refresh rate");
            Assert(DisplayRefreshRate.NativeLayoutsValid,"DisplayConfig native layouts");
            var display=DisplayRefreshRate.Read(IntPtr.Zero);
            Console.WriteLine($"Read-only display probe: {display.Device}, {display.Hertz?.ToString("F3")??"unknown"} Hz ({display.Source})");
            Assert(display.Hertz==null || double.IsFinite(display.Hertz.Value) && display.Hertz>0,"System refresh probe valid or explicit compositor fallback");
        }
        return Task.CompletedTask;
    }
}
