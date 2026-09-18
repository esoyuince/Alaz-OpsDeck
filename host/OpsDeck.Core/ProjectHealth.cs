namespace OpsDeck.Core;

public enum ProjectHealthState { Unknown=0, Ok=1, Attention=2, Degraded=3 }
public sealed record ProjectResourceHealth(ResourceKey Key,ProjectHealthState Health,
    SourceState SourceState,DateTimeOffset? CollectedAt,string Basis);
public sealed record ProjectHealthSummary(string AccountId,string AccountName,string Project,bool Assigned,
    ProjectHealthState Health,int Resources,int EvidenceCount,int OkCount,int AttentionCount,int DegradedCount,int UnknownCount)
{
    public string Label=>ProjectHealthEngine.Label(Health);
}

public static class ProjectHealthEngine
{
    public static string Label(ProjectHealthState state)=>state switch{
        ProjectHealthState.Ok=>"OK",ProjectHealthState.Attention=>"ATTENTION",
        ProjectHealthState.Degraded=>"DEGRADED",_=>"UNKNOWN"};
    public static ProjectHealthState FromSource(SourceState state)=>state switch{
        SourceState.Ok=>ProjectHealthState.Ok,
        SourceState.Partial or SourceState.Stale=>ProjectHealthState.Attention,
        SourceState.Error or SourceState.Denied=>ProjectHealthState.Degraded,
        _=>ProjectHealthState.Unknown};
    public static bool SupportsResourceHealth(ResourceKey key)=>key.Kind is ResourceKind.Worker or ResourceKind.D1 or ResourceKind.R2;
    public static string EvidenceLabel(ResourceKey key,ProjectResourceHealth? evidence)
    {
        if(!SupportsResourceHealth(key))return "NO HEALTH DATA";
        if(evidence==null)return "PENDING";
        return evidence.SourceState switch{
            SourceState.Ok=>"OK",SourceState.NoData=>"NO DATA",SourceState.Stale=>"STALE",
            SourceState.Partial=>"ATTENTION",SourceState.Error or SourceState.Denied=>"ERROR",_=>"PENDING"};
    }
    public static string RollupLabel(IEnumerable<ResourceKey> resources,IReadOnlyDictionary<ResourceKey,ProjectResourceHealth>? evidence)
    {
        var keys=resources.Distinct().Where(SupportsResourceHealth).ToArray();
        if(keys.Length==0)return "NO HEALTH DATA";
        bool pending=false,noData=false,stale=false,attention=false,error=false;
        foreach(var key in keys)
        {
            if(evidence==null||!evidence.TryGetValue(key,out var found)){pending=true;continue;}
            switch(found.SourceState){
                case SourceState.Error:case SourceState.Denied:error=true;break;
                case SourceState.Partial:attention=true;break;
                case SourceState.Stale:stale=true;break;
                case SourceState.NoData:noData=true;break;
                case SourceState.Ok:break;
                default:pending=true;break;
            }
        }
        if(error)return "ERROR";if(attention)return "ATTENTION";if(stale)return "STALE";
        if(pending)return "PENDING";if(noData)return "NO DATA";return "OK";
    }
    public const int EvidenceTtlSeconds=180;
    public static ProjectResourceHealth Evidence(ResourceKey key,SourceState state,DateTimeOffset at,DateTimeOffset now,string basis)
    {
        key.Validate();if(at>now.AddSeconds(5))throw new InvalidDataException("Project health evidence timestamp is in the future.");
        var effective=Freshness.IsCurrent(at,now,EvidenceTtlSeconds)?state:SourceState.Stale;
        return new(key,FromSource(effective),effective,at,basis);
    }
    public static ProjectHealthSummary Rollup(string accountId,string accountName,string? project,
        IEnumerable<ResourceKey> resources,IReadOnlyDictionary<ResourceKey,ProjectResourceHealth>? evidence)
    {
        var keys=resources.Distinct().ToArray();foreach(var key in keys)key.Validate();
        var supported=keys.Where(SupportsResourceHealth).ToArray();
        int ok=0,attention=0,degraded=0,unknown=0,evidenceCount=0;
        foreach(var key in supported)
        {
            ProjectHealthState h=ProjectHealthState.Unknown;
            if(evidence!=null&&evidence.TryGetValue(key,out var found)){h=found.Health;evidenceCount++;}
            if(h==ProjectHealthState.Ok)ok++;else if(h==ProjectHealthState.Attention)attention++;
            else if(h==ProjectHealthState.Degraded)degraded++;else unknown++;
        }
        var health=degraded>0?ProjectHealthState.Degraded:attention>0?ProjectHealthState.Attention:
            unknown>0||supported.Length==0?ProjectHealthState.Unknown:ProjectHealthState.Ok;
        return new(accountId,accountName,project??"Unassigned",project!=null,health,keys.Length,evidenceCount,
            ok,attention,degraded,unknown);
    }
    public static ProjectHealthSummary[] Build(ResourceInventory inventory,
        IReadOnlyDictionary<ResourceKey,string>? mappings,IReadOnlyDictionary<ResourceKey,ProjectResourceHealth>? evidence)
    {
        if(mappings==null)return [];
        var rows=inventory.Sets.SelectMany(s=>s.Items.Select(r=>(Set:s,Resource:r))).ToArray();
        var seen=new HashSet<ResourceKey>();foreach(var row in rows){row.Resource.Key.Validate();if(!seen.Add(row.Resource.Key))throw new InvalidDataException("Duplicate resource identity in project health.");}
        return rows.GroupBy(x=>(x.Resource.Key.AccountId,Project:mappings.TryGetValue(x.Resource.Key,out var p)&&!string.IsNullOrWhiteSpace(p)?p:null))
            .Select(g=>Rollup(g.Key.AccountId,g.First().Set.ProfileName,g.Key.Project,g.Select(x=>x.Resource.Key),evidence))
            .OrderBy(x=>x.AccountName,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Project,StringComparer.OrdinalIgnoreCase)
            .ThenBy(x=>x.Project,StringComparer.Ordinal).ToArray();
    }
}
public sealed partial class AppEngine
{
    public IReadOnlyDictionary<ResourceKey,ProjectResourceHealth> ReadProjectResourceHealth(DateTimeOffset now)
    {
        var result=new Dictionary<ResourceKey,ProjectResourceHealth>();
        lock(gate)
        {
            void Put(ResourceKey key,SourceState state,DateTimeOffset at,string basis)
            {
                result[key]=ProjectHealthEngine.Evidence(key,state,at,now,basis);
            }
            foreach(var x in workerDetails.Values)Put(x.Value.Key,x.Value.State,x.Value.CollectedAt,"Worker resource analytics");
            foreach(var x in d1Details.Values)Put(x.Value.Key,x.Value.State,x.Value.CollectedAt,"D1 resource analytics");
            foreach(var x in r2Details.Values)Put(x.Value.Key,x.Value.State,x.Value.CollectedAt,"R2 resource analytics");
        }
        return result;
    }
    public ProjectHealthSummary[] ReadProjectHealth(DateTimeOffset now)
    {
        var mappings=Volatile.Read(ref panelMappings);if(mappings==null)return [];
        return ProjectHealthEngine.Build(Inventory,mappings,ReadProjectResourceHealth(now));
    }
}
