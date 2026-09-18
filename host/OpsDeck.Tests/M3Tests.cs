using System.Net;
using System.Text;
using System.Text.Json;
using OpsDeck.Core;
internal static class M3Tests
{
    public static async Task Run(Action<string,bool> Check,Action<string,Action> Throws)
    {
        const string aId="11111111111111111111111111111111",bId="22222222222222222222222222222222";
        const string luid="0x00000000_0x00015DFE";
        string E(int pid,int engine,string id=luid)=>$"pid_{pid}_luid_{id}_phys_0_eng_{engine}_engtype_3D";
        Check("m3-gpu-processes-same-engine-sum",GpuMath.BusyEngine(luid,[new(E(1,0),20),new(E(2,0),30),new(E(3,1),40)])==50);
        Check("m3-gpu-not-sum-unrelated-engines",GpuMath.BusyEngine(luid,[new(E(1,0),40),new(E(2,1),55)])==55);
        Check("m3-gpu-other-adapter-ignored",GpuMath.BusyEngine(luid,[new(E(1,0),5),new(E(2,0,"0x00000000_0x00099999"),100)])==5);
        Check("m3-gpu-duplicate-counter-not-doubled",GpuMath.BusyEngine(luid,[new(E(1,0),20),new(E(1,0),30)])==30);
        Check("m3-gpu-no-data-not-zero",GpuMath.BusyEngine(luid,[])==null);
        Check("m3-gpu-real-zero",GpuMath.BusyEngine(luid,[new(E(1,0),0)])==0);
        Check("m3-gpu-invalid-counter",GpuMath.BusyEngine(luid,[new(E(1,0),double.NaN)])==null);
        Check("m3-gpu-clamped",GpuMath.BusyEngine(luid,[new(E(1,0),60),new(E(2,0),70)])==100);
        Check("m3-adapter-memory-not-process-memory",GpuMath.Memory(luid,[new("luid_"+luid+"_phys_0",1073741824)])==1);
        Check("m3-ambiguous-memory-withheld",GpuMath.Memory(luid,[new("luid_"+luid+"_phys_0",1),new("luid_"+luid+"_phys_1",2)])==null);
        var real=new GpuReading(new(luid,"Intel",0x8086,1,.125,16),34,0,2);
        var alias=new GpuReading(new("0x00000000_0x00077777","Intel",0x8086,1,.125,16),null,null,null);
        string? selected=null;Check("m3-intel-indirect-alias-excluded",PcSampler.SelectMeasured([alias,real],ref selected)==real&&selected==luid);
        var noData=real with{Usage=null,DedicatedGib=null,SharedGib=null};Check("m3-selected-luid-stable",PcSampler.SelectMeasured([alias with{Usage=99},noData],ref selected)==noData);
        selected=null;Check("m3-two-measured-adapters-ambiguous",PcSampler.SelectMeasured([alias with{Usage=20},real],ref selected)==null);
        var p1=new CloudAccountConfig{ProfileId="vetakeep",Name="VetaKeep",AccountId=aId,Enabled=true};
        var p2=new CloudAccountConfig{ProfileId="projects",Name="Diğer Projeler",AccountId=bId,Enabled=true};
        new HostConfig{Accounts=[p1,p2]}.Validate();Check("m3-two-account-config",true);
        Throws("m3-duplicate-account-rejected",()=>new HostConfig{Accounts=[p1,p2 with{AccountId=aId}]}.Validate());
        Throws("m3-duplicate-profile-rejected",()=>new HostConfig{Accounts=[p1,p2 with{ProfileId=p1.ProfileId}]}.Validate());
        Throws("m3-bad-profile-path-rejected",()=>new HostConfig{Accounts=[p1 with{ProfileId="../x"}]}.Validate());
        Throws("m3-oversized-name-rejected",()=>new HostConfig{Accounts=[p1 with{Name=new string('x',33)}]}.Validate());
        Throws("m3-too-many-profiles-rejected",()=>new HostConfig{Accounts=[p1,p2,p1]}.Validate());
        Check("m3-panel-turkish-ascii",AccountState.PanelName("Diğer Projeler") == "Diger Projeler");
        Check("m3-panel-name-bounded",AccountState.PanelName(new string('a',40)).Length==22);
        var now=DateTimeOffset.UtcNow;var start=new DateTimeOffset(2026,9,1,0,0,0,TimeSpan.Zero);var end=start.AddMonths(1);
        Metric Money(double value,string currency="USD",DateTimeOffset? periodEnd=null)=>new(SourceState.Ok,value,CollectedAt:now,Unit:currency,PeriodStart:start,PeriodEnd:periodEnd??end);
        var a=AccountState.Empty(p1) with{Cost=Money(2),Workers=new(SourceState.Ok,10,CollectedAt:now,Unit:"req/min")};
        var b=AccountState.Empty(p2) with{Cost=Money(3),Workers=new(SourceState.Ok,20,CollectedAt:now,Unit:"req/min")};
        Metric Combine(params AccountState[] x)=>AccountAggregate.Combine(x,z=>z.Cost,now,7200,true);
        var total=Combine(a,b);Check("m3-total-same-period-currency",total.Value==5&&total.State==SourceState.Ok&&total.Covered==2&&total.Expected==2);
        var partial=Combine(a,b with{Cost=b.Cost.Failed(SourceState.Denied,"denied")});Check("m3-one-denied-known-subtotal",partial.Value==2&&partial.State==SourceState.Partial&&partial.Covered==1&&partial.Expected==2);
        Check("m3-no-success-not-zero",Combine(a with{Cost=Metric.Setup()},b with{Cost=Metric.Setup()}).Value==null);
        Check("m3-mixed-currency-withheld",Combine(a,b with{Cost=Money(3,"EUR")}).Value==null);
        Check("m3-mixed-period-withheld",Combine(a,b with{Cost=Money(3,periodEnd:end.AddMonths(1))}).Value==null);
        Check("m3-unknown-period-withheld",Combine(a,b with{Cost=Money(3) with{PeriodEnd=null}}).Value==null);
        Check("m3-stale-account-not-counted",Combine(a,b with{Cost=Money(3) with{CollectedAt=now.AddHours(-3)}}).Covered==1);
        Check("m3-duplicate-account-no-total",Combine(a,a).Value==null);
        Check("m3-no-enabled-account-setup",Combine(a with{Profile=p1 with{Enabled=false}}).State==SourceState.Setup);
        Check("m3-request-aggregate",AccountAggregate.Combine([a,b],z=>z.Workers,now,180).Value==30);
        string wire=a.Wire(0,2,"1234abcd",now);
        Check("m3-account-frame-bounded",Encoding.UTF8.GetByteCount(wire)<3000);
        Check("m3-account-id-not-on-panel",!wire.Contains(aId));
        using(var d=JsonDocument.Parse(wire)){Check("m3-account-slot-preserved",d.RootElement.GetProperty("slot").GetInt32()==0&&d.RootElement.GetProperty("generation").GetString()=="1234abcd");}
        string pc=new PcSample(Cpu:0,Gpu:0,IntelGpu:34,IntelSharedGib:2,IntelSharedLimitGib:16,Fan1Rpm:null).Wire();
        using(var d=JsonDocument.Parse(pc)){var q=d.RootElement;Check("m3-dual-gpu-wire",q.GetProperty("intel_gpu").GetInt32()==34&&q.GetProperty("gpu").GetInt32()==0);Check("m3-no-fake-temp-or-fan",!q.TryGetProperty("intel_temp",out _)&&!q.TryGetProperty("fan1_rpm",out _));Check("m3-pc-version",q.GetProperty("type").GetString()=="opsdeck.pc.v1");}
        string temp=Path.Combine(Path.GetTempPath(),"alaz-m3-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try{
            var st=new LocalSettings(temp);st.SaveTokenForAccount(aId,"test-account-a-not-real-1234");st.SaveTokenForAccount(bId,"test-account-b-not-real-1234");
            Check("m3-token-a-isolated",st.ReadTokenForAccount(aId)=="test-account-a-not-real-1234");Check("m3-token-b-isolated",st.ReadTokenForAccount(bId)=="test-account-b-not-real-1234");
            Check("m3-billing-fallback-primary",st.ReadBillingTokenForAccountOrPrimary(aId)=="test-account-a-not-real-1234");
            st.SaveBillingTokenForAccount(aId,"test-billing-a-not-real-5678");
            Check("m3-billing-token-dedicated",st.ReadBillingTokenForAccount(aId)=="test-billing-a-not-real-5678"&&st.ReadBillingTokenForAccountOrPrimary(aId)=="test-billing-a-not-real-5678");
            Check("m3-billing-token-separate-file",st.HasBillingTokenForAccount(aId)&&st.ReadTokenForAccount(aId)=="test-account-a-not-real-1234"&&!st.HasBillingTokenForAccount(bId));
            st.Save(new HostConfig{Accounts=[p1,p2]});Check("m3-tokens-not-config",!File.ReadAllText(st.ConfigPath).Contains("not-real"));
            st.DeleteBillingTokenForAccount(aId);Check("m3-billing-delete-fallback",!st.HasBillingTokenForAccount(aId)&&st.ReadBillingTokenForAccountOrPrimary(aId)=="test-account-a-not-real-1234");
            st.DeleteTokenForAccount(aId);Check("m3-remove-one-preserves-other",!st.HasTokenForAccount(aId)&&st.HasTokenForAccount(bId));
            Throws("m3-secret-id-path-blocked",()=>st.ReadTokenForAccount("../x"));Throws("m3-billing-secret-id-path-blocked",()=>st.ReadBillingTokenForAccount("../x"));
            var legacy=new LocalSettings(Path.Combine(temp,"legacy"));legacy.SaveToken("legacy-test-not-real-987654");legacy.Save(new HostConfig{CloudflareAccountId=aId,CloudflareEnabled=true});
            var c=legacy.Load();Check("m3-legacy-config-migration",c.Accounts[0].AccountId==aId&&c.Accounts[0].Enabled&&!c.CloudflareEnabled&&c.SchemaVersion==3);
            Check("m3-legacy-secret-preserved",legacy.ReadTokenForAccount(aId)=="legacy-test-not-real-987654"&&legacy.HasToken);
            Check("m3-legacy-backup-exists",File.Exists(legacy.ConfigPath+".before-m3"));Check("m3-migration-idempotent",legacy.Load().Accounts.Length==c.Accounts.Length);
            var changed=c with{Accounts=[c.Accounts[0] with{AccountId=bId},c.Accounts[1]]};legacy.Save(changed);Check("m3-new-id-no-previous-secret",!legacy.HasTokenForAccount(bId));
        }finally{Directory.Delete(temp,true);}
        var ha=new AccountHandler(403);var hb=new AccountHandler(200);
        using(var ca=new CloudflareClient(aId,"test-token-a-read-only",ha))using(var cb=new CloudflareClient(bId,"test-token-b-read-only",hb)){
            try{await ca.R2(CancellationToken.None);Check("m3-a-denied",false);}catch(SourceFailure e){Check("m3-a-denied",e.State==SourceState.Denied);}
            var result=await cb.R2(CancellationToken.None);Check("m3-b-continues",result.State==SourceState.Ok&&result.Value==1);
            Check("m3-http-token-scope-a",ha.Token=="test-token-a-read-only"&&ha.Path!.Contains(aId));Check("m3-http-token-scope-b",hb.Token=="test-token-b-read-only"&&hb.Path!.Contains(bId));
        }
        using(var sampler=new PcSampler(new())){sampler.Sample();await Task.Delay(1100);var measured=sampler.Sample();Check("m3-real-intel-range",measured.IntelGpu is >=0 and <=100);Check("m3-real-nvidia-range",measured.Gpu is >=0 and <=100);Check("m3-real-separate-identities",measured.Gpus?.Any(g=>g.Identity.Vendor==0x8086)==true&&measured.Gpus.Any(g=>g.Identity.Vendor==0x10DE));}
    }
    private sealed class AccountHandler(int code):HttpMessageHandler
    {
        public string? Token,Path;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Token=request.Headers.Authorization?.Parameter;Path=request.RequestUri!.AbsolutePath;return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code){Content=new StringContent("{\"success\":true,\"result\":{\"standard\":{\"published\":{\"payloadSize\":1073741824,\"objects\":1}}}}")});}
    }
}