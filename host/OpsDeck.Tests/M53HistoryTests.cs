using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M53HistoryTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m53-"+n,ok);
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m53-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,"telemetry-history.db");var now=DateTimeOffset.UtcNow;
        VolumeReading V(string id,double total,double free)=>new(id,(long)(total*1073741824d),(long)(free*1073741824d),(long)(free*1073741824d),now,"fixture");
        PcSample S(double? cpu,double? temp,double? fan2=1200)=>new(Cpu:cpu,Gpu:30,GpuTemp:50,RamUsedGib:10,RamTotalGib:32,
            VramUsedGib:2,VramTotalGib:8,RxMbps:1,TxMbps:2,IntelGpu:20,IntelSharedGib:3.5,IntelSharedLimitGib:18,Fan1Rpm:1000,Fan2Rpm:fan2,CpuTemp:temp,ChassisTemp:40,Volumes:[V("C:",100,10)]);
        try
        {
            using(var store=new TelemetryHistoryStore(path))
            {
                var baseLink=LinkHealthSnapshot.Initial(now.AddHours(-1)).OnForward(now.AddMinutes(-10));
                store.Add(now.AddDays(-8),[S(99,99)],baseLink);
                store.Add(now.AddMinutes(-2),[S(10,60),S(30,70,0)],baseLink);
                var recovered=new LinkHealthSnapshot(SourceState.Ok,false,1,1,1,SerialRecoveryAction.RomProbeReset,SerialRecoveryReason.ForwardStalled,now.AddHours(-1),now.AddMinutes(-1),now.AddMinutes(-1));
                store.Add(now.AddMinutes(-1),[S(50,null)],recovered);
                C("retention",store.Count()==2);
                var one=store.ReadSummary(TelemetryWindow.H1,now);C("minute-rows",one.MinuteRows==2);
                var cpu=one.Metrics.Single(x=>x.Key=="cpu");C("cpu-min-avg-max",cpu.Min==10&&cpu.Avg==35&&cpu.Max==50&&cpu.Points==2);
                var ct=one.Metrics.Single(x=>x.Key=="cpu_temp");C("missing-not-zero",ct.Min==60&&ct.Max==70&&ct.Points==1);
                var f2=one.Metrics.Single(x=>x.Key=="fan2");C("zero-preserved",f2.Min==0&&f2.Points==2);
                var shared=one.Metrics.Single(x=>x.Key=="intel_shared");C("shared-history",shared.Min==3.5&&shared.Max==3.5&&shared.Points==2);
                var vol=one.Volumes.Single();C("volume-history",vol.Id=="C:"&&vol.MinUsed>89.9&&vol.MaxUsed<90.1&&vol.Points==2);
                var volumeSeries=store.ReadVolumeSeries(TelemetryWindow.H1,now,"C:",10);C("volume-series",volumeSeries.Length==2&&volumeSeries.All(x=>x.Used.HasValue));
                C("link-deltas",one.Link.Reopens==1&&one.Link.RomProbes==1&&one.Link.Recoveries==1&&one.Link.LatestState==SourceState.Ok);
                var series=store.ReadSeries(TelemetryWindow.H1,now,10);C("series-order",series.Length==2&&series[0].At<series[1].At);C("shared-series",series.All(x=>x.IntelShared==3.5));
                var fixed7=store.ReadFixedSeries(TelemetryWindow.D7,now,48);C("fixed-7d-bound",fixed7.Length==48);C("fixed-7d-leading-empty",fixed7.Take(40).All(x=>!x.Cpu.HasValue)&&fixed7[^1].Cpu.HasValue);
                var fixed15=store.ReadFixedSeries(TelemetryWindow.M15,now,48);C("fixed-15m-slots",fixed15.Length==15&&!fixed15[^1].Cpu.HasValue&&fixed15[^2].Cpu.HasValue);
                var fixedVol=store.ReadFixedVolumeSeries(TelemetryWindow.D7,now,"C:",48);C("fixed-volume-leading-empty",fixedVol.Length==48&&fixedVol.Take(40).All(x=>!x.Used.HasValue)&&fixedVol[^1].Used.HasValue);
                var bounds=store.ReadMetricBounds(TelemetryWindow.D7,now,"cpu");C("metric-bounds",bounds.First.HasValue&&bounds.Last.HasValue&&bounds.First<bounds.Last);var vb=store.ReadVolumeBounds(TelemetryWindow.D7,now,"C:");C("volume-bounds",vb.First.HasValue&&vb.Last.HasValue&&vb.First<vb.Last);
                C("window-labels",TelemetryWindows.Label(TelemetryWindow.M15)=="15m"&&TelemetryWindows.Label(TelemetryWindow.D7)=="7d");
                try{store.ReadSeries(TelemetryWindow.H1,now,9);C("series-bound",false);}catch(ArgumentOutOfRangeException){C("series-bound",true);}
            }
            using(var store=new TelemetryHistoryStore(path)){C("reopen-persistent",store.Count()==2&&store.ReadSummary(TelemetryWindow.H1,now).MinuteRows==2); var boundLink=LinkHealthSnapshot.Initial(now.AddHours(-1)); for(int j=3;j<21;j++)store.Add(now.AddMinutes(-j),[S(j,50+j)],boundLink); var bounded=store.ReadSeries(TelemetryWindow.H1,now,10); C("downsample-hard-bound",bounded.Length==10&&bounded[0].At<bounded[^1].At);}
            string legacyPath=Path.Combine(dir,"legacy-history.db");using(var seed=new TelemetryHistoryStore(legacyPath)){seed.Add(now.AddMinutes(-2),[S(22,61)],LinkHealthSnapshot.Initial(now));}
            using(var raw=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyPath}")){raw.Open();foreach(string c in new[]{"intel_shared_avg","intel_shared_min","intel_shared_max"}){using var drop=raw.CreateCommand();drop.CommandText=$"ALTER TABLE telemetry_minute DROP COLUMN {c}";drop.ExecuteNonQuery();}}
            using(var migrated=new TelemetryHistoryStore(legacyPath)){C("shared-schema-migration-preserves",migrated.Count()==1&&migrated.ReadSummary(TelemetryWindow.H1,now).Metrics.Single(x=>x.Key=="cpu").Points==1);migrated.Add(now.AddMinutes(-1),[S(33,62)],LinkHealthSnapshot.Initial(now));var sm=migrated.ReadSummary(TelemetryWindow.H1,now).Metrics.Single(x=>x.Key=="intel_shared");C("shared-schema-migration-writes",sm.Points==1&&sm.Avg==3.5);}
            var config=new HostConfig();var settings=new LocalSettings(Path.Combine(dir,"engine"));
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var engine=new AppEngine(config,settings);engine.Start(false,false);
            C("engine-store-available",engine.TelemetryHistoryAvailable);
            var empty=engine.ReadTelemetryTrend(TelemetryWindow.M15);C("engine-empty-safe",empty.MinuteRows==0&&empty.Link.LatestState==SourceState.NoData);
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
}

