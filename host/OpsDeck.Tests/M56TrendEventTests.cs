using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M56TrendEventTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool v)=>check("m56-"+n,v);
        var now=DateTimeOffset.UtcNow;string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m56-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            string path=Path.Combine(dir,"events.db");
            using(var store=new OperationalEventStore(path))
            {
                store.Add(new(now.AddHours(-3),OperationalSeverity.Info,OperationalDomain.Host,"OLD","Older but retained"));
                store.Add(new(now.AddHours(-1),OperationalSeverity.Info,OperationalDomain.Link,"START","Window start"));
                store.Add(new(now.AddMinutes(-30),OperationalSeverity.Warning,OperationalDomain.Https,"MID","Middle"));
                store.Add(new(now,OperationalSeverity.Error,OperationalDomain.Cloud,"END","Window end"));
                var rows=store.ReadRange(now.AddHours(-1),now,20);
                C("range-inclusive",rows.Length==3&&rows.Any(x=>x.Code=="START")&&rows.Any(x=>x.Code=="END"));
                C("range-excludes-old",rows.All(x=>x.Code!="OLD"));
                C("range-order",rows[0].Code=="END"&&rows[^1].Code=="START");
                C("range-limit",store.ReadRange(now.AddHours(-4),now,2).Length==2);
                try{store.ReadRange(now,now.AddHours(-1),10);C("reverse-rejected",false);}catch(ArgumentException){C("reverse-rejected",true);}
                try{store.ReadRange(now.AddDays(-20),now,10);C("overspan-rejected",false);}catch(ArgumentException){C("overspan-rejected",true);}
                var context=TelemetryEventContextEngine.Build(now.AddHours(-1),now,rows,20);
                C("context-count",context.Events.Length==3);
                C("context-domains",context.DomainCount==3);
                C("context-severity",context.WarningCount==1&&context.ErrorCount==1);
                C("context-disclaimer",TelemetryEventContext.Disclaimer=="Time-related, not causal.");
                var bounded=TelemetryEventContextEngine.Build(now.AddHours(-4),now,store.ReadRange(now.AddHours(-4),now,20),2);
                C("context-limit",bounded.Events.Length==2);
            }
            using(var store=new OperationalEventStore(path))C("store-persistent",store.Count()==4);
            var settings=new LocalSettings(Path.Combine(dir,"engine"));var engine=new AppEngine(new HostConfig(),settings);engine.Start(false,false);
            var live=engine.ReadTelemetryEventContext(now.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(1),20);
            C("engine-host-event",live.Events.Any(x=>x.Code=="HOST_STARTED"&&x.Domain==OperationalDomain.Host));
            C("engine-bounded",live.Events.Length<=20);
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
}
