namespace OpsDeck.Core;

public enum ProjectFilterMode { All, Unassigned, Named }
public sealed record InventoryFilter(string? AccountId=null, ResourceKind? Kind=null,
    ProjectFilterMode ProjectMode=ProjectFilterMode.All, string? Project=null, string Search="")
{
    public bool Includes(ResourceSet set) =>
        (AccountId==null || string.Equals(AccountId,set.AccountId,StringComparison.OrdinalIgnoreCase)) &&
        (!Kind.HasValue || Kind==set.Kind);
}
public sealed record InventoryRow(ResourceSet Source,CloudResource Resource,string? Project);
public sealed record InventorySelection(InventoryRow[] Rows,int DiscoveredInScope,
    int SourceCount,int CompleteSourceCount)
{
    public bool ScopeComplete => SourceCount>0 && SourceCount==CompleteSourceCount;
}
public static class InventoryFiltering
{
    public static InventorySelection Apply(ResourceInventory inventory,
        IReadOnlyDictionary<ResourceKey,string> assignments,InventoryFilter filter)
    {
        if(!Enum.IsDefined(filter.ProjectMode) || (filter.Kind.HasValue && !Enum.IsDefined(filter.Kind.Value)))
            throw new ArgumentException("Invalid inventory filter.");
        if(filter.ProjectMode==ProjectFilterMode.Named && string.IsNullOrWhiteSpace(filter.Project))
            throw new ArgumentException("Named project filter needs a project.");
        var sources=inventory.Sets.Where(filter.Includes).ToArray();
        var rows=new List<InventoryRow>();var seen=new HashSet<ResourceKey>();int discovered=0;
        string search=filter.Search.Trim();
        foreach(var source in sources)foreach(var resource in source.Items)
        {
            if(resource.Key.AccountId!=source.AccountId || resource.Key.Kind!=source.Kind)
                throw new InvalidDataException("Resource does not belong to the reported account/type.");
            if(!seen.Add(resource.Key))continue;
            discovered++;
            assignments.TryGetValue(resource.Key,out string? project);
            if(string.IsNullOrWhiteSpace(project))project=null;
            if(filter.ProjectMode==ProjectFilterMode.Unassigned && project!=null)continue;
            if(filter.ProjectMode==ProjectFilterMode.Named && !string.Equals(project,filter.Project,StringComparison.Ordinal))continue;
            if(search.Length>0 && !string.Join(" ",source.ProfileName,resource.Name,
                resource.Key.Kind,resource.Key.Scope,resource.Key.Id,project??"")
                .Contains(search,StringComparison.OrdinalIgnoreCase))continue;
            rows.Add(new(source,resource,project));
        }
        var ordered=rows.OrderBy(x=>x.Source.ProfileName,StringComparer.OrdinalIgnoreCase)
            .ThenBy(x=>x.Source.AccountId,StringComparer.Ordinal).ThenBy(x=>x.Resource.Key.Kind)
            .ThenBy(x=>x.Resource.Name,StringComparer.OrdinalIgnoreCase)
            .ThenBy(x=>x.Resource.Key.Scope,StringComparer.Ordinal).ThenBy(x=>x.Resource.Key.Id,StringComparer.Ordinal).ToArray();
        return new(ordered,discovered,sources.Length,sources.Count(x=>x.Complete&&x.State==SourceState.Ok));
    }
    public static string[] Projects(IReadOnlyDictionary<ResourceKey,string> assignments,InventoryFilter filter) =>
        assignments.Where(x=>(filter.AccountId==null||string.Equals(x.Key.AccountId,filter.AccountId,StringComparison.OrdinalIgnoreCase))&&
            (!filter.Kind.HasValue||x.Key.Kind==filter.Kind)).Select(x=>x.Value).Where(x=>!string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x,StringComparer.Ordinal).ToArray();
}
