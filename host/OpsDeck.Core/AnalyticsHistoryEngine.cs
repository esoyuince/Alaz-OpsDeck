using System.Text.Json;
namespace OpsDeck.Core;
public sealed partial class AppEngine
{
    private readonly Func<string,string,HttpMessageHandler?>? historyHandlerFactory;
    private readonly Dictionary<string,(IAnalyticsReading Value,DateTimeOffset Next)> analyticsHistoryCache=new(StringComparer.OrdinalIgnoreCase);
    private Task<IAnalyticsReading>? analyticsHistoryTask;private string analyticsHistoryTaskKey="";
    public Task<QueueHistory> ReadQueueHistory(QueueInfo requested,CancellationToken ct)
    {
        ValidateQueueAccount(requested.AccountId);QueueInfo q;
        lock(gate)
        {
            if(!queueCache.TryGetValue(requested.AccountId+"/list",out var entry)||entry.Value is not QueueInventory inventory)throw new ArgumentException("Önce kuyruk listesini keşfedin.");
            q=inventory.Items.SingleOrDefault(x=>string.Equals(x.Id,requested.Id,StringComparison.OrdinalIgnoreCase))??throw new ArgumentException("Kuyruk listede yok.");
        }
        return HistoryRead(q.AccountId,"queue/"+q.AccountId+"/"+q.Id,(cf,t)=>cf.QueueHistory24(q,t),e=>new QueueHistory(q,AnalyticsWindow.Last24Hours(DateTimeOffset.UtcNow),DateTimeOffset.UtcNow,
            new(e.State,[],e.Message,e.RetrySeconds,e.RateLimited),new(e.State,[],e.Message,e.RetrySeconds,e.RateLimited),new(e.State,[],e.Message,e.RetrySeconds,e.RateLimited)),ct);
    }
    public Task<AiHistory> ReadAiHistory(string accountId,CancellationToken ct)
    {
        ValidateQueueAccount(accountId);
        return HistoryRead(accountId,"ai/"+accountId,(cf,t)=>cf.AiHistory24(t),e=>new AiHistory(accountId,AnalyticsWindow.Last24Hours(DateTimeOffset.UtcNow),DateTimeOffset.UtcNow,new(e.State,[],e.Message,e.RetrySeconds,e.RateLimited),new(e.State,[],e.Message,e.RetrySeconds,e.RateLimited)),ct);
    }
    private async Task<T> HistoryRead<T>(string accountId,string key,Func<CloudflareClient,CancellationToken,Task<T>> read,Func<SourceFailure,T> fail,CancellationToken ct)where T:class,IAnalyticsReading
    {
        ct.ThrowIfCancellationRequested();Task<IAnalyticsReading> pending;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
            if(analyticsHistoryCache.TryGetValue(key,out var cached)&&DateTimeOffset.UtcNow<cached.Next)return (T)cached.Value;
            if(analyticsHistoryTask is{IsCompleted:false})
            {if(!string.Equals(key,analyticsHistoryTaskKey,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Önce sürmekte olan geçmiş okumasını bekleyin.");pending=analyticsHistoryTask;}
            else
            {analyticsHistoryTaskKey=key;pending=analyticsHistoryTask=Task.Run(()=>HistoryCore(accountId,key,read,fail,ct));tasks.RemoveAll(t=>t.IsCompletedSuccessfully||t.IsCanceled);tasks.Add(pending);}
        }
        return (T)await pending.WaitAsync(ct);
    }
    private async Task<IAnalyticsReading> HistoryCore<T>(string accountId,string key,Func<CloudflareClient,CancellationToken,Task<T>> read,Func<SourceFailure,T> fail,CancellationToken external)where T:class,IAnalyticsReading
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(external,stop.Token);var ct=linked.Token;T result;
        try
        {
            var token=settings.ReadTokenForAccount(accountId);if(string.IsNullOrEmpty(token))throw new SourceFailure(SourceState.Denied,"Kayıtlı hesap tokenı kullanılamıyor.",900);
            using var cf=new CloudflareClient(accountId,token,historyHandlerFactory?.Invoke(accountId,token));await apiGate.WaitAsync(ct);
            try{result=await read(cf,ct);}finally{apiGate.Release();}
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is SourceFailure or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
        {result=fail(e as SourceFailure??new SourceFailure(SourceState.Error,"Geçmiş ölçümü alınamadı; değerler sıfırla değiştirilmedi."));}
        lock(gate)
        {
            if(analyticsHistoryCache.Count>=128&&!analyticsHistoryCache.ContainsKey(key))analyticsHistoryCache.Remove(analyticsHistoryCache.MinBy(x=>x.Value.Next).Key);
            analyticsHistoryCache[key]=(result,DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(result.RetrySeconds,60,86400)));
        }
        return result;
    }
}
