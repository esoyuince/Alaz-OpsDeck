namespace OpsDeck.Core;

public static class PanelDetailsWarmPolicy
{
    public const int InitialDelayMs=3000;
    public const int RefreshSeconds=60;
    public const int InterReadDelayMs=150;
    public const int MaxResourcesPerKind=32;
    public const int MaxQueuesPerAccount=32;

    public static ResourceKey[] SelectResources(ResourceInventory inventory)=>inventory.Items.Select(x=>x.Key)
        .Where(k=>k.Kind is ResourceKind.Worker or ResourceKind.D1 or ResourceKind.R2)
        .GroupBy(k=>(Account:k.AccountId.ToLowerInvariant(),k.Kind))
        .OrderBy(g=>g.Key.Kind).ThenBy(g=>g.Key.Account,StringComparer.Ordinal)
        .SelectMany(g=>g.OrderBy(k=>k.Id,StringComparer.Ordinal).Take(MaxResourcesPerKind)).ToArray();

    public static ResourceKey[] FirstPass(ResourceInventory inventory)=>SelectResources(inventory)
        .GroupBy(k=>(Account:k.AccountId.ToLowerInvariant(),k.Kind))
        .OrderBy(g=>g.Key.Kind).ThenBy(g=>g.Key.Account,StringComparer.Ordinal)
        .SelectMany(g=>g.Take(4)).ToArray();

    public static ResourceKey[] Remaining(ResourceInventory inventory)
    {
        var first=FirstPass(inventory).ToHashSet();
        return SelectResources(inventory).Where(k=>!first.Contains(k)).ToArray();
    }
}
public sealed partial class AppEngine
{
    private static bool WarmRecoverable(Exception e)=>e is SourceFailure or IOException or UnauthorizedAccessException
        or System.Security.Cryptography.CryptographicException or HttpRequestException or System.Text.Json.JsonException
        or InvalidOperationException or ArgumentException or KeyNotFoundException or OverflowException;

    private async Task<bool> WarmResource(ResourceKey key)
    {
        try
        {
            if(key.Kind==ResourceKind.Worker)await ReadWorkerDetail(key,stop.Token);
            else if(key.Kind==ResourceKind.D1)await ReadD1Detail(key,stop.Token);
            else if(key.Kind==ResourceKind.R2)await ReadR2Detail(key,stop.Token);
            return true;
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){throw;}
        catch(Exception e)when(WarmRecoverable(e)){return false;}
    }

    private async Task PanelDetailsWarmLoop()
    {
        try{await Task.Delay(PanelDetailsWarmPolicy.InitialDelayMs,stop.Token);}catch(OperationCanceledException){return;}
        while(!stop.IsCancellationRequested)
        {
            int resourceReads=0,queueReads=0,historyReads=0,skipped=0;
            var queueLists=new Dictionary<string,QueueInventory>(StringComparer.OrdinalIgnoreCase);
            try
            {
                try{await DiscoverResources(stop.Token);}catch(OperationCanceledException)when(stop.IsCancellationRequested){return;}
                catch(Exception e)when(WarmRecoverable(e)){skipped++;}

                foreach(var key in PanelDetailsWarmPolicy.FirstPass(Inventory))
                {
                    if(await WarmResource(key))resourceReads++;else skipped++;
                    await Task.Delay(PanelDetailsWarmPolicy.InterReadDelayMs,stop.Token);
                }

                foreach(var profile in Accounts.Select(x=>x.Profile).Where(x=>x.Enabled&&!string.IsNullOrEmpty(x.AccountId)))
                {
                    QueueInventory? list=null;
                    try{list=await ReadQueues(profile.AccountId,stop.Token);queueReads++;queueLists[profile.AccountId]=list;}
                    catch(OperationCanceledException)when(stop.IsCancellationRequested){return;}
                    catch(Exception e)when(WarmRecoverable(e)){skipped++;}

                    if(list is {Items.Length:>0})
                    {
                        try{await ReadQueueBacklog(list.Items[0],stop.Token);queueReads++;}
                        catch(OperationCanceledException)when(stop.IsCancellationRequested){return;}
                        catch(Exception e)when(WarmRecoverable(e)){skipped++;}
                    }
                    try{await ReadAiHistory(profile.AccountId,stop.Token);historyReads++;}
                    catch(OperationCanceledException)when(stop.IsCancellationRequested){return;}
                    catch(Exception e)when(WarmRecoverable(e)){skipped++;}
                }

                log.Event("panel_cache_warm_firstpass",new{resource_reads=resourceReads,queue_reads=queueReads,
                    history_reads=historyReads,skipped,inventory_items=Inventory.Items.Count()});

                foreach(var key in PanelDetailsWarmPolicy.Remaining(Inventory))
                {
                    if(await WarmResource(key))resourceReads++;else skipped++;
                    await Task.Delay(PanelDetailsWarmPolicy.InterReadDelayMs,stop.Token);
                }

                foreach(var profile in Accounts.Select(x=>x.Profile).Where(x=>x.Enabled&&!string.IsNullOrEmpty(x.AccountId)))
                {
                    if(!queueLists.TryGetValue(profile.AccountId,out var list))continue;
                    foreach(var q in list.Items.Take(PanelDetailsWarmPolicy.MaxQueuesPerAccount))
                    {
                        if(q!=list.Items[0])
                        {
                            try{await ReadQueueBacklog(q,stop.Token);queueReads++;}
                            catch(OperationCanceledException)when(stop.IsCancellationRequested){return;}
                            catch(Exception e)when(WarmRecoverable(e)){skipped++;}
                        }
                        try{await ReadQueueHistory(q,stop.Token);historyReads++;}
                        catch(OperationCanceledException)when(stop.IsCancellationRequested){return;}
                        catch(Exception e)when(WarmRecoverable(e)){skipped++;}
                        await Task.Delay(PanelDetailsWarmPolicy.InterReadDelayMs,stop.Token);
                    }
                }
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){return;}
            catch(Exception e)when(WarmRecoverable(e)){skipped++;}

            log.Event("panel_cache_warm",new{resource_reads=resourceReads,queue_reads=queueReads,
                history_reads=historyReads,skipped,inventory_items=Inventory.Items.Count()});
            try{await Task.Delay(TimeSpan.FromSeconds(PanelDetailsWarmPolicy.RefreshSeconds),stop.Token);}
            catch(OperationCanceledException){return;}
        }
    }
}
