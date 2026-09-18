using OpsDeck.Core;

namespace OpsDeck.Tests;

public static class M615ResponsivenessTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m615-perf-"+n,ok);
        C("short-gap-min",PanelSerialPacing.GapMs("x")==PanelSerialPacing.MinGapMs);
        int medium=PanelSerialPacing.GapMs(new string('x',500));
        C("medium-gap-wire-aware",medium is >=40 and <=60);
        int large=PanelSerialPacing.GapMs(new string('x',3000));
        C("large-gap-wire-aware",large is >=250 and <=280);
        C("gap-capped",PanelSerialPacing.GapMs(new string('x',10000))==PanelSerialPacing.MaxGapMs);
        C("loop-fast",PanelSerialPacing.LoopDelayMs<=5);
        C("request-immediate",PanelSerialPacing.RequestResponseFloorMs==0);
        bool threw=false;try{PanelSerialPacing.GapMs("x",0);}catch(ArgumentOutOfRangeException){threw=true;}
        C("bad-baud-rejected",threw);
    }
}
