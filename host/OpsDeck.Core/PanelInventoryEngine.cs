namespace OpsDeck.Core;
public sealed partial class AppEngine
{
    private IReadOnlyDictionary<ResourceKey,string>? panelMappings;
    public void ReloadProjectMappings()
    {
        try
        {
            var saved=new ProjectMap(settings.DirectoryPath).Load();
            Volatile.Write(ref panelMappings,saved.Assignments.ToDictionary(x=>x.Key,x=>x.Project));
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {Volatile.Write(ref panelMappings,null);log.Event("project_map_unavailable",new{kind=e.GetType().Name});}
    }
    private string InventoryFrame(PanelInventoryRequest query)
    {
        if(Locked)return new PanelInventoryPage(query.Slot,query.View,query.Page,0,0,0,-1,0,4,SourceState.NoData,-1,true,
            query.Group,"Windows session locked","Private",query.RequestId,[]).Wire(generation);
        var profiles=Accounts;
        if(query.Slot>=profiles.Length)
            return new PanelInventoryPage(query.Slot,query.View,query.Page,0,0,0,-1,0,4,SourceState.NoData,-1,false,
                query.Group,"Profile unavailable","No profile",query.RequestId,[]).Wire(generation);
        try{var now=DateTimeOffset.UtcNow;return InventoryPaging.Build(Inventory,Volatile.Read(ref panelMappings),profiles[query.Slot].Profile,query,now,ReadProjectResourceHealth(now)).Wire(generation);}
        catch(Exception e)when(e is ArgumentException or IOException or InvalidOperationException)
        {
            return new PanelInventoryPage(query.Slot,query.View,query.Page,0,0,0,-1,0,4,SourceState.Error,-1,false,
                query.Group,"Inventory validation failed",InventoryPaging.Label(profiles[query.Slot].Profile.Name,22),query.RequestId,[]).Wire(generation);
        }
    }
}
