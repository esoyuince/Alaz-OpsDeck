using OpsDeck.Core;
namespace OpsDeck.Tests;
internal static class M42Tests
{
    public static void Run(Action<string,bool> Check,Action<string,Action> Throws)
    {
        const string a="11111111111111111111111111111111",b="22222222222222222222222222222222";
        CloudResource R(string account,ResourceKind kind,string id,string name)=>new(new(account,kind,"account",id),name);
        var aw=R(a,ResourceKind.Worker,"api","api");var bw=R(b,ResourceKind.Worker,"api","api");
        var ad=R(a,ResourceKind.D1,"database","Hayvan verisi");var br=R(b,ResourceKind.R2,"images","Images");
        var bp=R(b,ResourceKind.Pages,"site","Website");
        ResourceSet S(string account,ResourceKind kind,CloudResource[] rows,bool complete=true)=>
            new(account,account==a?"VetaKeep":"Other",kind,complete?SourceState.Ok:SourceState.Partial,rows,complete,DateTimeOffset.UtcNow,"");
        var inv=new ResourceInventory([S(a,ResourceKind.Worker,[aw]),S(b,ResourceKind.Worker,[bw]),
            S(a,ResourceKind.D1,[ad]),S(b,ResourceKind.R2,[br],false),S(b,ResourceKind.Pages,[bp])]);
        var map=new Dictionary<ResourceKey,string>{{aw.Key,"VetaKeep"},{ad.Key,"VetaKeep"},{bw.Key,"CaptainCalc"},{bp.Key,"Atanmamış kaynaklar"}};
        InventorySelection F(InventoryFilter f)=>InventoryFiltering.Apply(inv,map,f);
        Check("m42-all-rows",F(new()).Rows.Length==5);
        Check("m42-same-resource-name-isolated",F(new(a)).Rows.All(x=>x.Resource.Key.AccountId==a)&&F(new(a)).Rows.Length==2);
        Check("m42-kind-filter",F(new(Kind:ResourceKind.Worker)).Rows.Length==2);
        Check("m42-account-kind-intersection",F(new(b,ResourceKind.Worker)).Rows.Single().Resource.Key==bw.Key);
        Check("m42-project-exact",F(new(ProjectMode:ProjectFilterMode.Named,Project:"VetaKeep")).Rows.Length==2);
        Check("m42-unassigned-only",F(new(ProjectMode:ProjectFilterMode.Unassigned)).Rows.Single().Resource.Key==br.Key);
        Check("m42-label-is-not-sentinel",F(new(ProjectMode:ProjectFilterMode.Named,Project:"Atanmamış kaynaklar")).Rows.Single().Resource.Key==bp.Key);
        Check("m42-no-project-name-inference",F(new(ProjectMode:ProjectFilterMode.Named,Project:"api")).Rows.Length==0);
        Check("m42-search-case-insensitive",F(new(Search:"IMAGES")).Rows.Single().Resource.Key==br.Key);
        Check("m42-search-trim",F(new(Search:"  api  ")).Rows.Length==2);
        Check("m42-search-project-name",F(new(Search:"CaptainCalc")).Rows.Single().Resource.Key==bw.Key);
        Check("m42-all-filter-intersection",F(new(b,ResourceKind.Worker,ProjectFilterMode.Named,"CaptainCalc","API")).Rows.Single().Resource.Key==bw.Key);
        Check("m42-search-no-match-not-zero-resources",F(new(Search:"absent")).Rows.Length==0&&F(new(Search:"absent")).DiscoveredInScope==5);
        Check("m42-partial-coverage-visible",!F(new()).ScopeComplete&&F(new()).CompleteSourceCount==4);
        Check("m42-complete-account-subset",F(new(a)).ScopeComplete);
        Check("m42-unknown-account-not-complete",!F(new("missing")).ScopeComplete);
        Check("m42-empty-inventory-not-success",!InventoryFiltering.Apply(ResourceInventory.Empty,map,new()).ScopeComplete);
        var setup=new ResourceInventory([ResourceSet.Setup(new CloudAccountConfig{ProfileId="blank",Name="Blank"},ResourceKind.Worker)]);
        Check("m42-setup-not-zero-proof",!InventoryFiltering.Apply(setup,map,new()).ScopeComplete);
        Throws("m42-named-filter-requires-name",()=>F(new(ProjectMode:ProjectFilterMode.Named)));
        Throws("m42-invalid-kind",()=>F(new(Kind:(ResourceKind)99)));
        Throws("m42-invalid-project-mode",()=>F(new(ProjectMode:(ProjectFilterMode)99)));
        var options=InventoryFiltering.Projects(map,new(b,ResourceKind.Worker));
        Check("m42-project-options-scoped",options.SequenceEqual(new[]{"CaptainCalc"}));
        var before=map.ToArray();F(new(a));F(new(Search:"xx"));F(new());
        Check("m42-filter-does-not-delete-hidden-mappings",map.SequenceEqual(before));
        var reordered=new ResourceInventory(inv.Sets.Reverse().ToArray());
        Check("m42-stable-order",F(new()).Rows.Select(x=>x.Resource.Key).SequenceEqual(InventoryFiltering.Apply(reordered,map,new()).Rows.Select(x=>x.Resource.Key)));
        var duplicate=new ResourceInventory([S(a,ResourceKind.Worker,[aw,aw])]);
        Check("m42-duplicate-row-not-double-counted",InventoryFiltering.Apply(duplicate,map,new()).Rows.Length==1);
        try{InventoryFiltering.Apply(new([S(a,ResourceKind.Worker,[bw])]),map,new());Check("m42-wrong-account-row-rejected",false);}
        catch(InvalidDataException){Check("m42-wrong-account-row-rejected",true);}
        string dir=Path.Combine(Path.GetTempPath(),"alaz-m42-"+Guid.NewGuid().ToString("N"));
        try {
            var store=new ProjectMap(dir);var initial=store.Load();
            var saved=store.Save(map.Select(x=>new ProjectAssignment(x.Key,x.Value)),initial.Revision);
            map[aw.Key]="VetaKeep SaaS";
            store.Save(map.Select(x=>new ProjectAssignment(x.Key,x.Value)),saved.Revision);
            var loaded=store.Load();
            Check("m42-save-after-filter-preserves-other-account",loaded.Assignments.Single(x=>x.Key==bw.Key).Project=="CaptainCalc");
            Check("m42-edit-updates-project-filter",F(new(ProjectMode:ProjectFilterMode.Named,Project:"VetaKeep SaaS")).Rows.Single().Resource.Key==aw.Key);
            Check("m42-previous-project-not-deleted",F(new(ProjectMode:ProjectFilterMode.Named,Project:"VetaKeep")).Rows.Single().Resource.Key==ad.Key);
        }finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
}
