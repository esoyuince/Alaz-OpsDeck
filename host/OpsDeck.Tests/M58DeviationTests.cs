using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M58DeviationTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool v)=>check("m58-"+n,v);var now=DateTimeOffset.UtcNow;var d=new TelemetryMetricDescriptor("cpu","CPU","%");
        MetricAveragePoint[] P(int n,double start,double step)=>Enumerable.Range(0,n).Select(i=>new MetricAveragePoint(now.AddMinutes(-n+i),start+i*step)).ToArray();
        var insufficient=BaselineDeviationEngine.Analyze(d,P(29,10,1),P(5,20,0));C("baseline-min",!insufficient.Sufficient&&insufficient.BaselinePoints==29);
        insufficient=BaselineDeviationEngine.Analyze(d,P(30,10,1),P(4,20,0));C("recent-min",!insufficient.Sufficient&&insufficient.RecentPoints==4);
        var dev=BaselineDeviationEngine.Analyze(d,P(30,0,1),P(5,30,0));C("sufficient",dev.Sufficient&&dev.ZScore>1&&dev.RecentAvg==30);
        C("delta",dev.DeltaPercent>100);
        var flat=BaselineDeviationEngine.Analyze(d,P(30,10,0),P(5,12,0));C("flat-no-z",flat.Sufficient&&flat.ZScore==null&&flat.BaselineStdDev==0);
        C("disclaimer",BaselineDeviationReport.Disclaimer.Contains("not a fault diagnosis",StringComparison.OrdinalIgnoreCase)&&BaselineDeviationReport.Disclaimer.Contains("temperature limit",StringComparison.OrdinalIgnoreCase));
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m58-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try{
            string path=Path.Combine(dir,"telemetry.db");var minuteNow=DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/60*60);var link=LinkHealthSnapshot.Initial(now);
            using(var store=new TelemetryHistoryStore(path)){
                for(int i=40;i>=1;i--)store.Add(minuteNow.AddMinutes(-i),[new PcSample(Cpu:i,CpuTemp:50+i/10d)],link);
                var rows=store.ReadMetricRange("cpu",minuteNow.AddMinutes(-35),minuteNow.AddMinutes(-5),100);C("store-range",rows.Length==30&&rows[0].At<rows[^1].At);
                var baseRows=store.ReadMetricRange("cpu",minuteNow.AddMinutes(-40),minuteNow.AddMinutes(-15),100);var recentRows=store.ReadMetricRange("cpu",minuteNow.AddMinutes(-15),minuteNow,100);C("ranges-no-overlap",!baseRows.Select(x=>x.At).Intersect(recentRows.Select(x=>x.At)).Any());
                C("descriptor-catalog",TelemetryHistoryStore.MetricDescriptors.Any(x=>x.Key=="cpu_temp")&&TelemetryHistoryStore.MetricDescriptors.Any(x=>x.Key=="intel_shared")&&TelemetryHistoryStore.MetricDescriptors.Length==13);
                try{store.ReadMetricRange("cpu_avg;DROP TABLE telemetry_minute",now.AddHours(-1),now,100);C("key-guard",false);}catch(ArgumentException){C("key-guard",true);}
                try{store.ReadMetricRange("cpu",now,now.AddHours(-1),100);C("range-guard",false);}catch(ArgumentException){C("range-guard",true);}
                try{store.ReadMetricRange("cpu",now.AddHours(-1),now,10081);C("row-bound",false);}catch(ArgumentOutOfRangeException){C("row-bound",true);}
            }
            var settings=new LocalSettings(Path.Combine(dir,"engine"));var engine=new AppEngine(new HostConfig(),settings);engine.Start(false,false);
            var report=engine.ReadBaselineDeviation();C("engine-empty-safe",report.Metrics.Length==13&&report.Metrics.All(x=>!x.Sufficient));
            C("engine-window",report.BaselineTo==report.RecentFrom&&(report.RecentTo-report.RecentFrom)>TimeSpan.FromMinutes(14)&&(report.BaselineTo-report.BaselineFrom)>TimeSpan.FromHours(23));
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
}
