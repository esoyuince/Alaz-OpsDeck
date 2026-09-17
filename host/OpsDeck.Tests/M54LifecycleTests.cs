using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M54LifecycleTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m54-"+n,ok);var now=DateTimeOffset.UtcNow;
        string old="HEALTH uptime_s=123 internal_free=169000 internal_min=160000 psram_free=6800000 packets=42";
        C("parse-legacy",PanelHealthSnapshot.TryParse(old,now,out var legacy)&&legacy.Present&&legacy.SdState==-1&&legacy.Packets==42);
        string modern="HEALTH uptime_s=456 internal_free=170000 internal_min=159000 psram_free=6810000 sd=1 packets=99";
        C("parse-m53",PanelHealthSnapshot.TryParse(modern,now,out var m53)&&m53.SdState==1&&m53.UptimeS==456&&m53.InternalMin==159000);
        C("reject-sd",!PanelHealthSnapshot.TryParse(modern.Replace("sd=1","sd=4"),now,out _));
        C("reject-extra",!PanelHealthSnapshot.TryParse(modern+" extra=1",now,out _));
        C("reject-control",!PanelHealthSnapshot.TryParse(modern+"\n",now,out _));
        C("reboot-detected",PanelHealthSnapshot.IsReboot(new(true,now,100,1,1,1,10),new(true,now,5,1,1,1,1)));
        C("small-rollback-not-reboot",!PanelHealthSnapshot.IsReboot(new(true,now,100,1,1,1,10),new(true,now,96,1,1,1,11)));
        var captured=LifecycleSample.Capture(PanelHealthSnapshot.Empty,Link(now,0,0,0));
        C("process-capture",captured.HostPrivateBytes>0&&captured.HostWorkingSetBytes>0&&captured.HostHandles>=0&&captured.HostThreads>0);
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m54-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);string path=Path.Combine(dir,"telemetry-history.db");
        try
        {
            using(var store=new LifecycleHistoryStore(path))
            {
                store.Add(Sample(now.AddDays(-8),500,80,80,0,0,0,0));
                store.Add(Sample(now.AddMinutes(-5),1000,100,100,0,0,0,0));
                store.Add(Sample(now.AddMinutes(-4),1060,160,110,1,0,1,0));
                store.Add(Sample(now.AddMinutes(-3),40,null,null,0,0,0,-1));
                store.Add(Sample(now.AddMinutes(-2),100,10,5,0,0,0,0));
                store.Add(Sample(now.AddMinutes(-1),160,70,25,1,1,1,1));
                C("retention",store.Count()==5);
                var t=store.ReadSummary(TelemetryWindow.H1,now);
                C("minute-rows",t.MinuteRows==5);
                C("host-restart",t.HostRestarts==1);
                C("panel-reboot-across-gap",t.PanelReboots==1);
                C("packet-progress",t.PanelPacketsAdvanced==30);
                C("recovery-deltas",t.LinkReopens==2&&t.LinkRomProbes==1&&t.LinkRecoveries==2);
                C("latest-panel",t.PanelLatestUptimeS==70&&t.PanelSdState==1&&t.PanelInternalKiB.Points==4);
                C("memory-range",t.HostPrivateMiB.Points==5&&t.HostPrivateMiB.Latest>100);
            }
            using(var store=new LifecycleHistoryStore(path))C("reopen-persistent",store.Count()==5);
            var settings=new LocalSettings(Path.Combine(dir,"engine"));var engine=new AppEngine(new HostConfig(),settings);engine.Start(false,false);
            var ready=System.Diagnostics.Stopwatch.StartNew();while(ready.Elapsed<TimeSpan.FromSeconds(2)&&(!engine.LifecycleHistoryAvailable||engine.ReadLifecycleTrend(TelemetryWindow.M15).MinuteRows<1))Thread.Sleep(25);C("engine-store",engine.LifecycleHistoryAvailable&&engine.ReadLifecycleTrend(TelemetryWindow.M15).MinuteRows>=1);
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
            C("device-domain",Enum.IsDefined(OperationalDomain.Device));
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
    private static LinkHealthSnapshot Link(DateTimeOffset now,int reopen,int rom,int recovered,SourceState state=SourceState.Ok)
        =>new(state,false,reopen,rom,recovered,SerialRecoveryAction.None,SerialRecoveryReason.None,now.AddHours(-1),now,null);
    private static LifecycleSample Sample(DateTimeOffset at,long hostUptime,long? panelUptime,long? packets,int reopen,int rom,int recovered,int sd)
    {
        var panel=panelUptime.HasValue?new PanelHealthSnapshot(true,at,panelUptime.Value,170000,159000,6810000,packets??0,sd):PanelHealthSnapshot.Empty;
        return new(at,150*1024*1024,110*1024*1024,120,16,hostUptime,panel,Link(at,reopen,rom,recovered));
    }
}
