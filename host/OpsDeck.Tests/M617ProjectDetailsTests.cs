using System.Text.Json;
using OpsDeck.Core;

namespace OpsDeck.Tests;

public static class M617ProjectDetailsTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m617-project-details-"+n,ok);
        void Reject(string n,Action a){try{a();C(n,false);}catch(Exception e)when(e is ArgumentException or InvalidOperationException){C(n,true);}}
        const string A="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",B="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var now=DateTimeOffset.Parse("2026-09-18T15:00:00Z");
        CloudResource R(string account,ResourceKind kind,string id,string name,string scope="account")=>new(new(account,kind,scope,id),name);
        var wA=R(A,ResourceKind.Worker,"worker-a","API");
        var wU=R(A,ResourceKind.Worker,"worker-u","Unassigned worker");
        var dA=R(A,ResourceKind.D1,"db-a","DB");
        var rB=R(A,ResourceKind.R2,"bucket-b","Bucket","default");
        var pA=R(A,ResourceKind.Pages,"pages-a","Site");
        var wB=R(B,ResourceKind.Worker,"worker-b","Other account worker");
        ResourceSet S(string account,string name,ResourceKind kind,params CloudResource[] rows)=>new(account,name,kind,SourceState.Ok,rows,true,now.AddSeconds(-20),"");
        var inventory=new ResourceInventory([
            S(A,"A",ResourceKind.Worker,wA,wU),S(A,"A",ResourceKind.D1,dA),S(A,"A",ResourceKind.R2,rB),S(A,"A",ResourceKind.Pages,pA),
            S(B,"B",ResourceKind.Worker,wB)
        ]);
        var map=new Dictionary<ResourceKey,string>{{wA.Key,"Project A"},{dA.Key,"Project A"},{rB.Key,"Project B"},{pA.Key,"Project A"},{wB.Key,"Project A"}};
        var window=AnalyticsWindow.Last24Hours(now);
        var snapshot=new PanelDetailsSnapshot(
            new Dictionary<ResourceKey,string>{{wA.Key,wA.Name},{wU.Key,wU.Name},{dA.Key,dA.Name},{rB.Key,rB.Name},{wB.Key,wB.Name}},
            [
                new(wA.Key,SourceState.Ok,now.AddSeconds(-10),now.AddMinutes(-8),now.AddMinutes(-3),10,1,2,3,4,5),
                new(wU.Key,SourceState.Ok,now.AddSeconds(-10),now.AddMinutes(-8),now.AddMinutes(-3),20,2,2,3,4,5),
                new(wB.Key,SourceState.Ok,now.AddSeconds(-10),now.AddMinutes(-8),now.AddMinutes(-3),99,9,2,3,4,5)
            ],
            [new(dA.Key,SourceState.NoData,now.AddSeconds(-10),now.AddHours(-24),now,DatabaseSizeBytes:100,TableCount:2)],
            [new(rB.Key,SourceState.Partial,now.AddSeconds(-10),now.AddHours(-24),now,PayloadBytes:123,ObjectCount:2,StorageAt:now.AddDays(-1))],
            [new(new QueueInfo(A,new string('c',32),"Queue"),SourceState.Ok,now.AddSeconds(-10),1,2)],
            [],
            [new(A,window,now.AddSeconds(-10),new(SourceState.Ok,[]),new(SourceState.Ok,[]))]
        );

        string projectA=InventoryPaging.GroupKey("Project A"),unassigned=InventoryPaging.GroupKey(null);
        var named=PanelDetailsProjection.FilterProject(A,projectA,inventory,map,snapshot);
        C("named-worker",named.Workers.Length==1&&named.Workers[0].Key==wA.Key);
        C("named-d1",named.D1.Length==1&&named.D1[0].Key==dA.Key);
        C("named-r2-excluded",named.R2.Length==0);
        C("named-cross-account-excluded",named.Workers.All(x=>x.Key.AccountId==A));
        C("account-wide-categories-cleared",named.Queues.Length==0&&named.QueueHistory.Length==0&&named.Ai.Length==0);
        C("names-filtered",named.Names.Keys.OrderBy(x=>x.Id).SequenceEqual(new[]{wA.Key,dA.Key}.OrderBy(x=>x.Id)));

        var ua=PanelDetailsProjection.FilterProject(A,unassigned,inventory,map,snapshot);
        C("unassigned-only",ua.Workers.Length==1&&ua.Workers[0].Key==wU.Key&&ua.D1.Length==0&&ua.R2.Length==0);
        C("all-preserved",ReferenceEquals(PanelDetailsProjection.FilterProject(A,"all",inventory,map,snapshot),snapshot));
        Reject("missing-map-rejected",()=>PanelDetailsProjection.FilterProject(A,projectA,inventory,null,snapshot));
        Reject("bad-group-rejected",()=>PanelDetailsProjection.FilterProject(A,"bad",inventory,map,snapshot));

        C("legacy-request",PanelDetailsRequest.TryParse("opsdeck.ui: DETAILS_REQUEST slot=0 kind=1 page=2 request=3",out var legacy)&&legacy==new PanelDetailsRequest(0,1,2,3,"all"));
        C("filtered-request",PanelDetailsRequest.TryParse($"I (1) opsdeck.ui: DETAILS_REQUEST slot=0 kind=2 page=0 group={projectA} request=4",out var filtered)&&filtered==new PanelDetailsRequest(0,2,0,4,projectA));
        C("filtered-queue-rejected",!PanelDetailsRequest.TryParse($"opsdeck.ui: DETAILS_REQUEST slot=0 kind=3 page=0 group={projectA} request=4",out _));
        Reject("filtered-kind-validation",()=>new PanelDetailsRequest(0,4,0,1,projectA).Validate());

        var profile=new CloudAccountConfig{ProfileId="a",Name="A",AccountId=A,Enabled=true};
        var projects=InventoryPaging.Build(inventory,map,profile,new(0,0,0,"all",1),now);
        C("project-rows-no-kind",projects.Rows.All(x=>x.Kind==null));
        var selected=InventoryPaging.Build(inventory,map,profile,new(0,1,0,projectA,2),now);
        C("resource-kind-present",selected.Rows.Any(x=>x.Label=="API"&&x.Kind==(int)ResourceKind.Worker)&&selected.Rows.Any(x=>x.Label=="DB"&&x.Kind==(int)ResourceKind.D1));
        using var rowWire=JsonDocument.Parse(selected.Wire("12345678"));
        C("kind-on-wire",rowWire.RootElement.GetProperty("rows").EnumerateArray().All(x=>x.TryGetProperty("kind",out var k)&&k.ValueKind==JsonValueKind.Number));

        var page=PanelDetails.Empty(new(0,0,0,7,projectA),"A","No project card");
        using var wire=JsonDocument.Parse(page.Wire("12345678"));
        C("group-echo",wire.RootElement.GetProperty("group").GetString()==projectA);
    }
}
