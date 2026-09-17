using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M416ProjectsTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m416-"+n,ok);
        const string A="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";var now=DateTimeOffset.Parse("2026-09-15T09:00:00Z");
        CloudResource R(ResourceKind k,string id,string name,string scope="account")=>new(new(A,k,scope,id),name);
        var w1=R(ResourceKind.Worker,"worker-1","API");var w2=R(ResourceKind.Worker,"worker-2","Jobs");
        var d1=R(ResourceKind.D1,"db-1","DB");var r2=R(ResourceKind.R2,"bucket-1","Files","default");var pages=R(ResourceKind.Pages,"pages-1","Site");
        ResourceSet S(ResourceKind k,params CloudResource[] rows)=>new(A,"Other Projects",k,SourceState.Ok,rows,true,now.AddSeconds(-30),"");
        var inv=new ResourceInventory([S(ResourceKind.Worker,w1,w2),S(ResourceKind.D1,d1),S(ResourceKind.R2,r2),S(ResourceKind.Pages,pages)]);
        var map=new Dictionary<ResourceKey,string>{{w1.Key,"Project A"},{d1.Key,"Project A"},{w2.Key,"Project B"},{pages.Key,"Project B"}};
        var profile=new CloudAccountConfig{ProfileId="other",Name="Other Projects",AccountId=A,Enabled=true};
        var projects=InventoryPaging.Build(inv,map,profile,new(0,0,0,"all",1),now);
        C("project-count",projects.TotalRows==3&&projects.KnownResources==5&&projects.CompleteSources==4);
        C("project-rows-are-inventory",projects.Rows.All(x=>x.Detail.Contains("res"))&&projects.Rows.All(x=>!x.Detail.Contains("health",StringComparison.OrdinalIgnoreCase)));
        var projectA=projects.Rows.Single(x=>x.Label=="Project A");
        var selected=InventoryPaging.Build(inv,map,profile,new(0,1,0,projectA.Key,2),now);
        C("selected-project-scope",selected.Scope=="Project A"&&selected.TotalRows==2&&selected.Rows.All(x=>x.Detail.Contains("Project A")));
        C("selected-project-only",selected.Rows.Select(x=>x.Label).Order().SequenceEqual(new[]{"API","DB"}.Order()));
        var all=InventoryPaging.Build(inv,map,profile,new(0,1,0,"all",3),now);
        C("all-resource-count",all.TotalRows==5&&all.KnownResources==5&&all.Scope=="All resources");
        C("unassigned-visible",projects.Rows.Any(x=>x.Label=="Unassigned"&&x.Detail.StartsWith("1 res")));
        C("inventory-age",projects.AgeS==30&&projects.State==SourceState.Ok);
    }
}
