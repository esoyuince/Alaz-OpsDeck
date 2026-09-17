using OpsDeck.Core;
using System.Text.Json;
namespace OpsDeck.Tests;
internal static class M43Tests
{
    private const string A="11111111111111111111111111111111",B="22222222222222222222222222222222";
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-09-14T00:00:00Z");
    private static CloudAccountConfig Profile(string id=A)=>new(){ProfileId="fixture",Name="TEST ONLY",AccountId=id,Enabled=true};
    private static CloudResource R(int n,string account=A,ResourceKind kind=ResourceKind.Worker,string scope="account")=>new(new(account,kind,scope,"resource-"+n),"Test resource "+n);
    private static ResourceInventory Inventory(CloudResource[] rows,string account=A)=>new(Enum.GetValues<ResourceKind>().Select(k=>
        new ResourceSet(account,"TEST ONLY",k,SourceState.Ok,rows.Where(r=>r.Key.Kind==k).ToArray(),true,Now,"TEST ONLY")).ToArray());
    public static void Run(Action<string,bool> Check,Action<string,Action> Throws)
    {
        var rows=Enumerable.Range(0,17).Select(i=>R(i)).ToArray();var inventory=Inventory(rows);
        var map=rows.ToDictionary(r=>r.Key,r=>"Project "+int.Parse(r.Key.Id.Split('-')[1])/3);
        PanelInventoryPage Build(PanelInventoryRequest? q=null,ResourceInventory? inv=null,IReadOnlyDictionary<ResourceKey,string>? mappings=null,CloudAccountConfig? profile=null,DateTimeOffset? now=null)=>
            InventoryPaging.Build(inv??inventory,mappings??map,profile??Profile(),q??new(),now??Now);
        var projects=Build();
        Check("m43-project-groups",projects.TotalRows==6&&projects.TotalPages==2&&projects.Rows.Length==4);
        Check("m43-resource-count-not-project-count",projects.KnownResources==17);
        Check("m43-source-coverage",projects.CompleteSources==4&&projects.State==SourceState.Ok);
        var last=Build(new(Page:1));Check("m43-last-project-page",last.Page==1&&last.Rows.Length==2);
        var clamped=Build(new(Page:1999));Check("m43-page-clamped-with-request-correlation",clamped.Page==1&&clamped.RequestedPage==1999);
        var resources=Build(new(View:1,Page:4));Check("m43-last-resource-page",resources.TotalPages==5&&resources.Rows.Length==1);
        string key=InventoryPaging.GroupKey("Project 0");var detail=Build(new(View:1,Group:key));
        Check("m43-project-resource-detail",detail.TotalRows==3&&detail.Scope=="Project 0");
        Check("m43-project-key-not-display-label",key!=InventoryPaging.Label("Project 0"));
        Check("m43-unassigned-not-name-sentinel",InventoryPaging.GroupKey(null)!=InventoryPaging.GroupKey("Unassigned"));
        Check("m43-removed-project-no-wrong-fallback",Build(new(View:1,Group:InventoryPaging.GroupKey("deleted"))).TotalRows==0);
        var second=Inventory(rows.Select((_,i)=>R(i,B)).ToArray(),B);
        var both=new ResourceInventory(inventory.Sets.Concat(second.Sets).ToArray());
        Check("m43-account-isolation",Build(inv:both).KnownResources==17);
        var secondPage=Build(inv:both,profile:Profile(B),q:new(Slot:1));
        Check("m43-second-account-no-mapping-leak",secondPage.TotalRows==1&&secondPage.Rows[0].Label=="Unassigned");
        var reversed=new ResourceInventory(inventory.Sets.Reverse().Select(s=>s with{Items=s.Items.Reverse().ToArray()}).ToArray());
        Check("m43-stable-paging-order",Build(inv:reversed,q:new(View:1)).Rows.SequenceEqual(Build(q:new(View:1)).Rows));
        var noData=Build(inv:ResourceInventory.Empty);Check("m43-not-discovered-unknown-count",noData.KnownResources==-1&&noData.TotalRows==0&&noData.State==SourceState.NoData);
        var setup=Build(profile:Profile() with{AccountId="",Enabled=false});Check("m43-disabled-account-not-zero",setup.KnownResources==-1&&setup.State==SourceState.Setup);
        Check("m43-empty-ids-never-merge",Build(profile:Profile() with{AccountId="",Enabled=false},inv:both).TotalRows==0);
        var empty=Build(inv:Inventory([]));Check("m43-proven-empty-count-zero",empty.KnownResources==0&&empty.State==SourceState.Ok);
        var partial=new ResourceInventory(inventory.Sets.Select(s=>s.Kind==ResourceKind.Worker?s with{Complete=false,State=SourceState.Partial}:s).ToArray());
        Check("m43-partial-retains-known-lower-bound",Build(inv:partial).KnownResources==17&&Build(inv:partial).State==SourceState.Partial);
        var denied=new ResourceInventory(Inventory([]).Sets.Select(s=>s with{Complete=false,State=SourceState.Denied}).ToArray());
        Check("m43-denied-not-zero",Build(inv:denied).KnownResources==-1&&Build(inv:denied).State==SourceState.Denied);
        Check("m43-source-age-not-transport-age",Build(now:Now.AddSeconds(901)).State==SourceState.Stale&&Build(now:Now.AddSeconds(901)).AgeS==901);
        Check("m43-ttl-boundary",Build(now:Now.AddSeconds(900)).State==SourceState.Ok);
        var noMap=InventoryPaging.Build(inventory,null,Profile(),new(),Now);
        Check("m43-map-read-failure-not-unassigned",!noMap.MapOk&&noMap.TotalRows==0&&noMap.State==SourceState.Error);
        var noMapResources=InventoryPaging.Build(inventory,null,Profile(),new(View:1),Now);
        Check("m43-resource-view-marks-map-unavailable",noMapResources.Rows.All(x=>x.Detail.Contains("Map unavailable")));
        var draft=new Dictionary<ResourceKey,string>(map);draft[rows[0].Key]="Uncommitted";
        Check("m43-snapshot-not-mutated-by-draft",Build().Rows.SequenceEqual(projects.Rows));
        Check("m43-label-width-explicit-clipping",InventoryPaging.Label(new string('x',70)).Length==48&&InventoryPaging.Label(new string('x',70)).EndsWith('~'));
        Check("m43-label-turkish-ascii",InventoryPaging.Label("Diğer Projeler")=="Diger Projeler");
        Check("m43-unrenderable-label-not-empty",InventoryPaging.Label("猫").Length>0);
        Check("m43-wire-under-uart-budget",System.Text.Encoding.UTF8.GetByteCount(projects.Wire("1234abcd",true))<3000);
        using(var doc=JsonDocument.Parse(projects.Wire("1234abcd",true)))Check("m43-test-frame-explicit",doc.RootElement.GetProperty("test").GetBoolean());
        using(var doc=JsonDocument.Parse(projects.Wire("1234abcd")))Check("m43-normal-frame-not-test",!doc.RootElement.GetProperty("test").GetBoolean());
        Throws("m43-invalid-generation",()=>projects.Wire("oops"));
        foreach(var q in new[]{new PanelInventoryRequest(Slot:2),new(View:2),new(Page:-1),new(Page:2000),new(Group:"erase"),new(RequestId:0),new(Group:key)})
            Throws("m43-invalid-query-"+q,()=>q.Validate());
        string request="I (123) opsdeck.ui: INVENTORY_REQUEST slot=1 view=1 page=12 group="+key+" request=7";
        Check("m43-parse-request",PanelInventoryRequest.TryParse(request,out var parsed)&&parsed==new PanelInventoryRequest(1,1,12,key,7));
        foreach(string invalid in new[]{request+" trailing",request.Replace("slot=1","slot=2"),request.Replace("request=7","request=2147483648"),request.Replace("page=12","page=2000"),request.Replace("group="+key,"group=../secret"),request.Replace("request=7","request=0")})
            Check("m43-reject-malformed-request",!PanelInventoryRequest.TryParse(invalid,out _));
        void Invalid(string name,Action work){try{work();Check(name,false);}catch(InvalidDataException){Check(name,true);}}
        Invalid("m43-cross-account-row-rejected",()=>Build(inv:new([inventory.Sets[0] with{Items=[R(1,B)]}])));
        Invalid("m43-duplicate-source-rejected",()=>Build(inv:new([inventory.Sets[0],inventory.Sets[0]])));
        Invalid("m43-duplicate-resource-rejected",()=>Build(inv:new([inventory.Sets[0] with{Items=[rows[0],rows[0]]}])));
        Invalid("m43-future-source-rejected",()=>Build(now:Now.AddMinutes(-1)));
        var large=Inventory(Enumerable.Range(0,1000).Select(i=>R(i)).ToArray());
        var largePage=Build(inv:large,q:new(View:1,Page:249));
        Check("m43-1000-resources-bounded-page",largePage.TotalRows==1000&&largePage.Rows.Length==4&&largePage.TotalPages==250);
        Check("m43-paged-wire-no-secret-or-api-command",!projects.Wire("1234abcd").Contains("token")&&!projects.Wire("1234abcd").Contains("https://"));
        string temp=Path.Combine(Path.GetTempPath(),"opsdeck-m43-engine-test-"+Guid.NewGuid().ToString("N"));
        var local=new LocalSettings(temp);var engine=new AppEngine(new HostConfig{Accounts=[Profile()]},local);
        try{
            var store=new ProjectMap(temp);var saved=store.Save([new(rows[0].Key,"SAVED PROJECT")],store.Load().Revision);
            engine.ReloadProjectMappings();
            const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            typeof(AppEngine).GetField("inventory",flags)!.SetValue(engine,inventory);
            string Frame()=>(string)typeof(AppEngine).GetMethod("InventoryFrame",flags)!.Invoke(engine,[new PanelInventoryRequest()])!;
            Check("m43-engine-uses-saved-project-map",Frame().Contains("SAVED PROJECT"));
            store.Save([new(rows[0].Key,"UPDATED PROJECT")],saved.Revision);engine.ReloadProjectMappings();
            Check("m43-engine-reloads-saved-map",Frame().Contains("UPDATED PROJECT")&&!Frame().Contains("SAVED PROJECT"));
            engine.Locked=true;using(var locked=JsonDocument.Parse(Frame()))
                Check("m43-session-lock-hides-projects",locked.RootElement.GetProperty("rows").GetArrayLength()==0&&!Frame().Contains("UPDATED PROJECT"));
            engine.Locked=false;Check("m43-unlock-restores-saved-view",Frame().Contains("UPDATED PROJECT"));
        }finally{engine.DisposeAsync().AsTask().GetAwaiter().GetResult();if(Directory.Exists(temp))Directory.Delete(temp,true);}

    }
    public static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        var rows=Enumerable.Range(0,9).Select(i=>R(i)).ToArray();
        var map=rows.ToDictionary(r=>r.Key,r=>"TEST Project "+r.Key.Id);
        var p=InventoryPaging.Build(Inventory(rows),map,Profile(),new(),Now);
        File.WriteAllText(Path.Combine(directory,"inventory-valid-test.json"),p.Wire("1234abcd",true));
        File.WriteAllText(Path.Combine(directory,"inventory-setup-test.json"),InventoryPaging.Build(ResourceInventory.Empty,map,Profile() with{Enabled=false},new(),Now).Wire("1234abcd",true));
    }
}
