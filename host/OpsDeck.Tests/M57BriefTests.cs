using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M57BriefTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool v)=>check("m57-"+n,v);var now=DateTimeOffset.UtcNow;
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m57-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            string db=Path.Combine(dir,"events.db");using(var store=new OperationalEventStore(db)){
                store.Add(new(now.AddMinutes(-20),OperationalSeverity.Warning,OperationalDomain.Link,"LINK_RECOVERY","Recovery"));
                store.Add(new(now.AddMinutes(-19),OperationalSeverity.Info,OperationalDomain.Link,"LINK_RECOVERED","Recovered"));
                store.Add(new(now.AddMinutes(-18),OperationalSeverity.Warning,OperationalDomain.Https,"HTTPS_DEGRADED","HTTPS 13/14"));
                store.Add(new(now.AddMinutes(-17),OperationalSeverity.Info,OperationalDomain.Cloud,"D1_STATE","D1 OK"));
                for(int i=0;i<505;i++)store.Add(new(now.AddMinutes(-10).AddMilliseconds(i),OperationalSeverity.Info,OperationalDomain.Host,"BURST","Bounded aggregate fixture"));
                var sum=store.SummarizeRange(now.AddHours(-1),now);
                C("aggregate-not-truncated",sum.Total==509&&sum.Info==507&&sum.Warning==2);
                C("aggregate-codes",sum.LinkRecovery==1&&sum.LinkRecovered==1&&sum.HttpsDegraded==1&&sum.CloudStateChanges==1);
                C("read-still-bounded",store.ReadRange(now.AddHours(-1),now,OperationalEventStore.MaxRead).Length==OperationalEventStore.MaxRead);
            }
            var metrics=new[]{new TrendMetric("cpu_temp","CPU temp","C",55,65,75,10),new TrendMetric("chassis_temp","Chassis temp","C",40,45,50,10),new TrendMetric("gpu_temp","NVIDIA temp","C",50,60,70,10)};
            var telemetry=new TelemetryTrend(TelemetryWindow.H24,now.AddHours(-24),now,metrics,[new("C:",91,94,95,20,10)],new(1,0,1,SourceState.Ok,10),10);
            var rs=new RangeStat(100,110,120,115,10);var lifecycle=new LifecycleTrend(TelemetryWindow.H24,now.AddHours(-24),now,10,rs,rs,rs,rs,rs,rs,rs,1000,0,5000,1,0,2,1,2,SourceState.Ok);
            var evsum=new OperationalRangeSummary(8,4,3,1,4,2,2,0,1,1,0,0,1,1,1,1,2);
            var latest=Enumerable.Range(0,12).Select(i=>new OperationalEvent(now.AddMinutes(-i),OperationalSeverity.Info,OperationalDomain.Host,"EV"+i,"Event "+i)).ToArray();
            var brief=OpsBriefEngine.Build(TelemetryWindow.H24,now.AddHours(-24),now,evsum,telemetry,lifecycle,new(AlertLevel.Info,[]),latest);
            C("brief-window",brief.Window==TelemetryWindow.H24&&brief.From==now.AddHours(-24));
            C("brief-latest-bounded",brief.LatestEvents.Length==10);
            C("brief-disk-policy",brief.Highlights.Any(x=>x.Contains("Peak volume use: C: 95.0% (ATTENTION policy)")));
            C("brief-temp-informational",brief.Highlights.Any(x=>x.Contains("no temperature threshold policy")));
            C("brief-memory",brief.Highlights.Any(x=>x.Contains("Host private memory min/latest")));
            C("brief-disclaimer",OpsBrief.Disclaimer.Contains("not causality",StringComparison.OrdinalIgnoreCase));
            try{OpsBriefEngine.Build(TelemetryWindow.H1,now.AddHours(-1),now,evsum,telemetry,lifecycle,new(AlertLevel.Info,[]),[]);C("brief-window-guard",false);}catch(ArgumentException){C("brief-window-guard",true);}
            var settings=new LocalSettings(Path.Combine(dir,"engine"));var engine=new AppEngine(new HostConfig(),settings);engine.Start(false,false);
            var live=engine.ReadOpsBrief(TelemetryWindow.H24);C("engine-host-start",live.Events.HostStarted>=1&&live.LatestEvents.Any(x=>x.Code=="HOST_STARTED"));
            C("engine-empty-history-safe",live.Telemetry.MinuteRows==0&&live.Lifecycle.MinuteRows==0);
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
}
