namespace OpsDeck.Core;

public sealed record CloudPanelSummary(
    SourceState InventoryState,int InventoryAgeS,int ResourceCount,int CompleteSources,
    int Workers,int D1,int R2,int Pages,
    SourceState QueueState,int QueueAgeS,int QueueCount,int QueueObserved,
    SourceState AiState,int AiAgeS,double? AiRequests,double? AiInputTokens,double? AiOutputTokens,
    SourceState GatewayState,int GatewayAgeS,double? GatewayRequests,double? GatewayErrors,double? GatewayCached)
{
    private static double Number(double? value)=>value.HasValue&&double.IsFinite(value.Value)&&value.Value>=0&&value.Value<=1e16?value.Value:-1;
    public object Wire()=>new{
        inventory_state=(int)InventoryState,inventory_age_s=InventoryAgeS,resource_count=ResourceCount,complete_sources=CompleteSources,
        workers=Workers,d1=D1,r2=R2,pages=Pages,
        queue_state=(int)QueueState,queue_age_s=QueueAgeS,queue_count=QueueCount,queue_observed=QueueObserved,
        ai_state=(int)AiState,ai_age_s=AiAgeS,ai_requests=Number(AiRequests),ai_input_tokens=Number(AiInputTokens),ai_output_tokens=Number(AiOutputTokens),
        gateway_state=(int)GatewayState,gateway_age_s=GatewayAgeS,gateway_requests=Number(GatewayRequests),gateway_errors=Number(GatewayErrors),gateway_cached=Number(GatewayCached)
    };
}

public sealed partial class AppEngine
{
    private CloudPanelSummary CloudSummary(CloudAccountConfig profile,DateTimeOffset now)
    {
        if(!profile.Enabled||string.IsNullOrEmpty(profile.AccountId))
            return new(SourceState.Setup,-1,-1,0,-1,-1,-1,-1,SourceState.Setup,-1,-1,-1,SourceState.Setup,-1,null,null,null,SourceState.Setup,-1,null,null,null);

        ResourceSet[] sets;QueueInventory? queues;QueueBacklog[] backlogs;AiHistory? ai;
        lock(gate)
        {
            sets=Inventory.Sets.Where(s=>string.Equals(s.AccountId,profile.AccountId,StringComparison.OrdinalIgnoreCase)).ToArray();
            queues=queueCache.TryGetValue(profile.AccountId+"/list",out var q)&&q.Value is QueueInventory qi?qi:null;
            backlogs=queueCache.Values.Select(x=>x.Value).OfType<QueueBacklog>().Where(x=>string.Equals(x.Queue.AccountId,profile.AccountId,StringComparison.OrdinalIgnoreCase)).ToArray();
            ai=analyticsHistoryCache.Values.Select(x=>x.Value).OfType<AiHistory>().Where(x=>string.Equals(x.AccountId,profile.AccountId,StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.CollectedAt).FirstOrDefault();
        }

        int Age(DateTimeOffset at)=>at==default?-1:(int)Math.Clamp(Math.Floor((now-at).TotalSeconds),0,int.MaxValue);
        SourceState Old(SourceState state,int age,int ttl)=>age>ttl&&state is SourceState.Ok or SourceState.Partial?SourceState.Stale:state;
        int complete=sets.Count(s=>s.Complete&&s.State==SourceState.Ok);var resources=sets.SelectMany(s=>s.Items).ToArray();
        int inventoryAge=sets.Length==0?-1:Age(sets.Min(s=>s.CollectedAt));
        SourceState inventoryState=sets.Length==0?SourceState.NoData:
            complete==4?SourceState.Ok:resources.Length>0?SourceState.Partial:
            sets.Any(s=>s.State==SourceState.Denied)?SourceState.Denied:
            sets.Any(s=>s.State==SourceState.Error)?SourceState.Error:SourceState.NoData;
        inventoryState=Old(inventoryState,inventoryAge,InventoryPaging.SourceTtlSeconds);
        int known=complete==4||resources.Length>0?resources.Length:-1;
        int Count(ResourceKind kind)=>known<0?-1:resources.Count(x=>x.Key.Kind==kind);

        int queueAge=queues==null?-1:Age(queues.CollectedAt);
        SourceState queueState=queues==null?SourceState.NoData:Old(queues.State,queueAge,InventoryPaging.SourceTtlSeconds);
        int queueCount=queues==null?-1:queues.Items.Length;
        int queueObserved=queues==null?-1:backlogs.Select(x=>x.Queue.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        int aiAge=ai==null?-1:Age(ai.CollectedAt);
        SourceState aiState=ai==null?SourceState.NoData:Old(ai.Inference.State,aiAge,InventoryPaging.SourceTtlSeconds);
        SourceState gatewayState=ai==null?SourceState.NoData:Old(ai.Gateway.State,aiAge,InventoryPaging.SourceTtlSeconds);
        static double? SumCount<T>(IEnumerable<T> rows,Func<T,double?> value)=>AnalyticsHistory.Sum(rows.Select(value));
        double? aiReq=ai==null?null:SumCount(ai.Inference.Rows,x=>x.Count);
        double? aiIn=ai==null?null:SumCount(ai.Inference.Rows,x=>x.InputTokens);
        double? aiOut=ai==null?null:SumCount(ai.Inference.Rows,x=>x.OutputTokens);
        double? gwReq=ai==null?null:SumCount(ai.Gateway.Rows,x=>x.Count);
        double? gwErr=ai==null?null:SumCount(ai.Gateway.Rows,x=>x.Errors);
        double? gwCached=ai==null?null:SumCount(ai.Gateway.Rows,x=>x.Cached);

        return new(inventoryState,inventoryAge,known,complete,Count(ResourceKind.Worker),Count(ResourceKind.D1),Count(ResourceKind.R2),Count(ResourceKind.Pages),
            queueState,queueAge,queueCount,queueObserved,aiState,aiAge,aiReq,aiIn,aiOut,gatewayState,aiAge,gwReq,gwErr,gwCached);
    }

    private string CloudWire(AccountState account,int slot,int total,DateTimeOffset now)=>
        account.Wire(slot,total,generation,now,CloudSummary(account.Profile,now));
}
