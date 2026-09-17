using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;

public static class M59ProjectHealthTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m59-"+n,ok);
        const string A="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";var now=DateTimeOffset.Parse("2026-09-15T16:00:00Z");
        ResourceKey K(ResourceKind k,string id,string scope="account")=>new(A,k,scope,id);
        var wk=K(ResourceKind.Worker,"worker-1");var dk=K(ResourceKind.D1,"db-1");
        var rk=K(ResourceKind.R2,"bucket-1","default");var pk=K(ResourceKind.Pages,"pages-1");
        ProjectResourceHealth E(ResourceKey k,SourceState state,int age=30)=>ProjectHealthEngine.Evidence(k,state,now.AddSeconds(-age),now,"test");
        C("source-ok",E(wk,SourceState.Ok).Health==ProjectHealthState.Ok);
        C("source-partial",E(wk,SourceState.Partial).Health==ProjectHealthState.Attention);
        C("source-error",E(wk,SourceState.Error).Health==ProjectHealthState.Degraded);
        C("source-nodata",E(wk,SourceState.NoData).Health==ProjectHealthState.Unknown);
        C("old-error-stale",E(wk,SourceState.Error,600).Health==ProjectHealthState.Attention&&E(wk,SourceState.Error,600).SourceState==SourceState.Stale);
        var ok=new Dictionary<ResourceKey,ProjectResourceHealth>{{wk,E(wk,SourceState.Ok)},{dk,E(dk,SourceState.Ok)}};
        C("rollup-ok",ProjectHealthEngine.Rollup(A,"Other","A",[wk,dk],ok).Health==ProjectHealthState.Ok);
        var unknown=new Dictionary<ResourceKey,ProjectResourceHealth>{{wk,E(wk,SourceState.Ok)}};
        C("unknown-prevents-ok",ProjectHealthEngine.Rollup(A,"Other","A",[wk,pk],unknown).Health==ProjectHealthState.Unknown);
        var att=new Dictionary<ResourceKey,ProjectResourceHealth>{{wk,E(wk,SourceState.Partial)}};
        C("attention-beats-unknown",ProjectHealthEngine.Rollup(A,"Other","A",[wk,pk],att).Health==ProjectHealthState.Attention);
        var deg=new Dictionary<ResourceKey,ProjectResourceHealth>{{wk,E(wk,SourceState.Error)},{dk,E(dk,SourceState.Partial)}};
        C("degraded-precedence",ProjectHealthEngine.Rollup(A,"Other","A",[wk,dk,pk],deg).Health==ProjectHealthState.Degraded);
        C("evidence-count",ProjectHealthEngine.Rollup(A,"Other","A",[wk,dk,pk],deg).EvidenceCount==2);
        CloudResource R(ResourceKey k,string name)=>new(k,name);
        ResourceSet S(ResourceKind k,params CloudResource[] rows)=>new(A,"Other Projects",k,SourceState.Ok,rows,true,now.AddSeconds(-30),"");
        var inv=new ResourceInventory([S(ResourceKind.Worker,R(wk,"API")),S(ResourceKind.D1,R(dk,"DB")),
            S(ResourceKind.R2,R(rk,"Files")),S(ResourceKind.Pages,R(pk,"Site"))]);
        var map=new Dictionary<ResourceKey,string>{{wk,"Project A"},{dk,"Project A"},{pk,"Project A"},{rk,"Project B"}};
        var evidence=new Dictionary<ResourceKey,ProjectResourceHealth>{{wk,E(wk,SourceState.Ok)},{dk,E(dk,SourceState.Partial)},{rk,E(rk,SourceState.Ok)}};
        var summaries=ProjectHealthEngine.Build(inv,map,evidence);
        var pa=summaries.Single(x=>x.Project=="Project A");
        C("build-project-a",pa.Health==ProjectHealthState.Attention&&pa.Resources==3&&pa.UnknownCount==1);
        C("build-project-b",summaries.Single(x=>x.Project=="Project B").Health==ProjectHealthState.Ok);
        C("map-unavailable",ProjectHealthEngine.Build(inv,null,evidence).Length==0);
        var profile=new CloudAccountConfig{ProfileId="other",Name="Other Projects",AccountId=A,Enabled=true};
        var legacy=InventoryPaging.Build(inv,map,profile,new(0,0,0,"all",1),now);
        C("legacy-detail-preserved",legacy.Rows.All(x=>!x.Detail.StartsWith("OK")&&!x.Detail.StartsWith("UNKNOWN"))&&legacy.ProjectHealth==0);
        var projects=InventoryPaging.Build(inv,map,profile,new(0,0,0,"all",2),now,evidence);
        var projectA=projects.Rows.Single(x=>x.Label=="Project A");
        C("project-row-health",projectA.Health==(int)ProjectHealthState.Attention&&projectA.Detail.StartsWith("ATTENTION |"));
        var selected=InventoryPaging.Build(inv,map,profile,new(0,1,0,projectA.Key,3),now,evidence);
        C("selected-health",selected.ProjectHealth==(int)ProjectHealthState.Attention&&selected.Rows.Length==3);
        C("pages-unknown",selected.Rows.Single(x=>x.Label=="Site").Health==(int)ProjectHealthState.Unknown);
        var all=InventoryPaging.Build(inv,map,profile,new(0,1,0,"all",4),now,evidence);
        C("resource-health",all.Rows.Single(x=>x.Label=="Files").Health==(int)ProjectHealthState.Ok&&all.Rows.Single(x=>x.Label=="Files").Detail.StartsWith("OK |"));
        string wire=projects.Wire("1234abcd");using var doc=JsonDocument.Parse(wire);
        C("wire-project-health",doc.RootElement.GetProperty("project_health").GetInt32()==0&&doc.RootElement.GetProperty("rows")[0].TryGetProperty("health",out _));
        C("wire-budget",System.Text.Encoding.UTF8.GetByteCount(wire)<=3000);
        try{ProjectHealthEngine.Evidence(wk,SourceState.Ok,now.AddMinutes(1),now,"future");C("future-rejected",false);}catch(InvalidDataException){C("future-rejected",true);}
    }
}
