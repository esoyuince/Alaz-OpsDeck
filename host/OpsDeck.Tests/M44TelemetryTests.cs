using System.Text;
using System.Text.Json;
using System.Diagnostics;
using OpsDeck.Core;
namespace OpsDeck.Tests;
internal static class M44TelemetryTests
{
    public static void Run(Action<string,bool> check)
    {
        var receipt=new PanelReceipt().Observe("PC_RX sequence=1 volumes=2").Observe("STATUS_RX sequence=1").Observe("CLOUD_RX sequence=1").Observe("INVENTORY_RX sequence=1");
        check("m44-ack-types-distinct",receipt.Pc==1&&receipt.Status==1&&receipt.Cloud==1&&receipt.Inventory==1);
        check("m44-ack-unknown-ignored",receipt.Observe("HEALTH uptime_s=1")==receipt);
        check("m44-ack-control-rejected",receipt.Observe("PC_RX bad\nline")==receipt);
        check("m44-ack-bounded",receipt.Observe("PC_RX "+new string('x',256))==receipt);
        check("m44-ack-reset-new-connection",new PanelReceipt().Pc==0);
        var now=new DateTimeOffset(2026,9,14,10,0,0,TimeSpan.Zero);
        var volume=new VolumeReading("C:",1000,100,50,now,"test");
        check("m44-disk-volume-not-user-quota",volume.Valid&&volume.UsedPercent==90);
        check("m44-disk-full-is-real-zero-free",(volume with{FreeBytes=0,AvailableBytes=0}).UsedPercent==100);
        check("m44-disk-empty-is-real-zero-used",(volume with{FreeBytes=1000,AvailableBytes=1000}).UsedPercent==0);
        check("m44-disk-unknown-not-zero",new VolumeReading("C:").UsedPercent==null);
        check("m44-disk-division-by-zero-rejected",!(volume with{TotalBytes=0}).Valid);
        check("m44-disk-negative-free-rejected",!(volume with{FreeBytes=-1}).Valid);
        check("m44-disk-overfull-free-rejected",!(volume with{FreeBytes=1001}).Valid);
        check("m44-disk-quota-cannot-exceed-free",!(volume with{AvailableBytes=101}).Valid);
        check("m44-disk-unknown-quota-no-success",!(volume with{AvailableBytes=null}).Valid);
        check("m44-disk-id-bounded",!(volume with{Id="C:/private"}).Valid);
        check("m44-disk-fresh-boundary",volume.State(now.AddSeconds(30))==SourceState.Ok);
        check("m44-disk-expired",volume.State(now.AddSeconds(31))==SourceState.Stale);
        check("m44-disk-future-withheld",volume.State(now.AddMinutes(-2))==SourceState.Stale);
        check("m44-disk-gib-units",new VolumeReading("C:",2147483648,1073741824,1073741824,now).FreeGib==1);
        const string cleanup="[{\"source\":\"cpu\",\"unregister_attempted\":true,\"helper_return\":true,\"error\":null},{\"source\":\"gpu\",\"unregister_attempted\":true,\"helper_return\":true,\"error\":null},{\"source\":\"fan_notebook\",\"unregister_attempted\":true,\"helper_return\":true,\"error\":null},{\"source\":\"fan_desktop\",\"unregister_attempted\":true,\"helper_return\":true,\"error\":null},{\"source\":\"ir_temperature\",\"unregister_attempted\":true,\"helper_return\":true,\"error\":null},{\"source\":\"ambient_temperature\",\"unregister_attempted\":true,\"helper_return\":true,\"error\":null},{\"source\":\"vr_temperature\",\"unregister_attempted\":true,\"helper_return\":true,\"error\":null}]";
        const string omen="{\"schema\":\"opsdeck.omen-probe.v3\",\"direct_control_api_invoked\":false,\"cleanup\":"+cleanup+",\"collected_utc\":\"2026-09-14T12:00:00Z\",\"snapshot\":{\"cpu_temperature_c\":65,\"chassis_temperature_c\":40,\"fan1_rpm\":0,\"fan2_rpm\":2500}}";
        var omenReading=OmenSensors.Parse(omen);
        check("m44-omen-required-readings",omenReading.CpuTemp==65&&omenReading.ChassisTemp==40&&omenReading.Fan1Rpm==0&&omenReading.Fan2Rpm==2500);
        check("m44-omen-measured-zero-retained",omenReading.Fan1Rpm==0);
        bool RejectsOmen(string value){try{OmenSensors.Parse(value);return false;}catch(InvalidDataException){return true;}}
        check("m44-omen-missing-fan-not-success",RejectsOmen(omen.Replace("\"fan2_rpm\":2500", "\"fan2_missing\":2500")));
        check("m44-omen-missing-chassis-is-explicit",OmenSensors.Parse(omen.Replace("\"chassis_temperature_c\":40,", "")).ChassisTemp==null);
        check("m44-omen-out-of-range-not-success",RejectsOmen(omen.Replace("\"cpu_temperature_c\":65", "\"cpu_temperature_c\":165")));
        check("m44-omen-chassis-out-of-range-not-success",RejectsOmen(omen.Replace("\"chassis_temperature_c\":40", "\"chassis_temperature_c\":165")));
        check("m44-omen-chassis-zero-retained",OmenSensors.Parse(omen.Replace("\"chassis_temperature_c\":40", "\"chassis_temperature_c\":0")).ChassisTemp==0);
        check("m44-omen-control-output-rejected",RejectsOmen(omen.Replace("\"direct_control_api_invoked\":false", "\"direct_control_api_invoked\":true")));
        check("m44-omen-cleanup-empty-rejected",RejectsOmen(omen.Replace(cleanup,"[]")));
        check("m44-omen-cleanup-partial-rejected",RejectsOmen(omen.Replace(cleanup,"[{\"source\":\"cpu\",\"unregister_attempted\":true,\"helper_return\":true,\"error\":null}]")));
        check("m44-omen-cleanup-duplicate-rejected",RejectsOmen(omen.Replace("\"source\":\"gpu\"","\"source\":\"cpu\"")));
        check("m44-omen-cleanup-not-attempted-rejected",RejectsOmen(omen.Replace("\"unregister_attempted\":true","\"unregister_attempted\":false")));
        check("m44-omen-cleanup-missing-return-rejected",RejectsOmen(omen.Replace(",\"helper_return\":true","")));
        check("m44-omen-cleanup-false-return-rejected",RejectsOmen(omen.Replace("\"helper_return\":true","\"helper_return\":false")));
        check("m44-omen-cleanup-error-rejected",RejectsOmen(omen.Replace("\"error\":null","\"error\":\"failed\"")));
        check("m44-omen-success-refresh-not-immediate",!OmenSensors.RefreshDue(now.AddSeconds(9),now,now));
        check("m44-omen-success-refresh-before-stale",OmenSensors.RefreshDue(now.AddSeconds(10),now,now));
        check("m44-omen-failure-retry-not-immediate",!OmenSensors.RefreshDue(now.AddSeconds(4),now,null));
        check("m44-omen-failure-retry-after-delay",OmenSensors.RefreshDue(now.AddSeconds(5),now,null));
        string dummyScript=Path.Combine(AppContext.BaseDirectory,"omen-dispose-test.ps1"),dummyPid=Path.Combine(AppContext.BaseDirectory,"omen-dispose-test.pid");
        File.WriteAllText(dummyScript,"param([int]$DurationSeconds,[string]$OutputPath,[switch]$InternalWorker,[switch]$Quiet) [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'omen-dispose-test.pid'),[string]$PID); Start-Sleep -Seconds 60");
        File.Delete(dummyPid);
        var disposableOmen=new OmenSensors(dummyScript);disposableOmen.Read();
        for(int i=0;i<30&&!File.Exists(dummyPid);i++)Thread.Sleep(100);
        int dummyProcessId=File.Exists(dummyPid)?int.Parse(File.ReadAllText(dummyPid)):0;
        disposableOmen.Dispose();
        bool dummyExited=dummyProcessId>0;
        if(dummyExited){try{using var process=Process.GetProcessById(dummyProcessId);dummyExited=process.HasExited;}catch(ArgumentException){dummyExited=true;}}
        check("m44-omen-dispose-confirms-worker-exit",dummyExited);
        File.Delete(dummyScript);File.Delete(dummyPid);
        var start=now.AddDays(-1);var end=now.AddDays(10);
        Metric Cost(double x)=>new(SourceState.Ok,x,CollectedAt:now,SourceEnd:now,Unit:"USD",PeriodStart:start,PeriodEnd:end,UsageCharges:true);
        var money=Cost(.45);
        check("m44-billing-source-current",Freshness.MetricState(money,now,7200)==SourceState.Ok);
        check("m44-billing-old-source-not-fresh-request",Freshness.MetricState(money with{SourceEnd=now.AddDays(-32)},now,7200)==SourceState.Stale);
        check("m44-billing-source-age-boundary",Freshness.MetricState(money with{SourceEnd=now.AddHours(-48)},now,7200)==SourceState.Ok);
        check("m44-billing-source-age-expired",Freshness.MetricState(money with{SourceEnd=now.AddHours(-48).AddSeconds(-1)},now,7200)==SourceState.Stale);
        check("m44-billing-future-source-not-current",Freshness.MetricState(money with{SourceEnd=now.AddMinutes(1)},now,7200)==SourceState.Stale);
        check("m44-billing-missing-source-not-green",Freshness.MetricState(money with{SourceEnd=null},now,7200)==SourceState.Partial);
        check("m44-billing-missing-period-not-green",Freshness.MetricState(money with{PeriodEnd=null},now,7200)==SourceState.Partial);
        check("m44-billing-inverted-period-not-green",Freshness.MetricState(money with{PeriodEnd=start},now,7200)==SourceState.Partial);
        check("m44-billing-denied-retains-cause",Freshness.MetricState(money.Failed(SourceState.Denied,"denied"),now,7200)==SourceState.Denied);
        check("m44-billing-note-does-not-hide-denied",MetricPresentation.Note(money.Failed(SourceState.Denied,"denied"),now,7200)=="PERMISSION DENIED");
        check("m44-billing-note-does-not-hide-error",MetricPresentation.Note(money.Failed(SourceState.Error,"failed"),now,7200)=="API ERROR");
        check("m44-billing-source-reason",MetricPresentation.Note(money with{SourceEnd=now.AddDays(-32)},now,7200)=="SOURCE STALE");
        check("m44-billing-period-reason",MetricPresentation.Note(money with{PeriodEnd=null},now,7200)=="PERIOD UNKNOWN");
        var p1=new CloudAccountConfig{ProfileId="a",Name="A",AccountId=new string('1',32),Enabled=true};
        var p2=p1 with{ProfileId="b",Name="B",AccountId=new string('2',32)};
        var a=AccountState.Empty(p1) with{Cost=Cost(.45)};var b=AccountState.Empty(p2) with{Cost=Cost(.05)};
        Metric Total(params AccountState[] all)=>AccountAggregate.Combine(all,x=>x.Cost,now,7200,true);
        var total=Total(a,b);check("m44-billing-comparable-total",total.State==SourceState.Ok&&total.Value==.5&&total.Covered==2);
        check("m44-billing-no-mixed-currency",Total(a,b with{Cost=b.Cost with{Unit="EUR"}}).Value==null);
        check("m44-billing-no-mixed-period",Total(a,b with{Cost=b.Cost with{PeriodStart=start.AddDays(-1)}}).Value==null);
        check("m44-billing-different-source-cutoff-withheld",Total(a,b with{Cost=b.Cost with{SourceEnd=now.AddHours(-2)}}).Value==null);
        check("m44-billing-mixed-source-kind-withheld",Total(a,b with{Cost=b.Cost with{UsageCharges=false}}).Value==null);
        var incomplete=Total(a with{Cost=a.Cost with{SourceEnd=now.AddDays(-32)}},b with{Cost=b.Cost with{PeriodEnd=null}});
        check("m44-billing-real-shape-no-total",incomplete.Value==null&&incomplete.State==SourceState.Partial&&incomplete.Note=="SOURCE / PERIOD");
        var sub=Total(a,b with{Cost=b.Cost with{SourceEnd=now.AddDays(-32)}});
        check("m44-billing-stale-excluded-subtotal-explicit",sub.Value==.45&&sub.State==SourceState.Partial&&sub.Note=="KNOWN SUBTOTAL");
        using(var doc=JsonDocument.Parse(JsonSerializer.Serialize((money with{SourceEnd=now.AddDays(-32)}).Wire(now,7200),Json.Options))){
            var x=doc.RootElement;check("m44-billing-wire-stale",x.GetProperty("state").GetInt32()==3);
            check("m44-billing-wire-dates",x.GetProperty("source_end").GetString()=="2026-08-13"&&x.GetProperty("period_start").GetString()=="2026-09-13");
        }
        check("m44-billing-ui-period-visible",MetricPresentation.Detail(money with{PeriodEnd=null}).Contains("2026-09-13 - ?"));
        var urls=HostingScope.Urls(["https://EXAMPLE.com","https://example.com/","https://example.com/Path","https://example.com/path"]);
        check("m44-hosting-canonical-host-dedupe-path-preserved",urls.Length==3);
        SiteResult Site(string u,bool ok)=>new(new Uri(u).Host,ok?200:503,1,ok,"test",Url:u);
        string[] general=["https://general.example/"];string[] own=["https://own.example/"];
        SiteResult[] results=[Site(general[0],true),Site(own[0],true)];
        check("m44-hosting-all-union",HostingScope.Summarize(general.Concat(own),results,now,"ALL","test").Value==2);
        check("m44-hosting-account-scope-only",HostingScope.Summarize(own,results,now,"ACCOUNT","test").Secondary==1);
        check("m44-hosting-general-not-moved",HostingScope.Summarize(general,results,now,"GENERAL","test").Value==1&&general.Length==1);
        check("m44-hosting-missing-check-not-up",HostingScope.Summarize(own,[],now,"ACCOUNT","test").State==SourceState.Partial);
        check("m44-hosting-no-sites-setup",HostingScope.Summarize([],results,now,"ACCOUNT","test").State==SourceState.Setup);
        check("m44-hosting-aliases-preserve-original",HostingScope.Summarize(general.Concat(own),[Site(general[0],true) with{FinalHost="own.example"},Site(own[0],true)],now,"ALL","test").Value==2);
        var volumes=Enumerable.Range(0,26).Select(i=>volume with{Id=$"{(char)('A'+i)}:",ObservedAt=DateTimeOffset.UtcNow}).ToArray();
        using(var doc=JsonDocument.Parse(new PcSample(Cpu:5,IntelTemp:66,Volumes:volumes).Wire())){
            var x=doc.RootElement;check("m44-pc-no-cpu-from-intel-temp",!x.TryGetProperty("cpu_temp",out _)&&x.GetProperty("cpu_sensor").GetInt32()==4);
            check("m44-pc-chassis-missing-not-zero",!x.TryGetProperty("chassis_temp",out _)&&x.GetProperty("chassis_sensor").GetInt32()==4);
            check("m44-pc-fans-not-fabricated",!x.TryGetProperty("fan1_rpm",out _)&&x.GetProperty("fan_sensor").GetInt32()==4);
            check("m44-pc-volume-display-bounded",x.GetProperty("volumes").GetArrayLength()==2&&x.GetProperty("volume_count").GetInt32()==26);
        }
        string full=new PcSample(Cpu:100,Gpu:100,GpuTemp:100,RamUsedGib:30,RamTotalGib:32,VramUsedGib:7,VramTotalGib:8,RxMbps:100,TxMbps:100,IntelGpu:30,IntelTemp:70,IntelSharedGib:5,IntelSharedLimitGib:16,Fan1Rpm:0,Fan2Rpm:2500,CpuTemp:65,Volumes:volumes,ChassisTemp:40).Wire();
        check("m44-pc-full-frame-budget",Encoding.UTF8.GetByteCount(full)<3000);
        using(var doc=JsonDocument.Parse(full)){
            check("m44-pc-measured-zero-rpm-retained",doc.RootElement.GetProperty("fan1_rpm").GetDouble()==0);
            check("m44-pc-chassis-wire",doc.RootElement.GetProperty("chassis_temp").GetDouble()==40&&doc.RootElement.GetProperty("chassis_sensor").GetInt32()==1);
        }
        using(var doc=JsonDocument.Parse(new PcSample(ChassisTemp:0).Wire()))check("m44-pc-chassis-measured-zero-retained",doc.RootElement.GetProperty("chassis_temp").GetDouble()==0&&doc.RootElement.GetProperty("chassis_sensor").GetInt32()==1);
        check("m44-cloud-full-frame-budget",Encoding.UTF8.GetByteCount(a.Wire(0,2,"1234abcd",now))<3000);
        check("m44-status-full-frame-budget",Encoding.UTF8.GetByteCount((FleetState.Empty with{Cost=total}).Wire(now,false))<3000);
        string folder=Path.Combine(AppContext.BaseDirectory,"m44-fixtures");Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"pc-synthetic.json"),full);
        File.WriteAllText(Path.Combine(folder,"cost-synthetic.json"),JsonSerializer.Serialize(money.Wire(now,7200),Json.Options));
    }
    public static async Task<int> Probe()
    {
        using var sampler=new PcSampler(new HostConfig());sampler.Sample();await Task.Delay(5000);var value=sampler.Sample();
        var report=new{environment="REAL PC READ-ONLY; NO SERIAL OR CLOUD API",time=DateTimeOffset.UtcNow,pc=value,wire=value.Wire(),sensorsInstalledByThisRun=false};
        Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions(Json.Options){WriteIndented=true}));
        return value.Volumes is {Length:>0}&&value.Volumes.Any(v=>v.Id=="C:"&&v.Valid)&&value.CpuTemp.HasValue&&value.ChassisTemp.HasValue&&value.Fan1Rpm.HasValue&&value.Fan2Rpm.HasValue?0:1;
    }
}
