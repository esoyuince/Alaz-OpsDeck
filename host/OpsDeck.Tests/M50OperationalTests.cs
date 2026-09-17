using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M50OperationalTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m50-"+n,ok);
        var now=DateTimeOffset.UtcNow;
        OperationalSnapshot S(bool locked=false,bool suspended=false,SourceState link=SourceState.Ok,bool recovering=false,
            int reopen=0,int probe=0,int recovered=0,SerialRecoveryAction action=SerialRecoveryAction.None,
            SerialRecoveryReason reason=SerialRecoveryReason.None,SourceState task=SourceState.Ok,int active=0,
            int approval=0,int blocked=0,int expired=0,SourceState workers=SourceState.Ok,SourceState d1=SourceState.Ok,
            SourceState r2=SourceState.Ok,SourceState hosting=SourceState.Ok,int httpsUp=14,int httpsTotal=14)
            =>new(locked,suspended,link,recovering,reopen,probe,recovered,action,reason,SourceState.Ok,SourceState.Ok,
                task,active,approval,blocked,expired,workers,d1,r2,hosting,httpsUp,httpsTotal);
        var baseline=S();
        C("same-no-event",OperationalDiff.Generate(baseline,baseline,now).Length==0);
        C("lock-event",OperationalDiff.Generate(baseline,S(locked:true),now).Single().Code=="SESSION_LOCKED");
        C("power-event",OperationalDiff.Generate(baseline,S(suspended:true),now).Single().Code=="POWER_SUSPEND");
        var recovery=S(link:SourceState.Partial,recovering:true,reopen:1,action:SerialRecoveryAction.ReopenPort,reason:SerialRecoveryReason.ForwardStalled);
        C("recovery-event",OperationalDiff.Generate(baseline,recovery,now).Any(x=>x.Code=="LINK_RECOVERY"&&x.Severity==OperationalSeverity.Warning));
        var recovered=S(recovered:1);var recoveredEvents=OperationalDiff.Generate(recovery,recovered,now);
        C("recovered-event",recoveredEvents.Any(x=>x.Code=="LINK_RECOVERED"&&x.Severity==OperationalSeverity.Info));
        C("approval-event",OperationalDiff.Generate(baseline,S(active:1,approval:1),now).Any(x=>x.Code=="TASK_APPROVAL"));
        C("blocked-event",OperationalDiff.Generate(baseline,S(blocked:1),now).Any(x=>x.Code=="TASK_BLOCKED"&&x.Severity==OperationalSeverity.Error));
        C("activity-start",OperationalDiff.Generate(baseline,S(active:2),now).Any(x=>x.Code=="TASK_ACTIVITY_STARTED"));
        C("cloud-state",OperationalDiff.Generate(baseline,S(d1:SourceState.Error),now).Any(x=>x.Code=="D1_STATE"&&x.Domain==OperationalDomain.Cloud));
        C("https-degraded",OperationalDiff.Generate(baseline,S(httpsUp:13),now).Any(x=>x.Code=="HTTPS_DEGRADED"));
        C("https-restored",OperationalDiff.Generate(S(httpsUp:13),baseline,now).Any(x=>x.Code=="HTTPS_RESTORED"));
        var all=OperationalDiff.Generate(baseline,S(active:1,approval:1,d1:SourceState.Error,httpsUp:13),now);
        C("generated-sanitized",all.All(x=>!x.Summary.Contains("SECRET",StringComparison.OrdinalIgnoreCase)&&x.Summary.Length<=180));
        try{new OperationalEvent(now,OperationalSeverity.Info,OperationalDomain.Host,"bad-code","ok").Validate();C("reject-code",false);}catch(ArgumentException){C("reject-code",true);}
        try{new OperationalEvent(now,OperationalSeverity.Info,OperationalDomain.Host,"GOOD","bad\nline").Validate();C("reject-control",false);}catch(ArgumentException){C("reject-control",true);}
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m50-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            using(var store=new OperationalEventStore(Path.Combine(dir,"ops-events.db")))
            {
                store.Add(new(now.AddDays(-20),OperationalSeverity.Info,OperationalDomain.Host,"OLD_EVENT","Old event"));
                C("retention-prunes-old",store.Count()==0);
                store.Add(new(now.AddSeconds(-2),OperationalSeverity.Info,OperationalDomain.Host,"HOST_STARTED","Host started"));
                store.Add(new(now,OperationalSeverity.Warning,OperationalDomain.Link,"LINK_RECOVERY","Recovery started"));
                var rows=store.Read(10);C("store-roundtrip",rows.Length==2&&rows[0].Code=="LINK_RECOVERY"&&rows[1].Code=="HOST_STARTED");
                C("store-count",store.Count()==2);
                try{store.Read(0);C("read-bound",false);}catch(ArgumentOutOfRangeException){C("read-bound",true);}
            }
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
        string engineDir=Path.Combine(Path.GetTempPath(),"opsdeck-m50-engine-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(engineDir);
        try
        {
            var settings=new LocalSettings(engineDir);var engine=new AppEngine(new HostConfig(),settings);engine.Start(serial:false,wifiTelemetry:false);
            Thread.Sleep(100);C("engine-store-available",engine.OperationalTimelineAvailable);
            C("engine-host-started",engine.ReadOperationalEvents(20).Any(x=>x.Code=="HOST_STARTED"));
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
            using var store=new OperationalEventStore(Path.Combine(engineDir,"ops-events.db"));
            var persisted=store.Read(20);C("engine-host-stopped",persisted.Any(x=>x.Code=="HOST_STOPPED")&&persisted.Any(x=>x.Code=="HOST_STARTED"));
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(engineDir,true);}
    }
}
