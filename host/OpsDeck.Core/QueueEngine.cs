using System.Text.Json;
namespace OpsDeck.Core;

public sealed partial class AppEngine
{
    private readonly Dictionary<string,(IQueueReading Value,DateTimeOffset Next)> queueCache=new(StringComparer.OrdinalIgnoreCase);
    private Task<IQueueReading>? queueTask;private string queueTaskKey="";
    public Task<QueueInventory> ReadQueues(string accountId,CancellationToken ct)
    {
        ValidateQueueAccount(accountId);
        return QueueRead(accountId,accountId+"/list",(cf,t)=>cf.QueuesList(t),
            e=>new QueueInventory(accountId,e.State,DateTimeOffset.UtcNow,[],false,e.Message,e.RetrySeconds),ct);
    }
    public Task<QueueBacklog> ReadQueueBacklog(QueueInfo requested,CancellationToken ct)
    {
        ValidateQueueAccount(requested.AccountId);QueueInfo canonical;
        lock(gate)
        {
            if(!queueCache.TryGetValue(requested.AccountId+"/list",out var entry)||entry.Value is not QueueInventory list)
                throw new ArgumentException("Önce bu hesabın kuyruklarını listeleyin.");
            canonical=list.Items.SingleOrDefault(q=>string.Equals(q.Id,requested.Id,StringComparison.OrdinalIgnoreCase))
                ??throw new ArgumentException("Kuyruk keşfedilmiş listede değil.");
        }
        return QueueRead(canonical.AccountId,canonical.AccountId+"/"+canonical.Id,(cf,t)=>cf.QueueMetrics(canonical,t),
            e=>new QueueBacklog(canonical,e.State,DateTimeOffset.UtcNow,Detail:e.Message,RetrySeconds:e.RetrySeconds),ct);
    }
    private void ValidateQueueAccount(string accountId)
    {
        if(!CloudflareClient.QueueIdValid(accountId)||!Accounts.Any(a=>a.Profile.Enabled&&string.Equals(a.Profile.AccountId,accountId,StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Etkin ve yapılandırılmış bir hesap seçin.");
    }
    private async Task<T> QueueRead<T>(string accountId,string key,Func<CloudflareClient,CancellationToken,Task<T>> collect,
        Func<SourceFailure,T> failed,CancellationToken ct) where T:class,IQueueReading
    {
        ct.ThrowIfCancellationRequested();Task<IQueueReading> pending;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
            if(queueCache.TryGetValue(key,out var cached)&&DateTimeOffset.UtcNow<cached.Next)return (T)cached.Value;
            if(queueTask is{IsCompleted:false})
            {if(!string.Equals(queueTaskKey,key,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Önce devam eden kuyruk okumasını bekleyin.");pending=queueTask;}
            else
            {queueTaskKey=key;pending=queueTask=Task.Run(()=>QueueCore(accountId,key,collect,failed,ct));tasks.RemoveAll(t=>t.IsCompletedSuccessfully||t.IsCanceled);tasks.Add(pending);}
        }
        return (T)await pending.WaitAsync(ct);
    }
    private async Task<IQueueReading> QueueCore<T>(string accountId,string key,Func<CloudflareClient,CancellationToken,Task<T>> collect,
        Func<SourceFailure,T> failed,CancellationToken external) where T:class,IQueueReading
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(external,stop.Token);var ct=linked.Token;T result;
        try
        {
            string? token=settings.ReadTokenForAccount(accountId);
            if(string.IsNullOrEmpty(token))throw new SourceFailure(SourceState.Denied,"Kayıtlı hesap tokenı kullanılamıyor.",900);
            using var client=new CloudflareClient(accountId,token,historyHandlerFactory?.Invoke(accountId,token));await apiGate.WaitAsync(ct);
            try{result=await collect(client,ct);}finally{apiGate.Release();}
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is SourceFailure or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        {result=failed(e as SourceFailure??new SourceFailure(SourceState.Error,"Kuyruk ölçümü alınamadı; eksik veri sıfıra çevrilmedi."));}
        lock(gate)
        {
            if(queueCache.Count>=128&&!queueCache.ContainsKey(key))queueCache.Remove(queueCache.MinBy(x=>x.Value.Next).Key);
            queueCache[key]=(result,DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(result.RetrySeconds,60,86400)));
        }
        return result;
    }
}
