using OpsDeck.Core;
using System.Text.Json;
internal static class M41Tests
{
    public static async Task Run(Action<string,bool> Check,Action<string,Action> Throws)
    {
        var now=DateTimeOffset.UtcNow;
        Check("m41-time-null-is-unknown",!Freshness.IsCurrent(null,now,10));
        Check("m41-time-boundary",Freshness.IsCurrent(now.AddSeconds(-10),now,10));
        Check("m41-time-expired",!Freshness.IsCurrent(now.AddSeconds(-11),now,10));
        Check("m41-small-clock-skew",Freshness.IsCurrent(now.AddSeconds(2),now,10));
        Check("m41-future-clock-not-fresh",!Freshness.IsCurrent(now.AddMinutes(2),now,10));
        Check("m41-future-age-is-unknown",Freshness.AgeSeconds(now.AddMinutes(2),now)==-1);
        Throws("m41-invalid-ttl",()=>Freshness.IsCurrent(now,now,0));
        var partial=new Metric(SourceState.Partial,12,CollectedAt:now.AddMinutes(-5));
        Check("m41-partial-expiry",Freshness.MetricState(partial,now,180)==SourceState.Stale);
        Check("m41-partial-fresh",Freshness.MetricState(partial with{CollectedAt=now},now,180)==SourceState.Partial);
        Check("m41-denied-retains-cause",Freshness.MetricState(partial with{State=SourceState.Denied},now,180)==SourceState.Denied);
        Check("m41-setup-not-stale",Freshness.MetricState(Metric.Setup(),now,180)==SourceState.Setup);
        using(var doc=JsonDocument.Parse(JsonSerializer.Serialize(partial.Wire(now,180))))
            Check("m41-wire-agrees-with-gui",doc.RootElement.GetProperty("state").GetInt32()==3);
        var profile=new CloudAccountConfig{ProfileId="test",Name="Test",AccountId=new string('1',32),Enabled=true};
        var a=AccountState.Empty(profile) with{Workers=new Metric(SourceState.Ok,42,CollectedAt:now.AddHours(1))};
        var total=AccountAggregate.Combine([a],x=>x.Workers,now,180);
        Check("m41-future-account-not-totalled",total.Value==null&&total.Covered==0);
        string dir=Path.Combine(Path.GetTempPath(),"alaz-m41-"+Guid.NewGuid().ToString("N"));
        try {
            var map=new ProjectMap(dir);var key=new ResourceKey(new string('1',32),ResourceKind.Worker,"account","api");
            var before=map.Load();map.Save([new(key,"Test")],before.Revision);
            using(var lease=new FileStream(Path.Combine(dir,"project-map.json.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
                try{new ProjectMap(dir).Save([],map.Load().Revision);Check("m41-map-concurrent-writer",false);}
                catch(IOException){Check("m41-map-concurrent-writer",true);}
            }
            Check("m41-map-conflict-preserved",map.Load().Assignments.Length==1);
            Throws("m41-map-null-entry",()=>ProjectMap.Validate([null!]));
            var settings=new LocalSettings(dir);
            await using(var e=new AppEngine(new HostConfig(),settings)) {
                Check("m41-initial-pc-not-fresh",!e.PcIsFresh);
                e.SerialPaused=true;e.SuspendObservation();
                Check("m41-suspend-is-explicit",e.PowerSuspended&&!e.PcIsFresh);
                e.ResumeObservation();
                Check("m41-resume-retains-user-pause",!e.PowerSuspended&&e.SerialPaused&&!e.PcIsFresh);
                e.SerialPaused=false;Check("m41-unpause",!e.SerialPaused);
                var inventory=await e.DiscoverResources(CancellationToken.None);
                Check("m41-zero-enabled-no-requests",inventory.Sets.Length==8&&inventory.Sets.All(x=>x.State==SourceState.Setup));
                Check("m41-setup-list-not-complete",!inventory.Complete);
                await e.DisposeAsync();
                Throws("m41-disposed-discovery-denied",()=>e.DiscoverResources(CancellationToken.None));
                Throws("m41-disposed-start-denied",()=>e.Start(false));
            }
        } finally {if(Directory.Exists(dir))Directory.Delete(dir,true);}
        using(var sampler=new PcSampler(new HostConfig())) {
            sampler.Sample();await Task.Delay(1100);sampler.Sample();sampler.ResetBaseline();
            var first=sampler.Sample();
            Check("m41-rebaseline-no-sleep-cpu-average",first.Cpu==null);
            Check("m41-rebaseline-no-network-spike",first.RxMbps==null&&first.TxMbps==null);
            Check("m41-rebaseline-no-gpu-invented-zero",first.IntelGpu==null&&first.Gpu==null);
            await Task.Delay(1100);var next=sampler.Sample();
            Check("m41-rebaseline-recovers-cpu",next.Cpu is >=0 and <=100);
        }
    }
}
