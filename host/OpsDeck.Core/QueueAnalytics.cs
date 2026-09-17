using System.Text.Json;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public interface IQueueReading { int RetrySeconds { get; } }
public sealed record QueueInfo(string AccountId,string Id,string Name,bool? DeliveryPaused=null,
    double? RetentionSeconds=null,double? Consumers=null,string Jurisdiction="");
public sealed record QueueInventory(string AccountId,SourceState State,DateTimeOffset CollectedAt,
    QueueInfo[] Items,bool Complete,string Detail="",int RetrySeconds=60):IQueueReading;
public sealed record QueueBacklog(QueueInfo Queue,SourceState State,DateTimeOffset CollectedAt,
    double? Messages=null,double? Bytes=null,DateTimeOffset? OldestAt=null,string Detail="",int RetrySeconds=60):IQueueReading
{
    public bool IsCurrent(DateTimeOffset now)=>Freshness.IsCurrent(CollectedAt,now,180);
    public double? OldestAgeSeconds=>OldestAt.HasValue?Math.Max(0,(CollectedAt-OldestAt.Value).TotalSeconds):null;
}
public sealed partial class CloudflareClient
{
    internal static bool QueueIdValid(string value)=>Regex.IsMatch(value,@"\A[a-fA-F0-9]{32}\z");
    public static bool IsReadOnlyQueuePath(string accountId,string path)=>QueueIdValid(accountId)&&
        Regex.IsMatch(path,@"\Aaccounts/"+accountId+@"/queues(?:\?page=(?:[1-9]|10)&per_page=100|/[a-fA-F0-9]{32}/metrics)\z");
    private static double? QueueNumber(JsonElement o,string field)
    {
        if(o.ValueKind!=JsonValueKind.Object)throw new SourceFailure(SourceState.Error,"Invalid queue metric object");
        double? n=OptionalNonNegative(o,field);
        if(n.HasValue&&n.Value!=Math.Truncate(n.Value))throw new SourceFailure(SourceState.Error,"Fractional queue count/timestamp");
        return n;
    }
    public async Task<QueueInventory> QueuesList(CancellationToken ct)
    {
        List<QueueInfo> items=[];HashSet<string> ids=new(StringComparer.OrdinalIgnoreCase);bool complete=false;
        for(int page=1;page<=10;page++)
        {
            using var doc=await Request($"accounts/{account}/queues?page={page}&per_page=100",null,ct);
            var root=doc.RootElement;var rows=root.GetProperty("result");
            if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>100)throw new SourceFailure(SourceState.Error,"Queue list row limit/schema");
            foreach(var row in rows.EnumerateArray())
            {
                string id=Dimension(row,"queue_id",32),name=Dimension(row,"queue_name",256);
                if(!QueueIdValid(id)||!ids.Add(id))throw new SourceFailure(SourceState.Error,"Invalid or duplicate queue identity");
                bool? paused=null;double? retention=null;string jurisdiction="";
                if(row.TryGetProperty("settings",out var s)&&s.ValueKind!=JsonValueKind.Null)
                {
                    if(s.ValueKind!=JsonValueKind.Object)throw new SourceFailure(SourceState.Error,"Queue settings schema");
                    if(s.TryGetProperty("delivery_paused",out var p)&&p.ValueKind!=JsonValueKind.Null)
                    {if(p.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new SourceFailure(SourceState.Error,"Queue paused schema");paused=p.GetBoolean();}
                    retention=QueueNumber(s,"message_retention_period");
                }
                if(row.TryGetProperty("jurisdiction",out var j)&&j.ValueKind!=JsonValueKind.Null)
                {jurisdiction=j.GetString()??"";if(jurisdiction is not ("" or "eu" or "us" or "fedramp"))throw new SourceFailure(SourceState.Error,"Queue jurisdiction schema");}
                items.Add(new(account,id,name,paused,retention,QueueNumber(row,"consumers_total_count"),jurisdiction));
            }
            if(!root.TryGetProperty("result_info",out var info)||info.ValueKind!=JsonValueKind.Object)break;
            double? actualPage=QueueNumber(info,"page"),per=QueueNumber(info,"per_page"),pages=QueueNumber(info,"total_pages"),total=QueueNumber(info,"total_count");
            if(actualPage.HasValue&&actualPage!=page||per is <=0 or >100||pages<page&&!(page==1&&pages==0&&items.Count==0))throw new SourceFailure(SourceState.Error,"Inconsistent queue pagination");
            bool last=pages.HasValue?page>=pages:per.HasValue&&rows.GetArrayLength()<per;
            if(last){complete=!total.HasValue||total==items.Count;break;}
            if(rows.GetArrayLength()==0)break;
        }
        return new(account,complete?SourceState.Ok:SourceState.Partial,DateTimeOffset.UtcNow,
            items.OrderBy(x=>x.Name,StringComparer.Ordinal).ToArray(),complete,
            complete?"Liste tamam; kaynak varlığı tüketicinin sağlıklı olduğu anlamına gelmez.":"Kısmi liste: sayfalama kanıtı eksik, değişken toplam veya 10 sayfa sınırı. Eksik kaynaklar yok sayılmaz.");
    }
    public async Task<QueueBacklog> QueueMetrics(QueueInfo queue,CancellationToken ct)
    {
        if(!QueueIdValid(queue.Id)||!string.Equals(queue.AccountId,account,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Queue account/identity mismatch");
        if(queue.Jurisdiction.Length>0)return new(queue,SourceState.NoData,DateTimeOffset.UtcNow,
            Detail:"Restricted jurisdiction bu görünümde desteklenmiyor; ölçüm isteği gönderilmedi.");
        using var doc=await Request($"accounts/{account}/queues/{queue.Id}/metrics",null,ct);
        return ParseQueueMetrics(queue,doc.RootElement.GetProperty("result"),DateTimeOffset.UtcNow);
    }
    public static QueueBacklog ParseQueueMetrics(QueueInfo queue,JsonElement result,DateTimeOffset now)
    {
        double? count=QueueNumber(result,"backlog_count"),bytes=QueueNumber(result,"backlog_bytes"),stamp=QueueNumber(result,"oldest_message_timestamp_ms");
        DateTimeOffset? oldest=null;
        if(stamp is >0)
        {
            if(stamp>now.ToUnixTimeMilliseconds()+60000||stamp<DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds())throw new SourceFailure(SourceState.Error,"Invalid oldest-message timestamp");
            oldest=DateTimeOffset.FromUnixTimeMilliseconds((long)stamp.Value);
            if(oldest>now)oldest=null;
        }
        var state=!count.HasValue&&!bytes.HasValue?SourceState.NoData:
            count.HasValue&&bytes.HasValue&&(count==0||oldest.HasValue)?SourceState.Ok:SourceState.Partial;
        return new(queue,state,now,count,bytes,oldest,
            "REST anlık, best-effort backlog; dağıtık sistem nedeniyle yaklaşık olabilir. Oldest timestamp 0/bulunamıyor ise yaş bilinmez. Mesaj içeriği, pull/peek/ACK/purge çağrısı yok; bu değer ücret veya iş başarı oranı değildir.");
    }
}
