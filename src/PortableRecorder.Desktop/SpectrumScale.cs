namespace PortableRecorder.Desktop;

/// <summary>Display coordinates only: logarithmic Hz and an explicitly nonuniform dB axis.</summary>
internal static class SpectrumScale
{
    public static readonly double[] Frequencies=[20,100,500,1000,3000,10000,20000];
    public static readonly double[] Levels=[0,-6,-12,-24,-48,-96];
    public static double FrequencyPosition(double hz)=>Math.Log10(Math.Clamp(hz,20,20000)/20)/3;

    public static double LevelPosition(double db)
    {
        if(double.IsNaN(db) || db<=Levels[^1])return 1;
        if(db>=Levels[0])return 0;
        for(var i=0;i<Levels.Length-1;i++)
            if(db>=Levels[i+1])return (i+(Levels[i]-db)/(Levels[i]-Levels[i+1]))/(Levels.Length-1);
        return 1;
    }
}
