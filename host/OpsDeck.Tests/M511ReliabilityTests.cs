using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M511ReliabilityTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m511-"+n,ok);
        C("window-labels",ReliabilityWindows.Label(ReliabilityWindow.H1)=="1h"&&ReliabilityWindows.Label(ReliabilityWindow.H8)=="8h"&&ReliabilityWindows.Label(ReliabilityWindow.H24)=="24h"&&ReliabilityWindows.Label(ReliabilityWindow.D7)=="7d");
        var to=DateTimeOffset.Parse("2026-09-15T20:00:00Z");var from=to.AddHours(-8);
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m511-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);string path=Path.Combine(dir,"telemetry-history.db");
        try
        {
            using(var store=new LifecycleHistoryStore(path))
            {
                store.Add(Sample(from.AddMinutes(-1),90,120,16,900,90,5,0,0,0,0));
                store.Add(Sample(from,100,120,16,1000,100,10,0,0,0,0));
                store.Add(Sample(from.AddMinutes(1),110,121,16,1060,160,20,0,0,0,0));
                store.Add(Sample(from.AddHours(4),130,125,17,20,10,3,1,1,0,0));
                store.Add(Sample(to.AddMinutes(-1),160,120,18,14400,14400,100,2,1,1,1));
                store.Add(Sample(to,999,999,99,15000,15000,200,9,9,9,3));
                var r=store.ReadReliability(ReliabilityWindow.H8,to);
                C("half-open-boundaries",r.ObservedMinutes==4&&r.FirstObservedAt==from&&r.LastObservedAt==to.AddMinutes(-1));
                C("expected-window",r.ExpectedMinutes==480&&Math.Abs(r.CoveragePercent-(4d/480*100))<0.0001);
                C("max-gap",r.MaxGapMinutes==238);
                C("memory-boundaries",r.HostPrivateMiB.First==100&&r.HostPrivateMiB.Latest==160&&r.HostPrivateMiB.Delta==60&&r.HostPrivateMiB.Min==100&&r.HostPrivateMiB.Max==160&&r.HostPrivateMiB.Points==4);
                C("zero-delta-preserved",r.Handles.First==120&&r.Handles.Latest==120&&r.Handles.Delta==0);
                C("restart-evidence",r.HostRestarts==1&&r.PanelReboots==1);
                C("packet-progress",r.PanelPacketsAdvanced==107);
                C("recovery-deltas",r.LinkReopens==2&&r.LinkRomProbes==1&&r.LinkRecoveries==1);
                C("window-rates",Math.Abs(r.RecoveryActionsPerWindowHour-.375)<0.0001&&Math.Abs(r.RecoveriesPerWindowHour-.125)<0.0001);
                C("latest-state",r.LatestLinkState==SourceState.Ok&&r.PanelSdState==1);
                C("panel-metrics",r.PanelInternalKiB.Points==4&&r.PanelPsramKiB.Points==4);
            }
            using(var store=new LifecycleHistoryStore(path))C("persistent",store.ReadReliability(ReliabilityWindow.H8,to).ObservedMinutes==4);
            string logDir=Path.Combine(dir,"log-lock");Directory.CreateDirectory(logDir);string liveLog=Path.Combine(logDir,"host.log");
            File.WriteAllText(liveLog,new string('x',1024*1024+32));for(int i=1;i<=4;i++)File.WriteAllText(Path.Combine(logDir,$"host.{i}.log"),$"archive-{i}");
            using(var locked=new FileStream(Path.Combine(logDir,"host.4.log"),FileMode.Open,FileAccess.Read,FileShare.None))
            {
                new SafeLog(logDir).Event("rotation_lock_probe",new{ok=true});
                C("log-rotation-lock-does-not-silence",File.ReadAllText(liveLog).Contains("rotation_lock_probe",StringComparison.Ordinal));
            }
            string engineDir=Path.Combine(dir,"engine");var engine=new AppEngine(new HostConfig(),new LocalSettings(engineDir));engine.Start(false,false);
            Thread.Sleep(120);var empty=engine.ReadReliabilityReport(ReliabilityWindow.H8);
            C("engine-window-aligned",empty.ExpectedMinutes==480&&empty.To.Second==0&&empty.To.Millisecond==0);
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
    private static LifecycleSample Sample(DateTimeOffset at,int privateMiB,int handles,int threads,long hostUptime,
        long panelUptime,long packets,int reopen,int rom,int recovered,int sd)
    {
        var panel=new PanelHealthSnapshot(true,at,panelUptime,170*1024,159*1024,6800*1024,packets,sd);
        var link=new LinkHealthSnapshot(SourceState.Ok,false,reopen,rom,recovered,SerialRecoveryAction.None,SerialRecoveryReason.None,at.AddHours(-1),at,null);
        return new(at,(long)privateMiB*1024*1024,110L*1024*1024,handles,threads,hostUptime,panel,link);
    }
}
