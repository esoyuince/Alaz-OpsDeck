namespace OpsDeck.Core;

public sealed record PanelDetailsSnapshot(IReadOnlyDictionary<ResourceKey,string> Names,
    WorkerAnalytics[] Workers,D1Analytics[] D1,R2Analytics[] R2,QueueBacklog[] Queues,
    QueueHistory[] QueueHistory,AiHistory[] Ai)
{
    public static PanelDetailsSnapshot Empty=>new(new Dictionary<ResourceKey,string>(),[],[],[],[],[],[]);
}
public static class PanelDetailsProjection
{
    private static bool Same(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
    public static PanelDetailsSnapshot FilterProject(string account,string group,ResourceInventory inventory,
        IReadOnlyDictionary<ResourceKey,string>? mappings,PanelDetailsSnapshot snapshot)
    {
        if(group=="all")return snapshot;
        if(!System.Text.RegularExpressions.Regex.IsMatch(group,@"\A[a-f0-9]{16}\z"))throw new ArgumentException("Invalid project filter.");
        if(mappings==null)throw new InvalidOperationException("Project map unavailable.");
        var allowed=inventory.Items.Where(x=>Same(x.Key.AccountId,account))
            .Where(x=>{string? project=null;mappings.TryGetValue(x.Key,out project);if(string.IsNullOrWhiteSpace(project))project=null;return InventoryPaging.GroupKey(project)==group;})
            .Select(x=>x.Key).ToHashSet();
        return snapshot with{
            Names=snapshot.Names.Where(x=>allowed.Contains(x.Key)).ToDictionary(x=>x.Key,x=>x.Value),
            Workers=snapshot.Workers.Where(x=>allowed.Contains(x.Key)).ToArray(),
            D1=snapshot.D1.Where(x=>allowed.Contains(x.Key)).ToArray(),
            R2=snapshot.R2.Where(x=>allowed.Contains(x.Key)).ToArray(),
            Queues=[],QueueHistory=[],Ai=[]
        };
    }
    public static PanelDetailCard[] Cards(string account,int kind,PanelDetailsSnapshot s)
    {
        var cards=new List<PanelDetailCard>();
        string Name(ResourceKey key){key.Validate();return s.Names.TryGetValue(key,out var n)?n:key.Kind+" (read in Windows)";}
        void Add(string identity,string title,string scope,string note,SourceState state,DateTimeOffset at,string reason,params PanelDetailMetric[] rows)
        {
            foreach(var row in rows)PanelDetails.Number(row.Value);
            cards.Add(new(PanelDetails.Key(account.ToLowerInvariant()+"/"+kind+"/"+identity),title,scope,note,state,at,rows,reason));
        }
        string Id(ResourceKey k)=>System.Text.Json.JsonSerializer.Serialize(k,Json.Options);
        static PanelDetailMetric M(string name,string unit,double? value)=>new(name,unit,value);
        static double? Sum(IEnumerable<double?> values)=>AnalyticsHistory.Sum(values);
        static string Observed(int hours)=>$"Observed groups; {hours}/24 hours. Missing hours are not zero. Not billing.";
        static string Reason(SourceState state,string noData,string partial)=>state switch{
            SourceState.NoData=>noData,SourceState.Partial=>partial,
            SourceState.Denied=>"Source access denied; values were not replaced by zero",
            SourceState.Error=>"Source read failed; values were not replaced by zero",
            SourceState.Setup=>"Source is not configured",SourceState.Stale=>"Source cache is stale",_=>""};
        if(kind==0)foreach(var v in s.Workers.Where(x=>Same(x.Key.AccountId,account)))
        {
            if(v.Key.Kind!=ResourceKind.Worker)throw new ArgumentException("Worker cache identity.");
            string scope=PanelDetails.Window(v.Start,v.End),id=Id(v.Key),name=Name(v.Key);
            string reason=Reason(v.State,"No analytics row in the 5m source window","Timing fields incomplete; request/error counts may still be observed");
            Add(id+"/traffic",name,scope,"Worker requests/errors; not global site availability",v.State,v.CollectedAt,reason,
                M("Requests","count",v.Requests),M("Errors","count",v.Errors),M("Error ratio","%",v.ErrorPercent));
            Add(id+"/timing",name,scope,"Server quantiles; missing latency is not zero",v.State,v.CollectedAt,reason,
                M("CPU P50","ms",v.CpuP50Ms),M("CPU P99","ms",v.CpuP99Ms),M("Wall P50","ms",v.WallP50Ms),M("Wall P99","ms",v.WallP99Ms));
        }
        if(kind==1)foreach(var v in s.D1.Where(x=>Same(x.Key.AccountId,account)))
        {
            if(v.Key.Kind!=ResourceKind.D1)throw new ArgumentException("D1 cache identity.");
            string scope=PanelDetails.Window(v.Start,v.End),id=Id(v.Key),name=Name(v.Key);
            string noData=v.DatabaseSizeBytes.HasValue||v.TableCount.HasValue?"Metadata observed; no analytics row in the 24h source window":"No D1 analytics row in the 24h source window";
            string reason=Reason(v.State,noData,"Some D1 analytics fields are unavailable");
            Add(id+"/rows",name,scope,"D1 observed analytics; query counts are not billed row counts",v.State,v.CollectedAt,reason,
                M("Read queries","count",v.ReadQueries),M("Write queries","count",v.WriteQueries),M("Rows read","rows",v.RowsRead),M("Rows written","rows",v.RowsWritten));
            Add(id+"/size",name,scope,"DB size/tables: metadata at collection; response/P90: analytics window",v.State,v.CollectedAt,reason,
                M("Database size","bytes",v.DatabaseSizeBytes),M("Tables","count",v.TableCount),M("Response bytes","bytes",v.ResponseBytes),M("Query P90","ms",v.QueryP90Ms));
        }
        if(kind==2)foreach(var v in s.R2.Where(x=>Same(x.Key.AccountId,account)))
        {
            if(v.Key.Kind!=ResourceKind.R2)throw new ArgumentException("R2 cache identity.");
            string id=Id(v.Key),name=Name(v.Key);
            bool hasOps=v.TotalRequests.HasValue,hasStorage=v.StorageAt.HasValue;
            string partial=hasOps&&!hasStorage?"Operations observed; storage snapshot unavailable":!hasOps&&hasStorage?"Storage snapshot observed; operations unavailable":"R2 source only partially observed";
            string reason=Reason(v.State,"No operations rows or storage snapshot observed",partial);
            Add(id+"/operations",name,PanelDetails.Window(v.OperationsStart,v.OperationsEnd),"Default jurisdiction; operations, not a bill",v.State,v.CollectedAt,reason,
                M("Requests","count",v.TotalRequests),M("Successful","count",v.SuccessRequests),M("User errors","count",v.UserErrors),M("Internal errors","count",v.InternalErrors));
            if(v.StorageAt>v.CollectedAt.AddMinutes(5))throw new ArgumentException("R2 snapshot in future.");
            Add(id+"/storage",name,PanelDetails.Snapshot(v.StorageAt),"Default scope; snapshot date is separate from host collection age",v.State,v.CollectedAt,reason,
                M("Payload","bytes",v.PayloadBytes),M("Metadata","bytes",v.MetadataBytes),M("Objects","count",v.ObjectCount),M("Uploads","count",v.UploadCount));
        }
        if(kind==3)
        {
            foreach(var v in s.Queues.Where(x=>Same(x.Queue.AccountId,account)))
            {
                if(v.OldestAt>v.CollectedAt)throw new ArgumentException("Queue oldest time in future.");
                string reason=v.Queue.Jurisdiction.Length>0&&v.State==SourceState.NoData?"Restricted jurisdiction is not queried by this view":
                    Reason(v.State,"Backlog metrics unavailable; missing is not zero","Backlog metrics only partially observed");
                Add(v.Queue.Id+"/now",v.Queue.Name,PanelDetails.Snapshot(v.CollectedAt),"Approximate backlog; oldest age is measured at collection, not live",v.State,v.CollectedAt,reason,
                    M("Backlog messages","count",v.Messages),M("Backlog size","bytes",v.Bytes),M("Oldest at collection","s",v.OldestAgeSeconds));
            }
            foreach(var v in s.QueueHistory.Where(x=>Same(x.Queue.AccountId,account)))
            {
                v.Window.Validate();int hours=v.Operations.Rows.Select(x=>x.Hour).Distinct().Count();
                string opReason=Reason(v.Operations.State,"No queue operation groups in the 24h source window","Only part of the queue operation groups were observed");
                Add(v.Queue.Id+"/operations",v.Queue.Name,PanelDetails.Window(v.Window.Start,v.Window.End),Observed(hours),v.Operations.State,v.CollectedAt,opReason,
                    M("Read attempts observed","count",Sum(v.Operations.Rows.Where(x=>x.Action=="ReadMessage").Select(x=>(double?)x.Count))),
                    M("Writes observed","count",Sum(v.Operations.Rows.Where(x=>x.Action=="WriteMessage").Select(x=>(double?)x.Count))),
                    M("Billable ops observed","count",Sum(v.Operations.Rows.Select(x=>x.BillableOperations))));
                var consumerValues=v.Consumers.Rows.Where(x=>x.Concurrency.HasValue).Select(x=>x.Concurrency!.Value).ToArray();
                if(consumerValues.Length>0){string consumerReason=Reason(v.Consumers.State,"No consumer concurrency groups in the 24h source window","Only part of the consumer groups were observed");
                    Add(v.Queue.Id+"/consumers",v.Queue.Name,PanelDetails.Window(v.Window.Start,v.Window.End),
                    "Consumer concurrency groups observed; missing hours are not zero",v.Consumers.State,v.CollectedAt,consumerReason,
                    M("Observed hours","hours",v.Consumers.Rows.Select(x=>x.Hour).Distinct().Count()),
                    M("Avg concurrency","count",consumerValues.Average()),M("Peak concurrency","count",consumerValues.Max()));}
            }
        }
        if(kind is 4 or 5)foreach(var v in s.Ai.Where(x=>Same(x.AccountId,account)))
        {
            v.Window.Validate();string scope=PanelDetails.Window(v.Window.Start,v.Window.End);
            if(kind==4)
            {
                var rows=v.Inference.Rows;string note=Observed(rows.Select(x=>x.Hour).Distinct().Count());
                string reason=Reason(v.Inference.State,"No Workers AI groups in the 24h source window","Only part of the Workers AI groups were observed");
                Add("inference","Workers AI",scope,note,v.Inference.State,v.CollectedAt,reason,
                    M("Requests observed","count",Sum(rows.Select(x=>(double?)x.Count))),M("Input tokens observed","tokens",Sum(rows.Select(x=>x.InputTokens))),
                    M("Output tokens observed","tokens",Sum(rows.Select(x=>x.OutputTokens))),M("Neurons observed","neurons",Sum(rows.Select(x=>x.Neurons))));
            }
            else
            {
                var rows=v.Gateway.Rows;string note=Observed(rows.Select(x=>x.Hour).Distinct().Count());
                string reason=Reason(v.Gateway.State,"No AI Gateway groups in the 24h source window","Only part of the AI Gateway groups were observed");
                Add("gateway/requests","AI Gateway (separate)",scope,note,v.Gateway.State,v.CollectedAt,reason,
                    M("Gateway requests observed","count",Sum(rows.Select(x=>(double?)x.Count))),M("Errors observed","count",Sum(rows.Select(x=>x.Errors))),
                    M("Cached observed","count",Sum(rows.Select(x=>x.Cached))),M("Rate limited observed","count",Sum(rows.Select(x=>(double?)(x.RateLimited?x.Count:0)))));
                Add("gateway/tokens","AI Gateway (separate)",scope,"Gateway tokens are NOT Workers AI totals; no inference or prompt read",v.Gateway.State,v.CollectedAt,reason,
                    M("Input tokens observed","tokens",Sum(rows.Select(x=>x.InputTokens))),M("Output tokens observed","tokens",Sum(rows.Select(x=>x.OutputTokens))));
            }
        }
        return cards.ToArray();
    }
}
public sealed partial class AppEngine
{
    // No collect method, token access, or network call is permitted on this path.
    public PanelDetailPage ReadPanelDetails(PanelDetailsRequest query,DateTimeOffset now)
    {
        query.Validate();
        PanelDetailPage Private()=>PanelDetails.Empty(query,"Private","Windows session locked");
        if(Locked)return Private();
        var profiles=Accounts;
        if(query.Slot>=profiles.Length)return PanelDetails.Empty(query,"No profile","Profile unavailable",SourceState.Setup);
        var profile=profiles[query.Slot].Profile;
        if(!profile.Enabled||string.IsNullOrEmpty(profile.AccountId))return PanelDetails.Empty(query,profile.Name,"Connect account in Windows",SourceState.Setup);
        try
        {
            PanelDetailsSnapshot snapshot;
            lock(gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
                snapshot=new(Inventory.Items.ToDictionary(x=>x.Key,x=>x.Name),workerDetails.Values.Select(x=>x.Value).ToArray(),
                    d1Details.Values.Select(x=>x.Value).ToArray(),r2Details.Values.Select(x=>x.Value).ToArray(),
                    queueCache.Values.Select(x=>x.Value).OfType<QueueBacklog>().ToArray(),
                    analyticsHistoryCache.Values.Select(x=>x.Value).OfType<QueueHistory>().ToArray(),
                    analyticsHistoryCache.Values.Select(x=>x.Value).OfType<AiHistory>().ToArray());
            }
            if(query.Group!="all")
            {
                try{snapshot=PanelDetailsProjection.FilterProject(profile.AccountId,query.Group,Inventory,Volatile.Read(ref panelMappings),snapshot);}
                catch(InvalidOperationException){return PanelDetails.Empty(query,profile.Name,"Project map unavailable; filter was not widened",SourceState.Error);}
            }
            var page=PanelDetails.Build(query,profile.Name,PanelDetailsProjection.Cards(profile.AccountId,query.Kind,snapshot),now);
            _=page.Wire(generation); // Validate before publishing; catches invalid cached values.
            return Locked?Private():page;
        }
        catch(Exception e)when(e is ArgumentException or InvalidOperationException or SourceFailure or System.Text.Json.JsonException)
        {return Locked?Private():PanelDetails.Empty(query,profile.Name,"Cached display validation failed; source values were not replaced by zero",SourceState.Error);}
    }
}
