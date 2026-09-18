using OpsDeck.Core;

namespace OpsDeck.Tests;

public static class M615ResponsivenessTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m615-perf-"+n,ok);
        C("baud-460800",PanelSerialPacing.Baud==460800);
        C("short-gap-min",PanelSerialPacing.GapMs("x")==PanelSerialPacing.MinGapMs);
        int medium=PanelSerialPacing.GapMs(new string('x',500));
        C("medium-gap-wire-aware",medium is >=12 and <=20);
        int large=PanelSerialPacing.GapMs(new string('x',3000));
        C("large-gap-wire-aware",large is >=60 and <=80);
        C("gap-capped",PanelSerialPacing.GapMs(new string('x',20000))==PanelSerialPacing.MaxGapMs);
        C("loop-fast",PanelSerialPacing.LoopDelayMs<=5);
        C("request-immediate",PanelSerialPacing.RequestResponseFloorMs==0);
        bool threw=false;try{PanelSerialPacing.GapMs("x",0);}catch(ArgumentOutOfRangeException){threw=true;}
        C("bad-baud-rejected",threw);
    }
}
