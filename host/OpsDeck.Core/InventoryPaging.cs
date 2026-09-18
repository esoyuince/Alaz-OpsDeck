using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;

// This is a local display request, not a Cloudflare API request or command.
public sealed record PanelInventoryRequest(int Slot=0,int View=0,int Page=0,string Group="all",int RequestId=1)
{
    public void Validate()
    {
        if(Slot is <0 or >1 || View is <0 or >1 || Page is <0 or >1999 || RequestId<1 ||
            !Regex.IsMatch(Group,"^(all|[a-f0-9]{16})$") || (View==0&&Group!="all"))
            throw new ArgumentException("Invalid panel inventory request.");
    }
    public static bool TryParse(string line,out PanelInventoryRequest? request)
    {
        request=null;
        var m=Regex.Match(line,@"opsdeck\.ui: INVENTORY_REQUEST slot=([01]) view=([01]) page=([0-9]{1,4}) group=(all|[a-f0-9]{16}) request=([0-9]{1,10})$");
        if(!m.Success || !int.TryParse(m.Groups[5].Value,out int id))return false;
        var q=new PanelInventoryRequest(int.Parse(m.Groups[1].Value),int.Parse(m.Groups[2].Value),int.Parse(m.Groups[3].Value),m.Groups[4].Value,id);
        try{q.Validate();request=q;return true;}catch(ArgumentException){return false;}
    }
}
public sealed record PanelInventoryRow(string Key,string Label,string Detail,int Health=0);
public sealed record PanelInventoryPage(int Slot,int View,int RequestedPage,int Page,int TotalPages,int TotalRows,
    int KnownResources,int CompleteSources,int SourceCount,SourceState State,int AgeS,bool MapOk,
    string Group,string Scope,string AccountName,int RequestId,PanelInventoryRow[] Rows,int ProjectHealth=0,string ProjectHealthLabel="")
{
    public string Wire(string generation,bool test=false)
    {
        if(!Regex.IsMatch(generation,"^[a-f0-9]{8}$"))throw new ArgumentException("Invalid generation.");
        if(ProjectHealth is <0 or >3||Rows.Any(x=>x.Health is <0 or >3))throw new ArgumentException("Invalid project health field.");
        if(ProjectHealthLabel.Length>20||ProjectHealthLabel.Any(c=>c<32||c>126))throw new ArgumentException("Invalid project health label.");
        var text=JsonSerializer.Serialize(new{type="opsdeck.inventory.v1",generation,test,slot=Slot,view=View,
            requested_page=RequestedPage,page=Page,total_pages=TotalPages,total_rows=TotalRows,
            known_resources=KnownResources,complete_sources=CompleteSources,source_count=SourceCount,
            state=(int)State,age_s=AgeS,map_ok=MapOk,project_health=ProjectHealth,
            project_health_label=string.IsNullOrEmpty(ProjectHealthLabel)?null:ProjectHealthLabel,
            group=Group,scope=Scope,account_name=AccountName,
            request_id=RequestId,rows=Rows},Json.Options);
        if(Encoding.UTF8.GetByteCount(text)>3000)throw new InvalidOperationException("Inventory frame exceeds UART budget.");
        return text;
    }
}
public static class InventoryPaging
{
    public const int PageSize=4, SourceTtlSeconds=900;
    public static string Label(string value,int max=48)
    {
        string plain=value.Replace('ı','i').Replace('İ','I').Replace('ş','s').Replace('Ş','S')
            .Replace('ğ','g').Replace('Ğ','G').Normalize(NormalizationForm.FormD);
        string ascii=new(plain.Where(c=>c>=32&&c<=126).ToArray());
        if(string.IsNullOrWhiteSpace(ascii))ascii="(see Windows name)";
        return ascii.Length<=max?ascii:ascii[..(max-1)]+"~";
    }
    public static string GroupKey(string? project)=>Hash(project==null?"unassigned":"named\0"+project);
    private static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    public static PanelInventoryPage Build(ResourceInventory inventory,IReadOnlyDictionary<ResourceKey,string>? mappings,
        CloudAccountConfig profile,PanelInventoryRequest request,DateTimeOffset now,
        IReadOnlyDictionary<ResourceKey,ProjectResourceHealth>? healthEvidence=null)
    {
        request.Validate();profile.Validate();
        string accountName=Label(profile.Name,22),scope=request.View==0?"Projects":"All resources";
        bool mapOk=mappings!=null;int known=-1,complete=0,age=-1,projectHealth=0;string projectHealthLabel="";SourceState state;
        var resultRows=new List<PanelInventoryRow>();
        PanelInventoryPage Result()
        {
            int total=resultRows.Count,pages=(total+PageSize-1)/PageSize;
            int page=pages==0?0:Math.Min(request.Page,pages-1);
            return new(request.Slot,request.View,request.Page,page,pages,total,known,complete,4,state,age,mapOk,
                request.Group,Label(scope),accountName,request.RequestId,resultRows.Skip(page*PageSize).Take(PageSize).ToArray(),projectHealth,projectHealthLabel);
        }
        if(!profile.Enabled||profile.AccountId.Length==0){state=SourceState.Setup;scope="Connect account in Windows";return Result();}
        var sets=inventory.Sets.Where(s=>string.Equals(s.AccountId,profile.AccountId,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(sets.Length==0){state=SourceState.NoData;scope="Discover resources in Windows";return Result();}
        if(sets.Length>4||sets.Select(s=>s.Kind).Distinct().Count()!=sets.Length || sets.Any(s=>!Enum.IsDefined(s.Kind)))
            throw new InvalidDataException("Ambiguous inventory sources.");
        complete=sets.Count(s=>s.Complete&&s.State==SourceState.Ok);
        var observed=sets.Where(s=>s.State!=SourceState.Setup).ToArray();
        if(observed.Length>0)age=(int)Math.Clamp(Math.Floor((now-observed.Min(s=>s.CollectedAt)).TotalSeconds),0,int.MaxValue);
        if(observed.Any(s=>s.CollectedAt>now.AddSeconds(5)))throw new InvalidDataException("Inventory timestamp is in the future.");
        var rows=new List<(CloudResource Resource,string? Project)>();var seen=new HashSet<ResourceKey>();
        foreach(var set in sets)foreach(var resource in set.Items)
        {
            resource.Key.Validate();
            if(!string.Equals(resource.Key.AccountId,profile.AccountId,StringComparison.OrdinalIgnoreCase)||resource.Key.Kind!=set.Kind)
                throw new InvalidDataException("Resource crosses account/type boundary.");
            if(!seen.Add(resource.Key))throw new InvalidDataException("Duplicate resource identity.");
            string? project=null;mappings?.TryGetValue(resource.Key,out project);
            rows.Add((resource,string.IsNullOrWhiteSpace(project)?null:project));
        }
        if(rows.Count>4000)throw new InvalidDataException("Inventory display budget exceeded.");
        known=complete==4||rows.Count>0?rows.Count:-1;
        state=complete==4?SourceState.Ok:rows.Count>0?SourceState.Partial:
            sets.Any(s=>s.State==SourceState.Denied)?SourceState.Denied:
            sets.Any(s=>s.State==SourceState.Error)?SourceState.Error:SourceState.NoData;
        if(age>SourceTtlSeconds&&known>=0)state=SourceState.Stale;
        if(!mapOk && (request.View==0||request.Group!="all"))
        {state=SourceState.Error;scope="Project map unavailable";return Result();}
        var groups=rows.GroupBy(x=>x.Project,StringComparer.Ordinal)
            .Select(g=>new{Project=g.Key,Key=GroupKey(g.Key),Rows=g.ToArray()})
            .OrderBy(g=>g.Project??"",StringComparer.OrdinalIgnoreCase).ThenBy(g=>g.Project??"",StringComparer.Ordinal).ToArray();
        if(groups.Select(g=>g.Key).Distinct().Count()!=groups.Length)throw new InvalidDataException("Ambiguous project identity.");
        if(request.View==0)
        {
            foreach(var g in groups)
            {
                if(healthEvidence==null)resultRows.Add(new(g.Key,Label(g.Project??"Unassigned"),
                    $"{g.Rows.Length} res | W:{g.Rows.Count(x=>x.Resource.Key.Kind==ResourceKind.Worker)} D:{g.Rows.Count(x=>x.Resource.Key.Kind==ResourceKind.D1)} R:{g.Rows.Count(x=>x.Resource.Key.Kind==ResourceKind.R2)} P:{g.Rows.Count(x=>x.Resource.Key.Kind==ResourceKind.Pages)}"));
                else{var keys=g.Rows.Select(x=>x.Resource.Key).ToArray();var health=ProjectHealthEngine.Rollup(profile.AccountId,profile.Name,g.Project,keys,healthEvidence);
                    int supported=health.OkCount+health.AttentionCount+health.DegradedCount+health.UnknownCount;string display=ProjectHealthEngine.RollupLabel(keys,healthEvidence);
                    resultRows.Add(new(g.Key,Label(g.Project??"Unassigned"),
                    $"{display} | {g.Rows.Length} res | obs {health.EvidenceCount}/{supported} | W:{g.Rows.Count(x=>x.Resource.Key.Kind==ResourceKind.Worker)} D:{g.Rows.Count(x=>x.Resource.Key.Kind==ResourceKind.D1)} R:{g.Rows.Count(x=>x.Resource.Key.Kind==ResourceKind.R2)} P:{g.Rows.Count(x=>x.Resource.Key.Kind==ResourceKind.Pages)}",(int)health.Health));}
            }
        }
        else
        {
            var selected=rows.AsEnumerable();
            if(request.Group!="all")
            {
                var group=groups.SingleOrDefault(g=>g.Key==request.Group);
                if(group==null){scope="Project absent in this snapshot";state=SourceState.NoData;return Result();}
                selected=group.Rows;scope=group.Project??"Unassigned";
                if(healthEvidence!=null){var keys=group.Rows.Select(x=>x.Resource.Key).ToArray();projectHealth=(int)ProjectHealthEngine.Rollup(profile.AccountId,profile.Name,group.Project,keys,healthEvidence).Health;projectHealthLabel=ProjectHealthEngine.RollupLabel(keys,healthEvidence);}
            }
            foreach(var row in selected.OrderBy(x=>x.Resource.Key.Kind).ThenBy(x=>x.Resource.Name,StringComparer.OrdinalIgnoreCase)
                .ThenBy(x=>x.Resource.Key.Scope,StringComparer.Ordinal).ThenBy(x=>x.Resource.Key.Id,StringComparer.Ordinal))
            {
                string project=mapOk?(row.Project??"Unassigned"):"Map unavailable";
                ProjectResourceHealth? healthRow=null;if(healthEvidence!=null)healthEvidence.TryGetValue(row.Resource.Key,out healthRow);
                int health=healthRow==null?0:(int)healthRow.Health;
                string detail=healthEvidence==null?$"{row.Resource.Key.Kind}/{row.Resource.Key.Scope} | {project}":$"{ProjectHealthEngine.EvidenceLabel(row.Resource.Key,healthRow)} | {row.Resource.Key.Kind}/{row.Resource.Key.Scope} | {project}";
                resultRows.Add(new(Hash(JsonSerializer.Serialize(row.Resource.Key,Json.Options)),Label(row.Resource.Name),Label(detail,96),health));
            }
        }
        return Result();
    }
}
