using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public sealed record D1Analytics(ResourceKey Key,SourceState State,DateTimeOffset CollectedAt,
    DateTimeOffset Start,DateTimeOffset End,double? ReadQueries=null,double? WriteQueries=null,
    double? RowsRead=null,double? RowsWritten=null,double? ResponseBytes=null,double? QueryP90Ms=null,
    double? DatabaseSizeBytes=null,double? TableCount=null,string Jurisdiction="",string ReplicationMode="",
    string Detail="",int RetrySeconds=60)
{
    public bool IsCurrent(DateTimeOffset now)=>Freshness.IsCurrent(CollectedAt,now,180);
}
public sealed record R2Operation(string ActionType,string Status,double Requests);
public sealed record R2Analytics(ResourceKey Key,SourceState State,DateTimeOffset CollectedAt,
    DateTimeOffset OperationsStart,DateTimeOffset OperationsEnd,double? TotalRequests=null,
    double? SuccessRequests=null,double? UserErrors=null,double? InternalErrors=null,
    double? PayloadBytes=null,double? MetadataBytes=null,double? ObjectCount=null,double? UploadCount=null,
    DateTimeOffset? StorageAt=null,R2Operation[]? TopOperations=null,string Detail="",int RetrySeconds=60)
{
    public bool IsCurrent(DateTimeOffset now)=>Freshness.IsCurrent(CollectedAt,now,180);
}

public sealed partial class CloudflareClient
{
    private const string D1ResourceQuery="query OpsDeckD1Resource($accountTag: string!, $databaseId: string!, $start: Time!, $end: Time!) { viewer { accounts(filter: {accountTag: $accountTag}) { d1AnalyticsAdaptiveGroups(limit: 1, filter: {databaseId: $databaseId, datetime_geq: $start, datetime_leq: $end}) { dimensions { databaseId } sum { readQueries writeQueries rowsRead rowsWritten queryBatchResponseBytes } quantiles { queryBatchTimeMsP90 } } } } }";
    private const string R2OperationsQuery="query OpsDeckR2Operations($accountTag: string!, $bucketName: string!, $start: Time!, $end: Time!) { viewer { accounts(filter: {accountTag: $accountTag}) { r2OperationsAdaptiveGroups(limit: 1000, filter: {bucketName: $bucketName, datetime_geq: $start, datetime_leq: $end}) { dimensions { bucketName actionType actionStatus } sum { requests } } } } }";
    private const string R2StorageQuery="query OpsDeckR2Storage($accountTag: string!, $bucketName: string!, $start: Time!, $end: Time!) { viewer { accounts(filter: {accountTag: $accountTag}) { r2StorageAdaptiveGroups(limit: 1, filter: {bucketName: $bucketName, datetime_geq: $start, datetime_leq: $end}, orderBy: [datetime_DESC]) { dimensions { bucketName datetime } max { objectCount uploadCount payloadSize metadataSize } } } } }";

    private Task<JsonDocument> ResourceGraph(string query,object variables,CancellationToken ct)=>
        Request("graphql",JsonSerializer.Serialize(new{query,variables}),ct);
    private JsonElement[] Dataset(JsonElement root,string dataset,int maxRows)
    {
        try
        {
            var accounts=root.GetProperty("data").GetProperty("viewer").GetProperty("accounts");
            if(accounts.ValueKind!=JsonValueKind.Array||accounts.GetArrayLength()!=1)throw new SourceFailure(SourceState.Error,"Account analytics not available");
            var rows=accounts[0].GetProperty(dataset);if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>maxRows)throw new SourceFailure(SourceState.Error,"Analytics row bound exceeded");
            return rows.EnumerateArray().Select(x=>x.Clone()).ToArray();
        }
        catch(Exception e)when(e is KeyNotFoundException or InvalidOperationException){throw new SourceFailure(SourceState.Error,"Analytics schema not supported");}
    }
    private static string Dimension(JsonElement o,string key,int max)
    {
        if(!o.TryGetProperty(key,out var v)||v.ValueKind!=JsonValueKind.String)throw new SourceFailure(SourceState.Error,"Missing analytics dimension");
        string s=v.GetString()??"";if(s.Length==0||s.Length>max||s.Any(char.IsControl))throw new SourceFailure(SourceState.Error,"Invalid analytics dimension");return s;
    }
    private static double? OptionalNonNegative(JsonElement o,string key)
    {
        if(!o.TryGetProperty(key,out var n)||n.ValueKind==JsonValueKind.Null)return null;
        if(!n.TryGetDouble(out var v)||!double.IsFinite(v)||v<0||v>1e16)throw new SourceFailure(SourceState.Error,"Invalid numeric analytics metric");return v;
    }
    public async Task<D1Analytics> D1Detail(ResourceKey key,CancellationToken ct)
    {
        key.Validate();if(key.Kind!=ResourceKind.D1||key.Scope!="account"||!string.Equals(key.AccountId,account,StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(key.Id,"D",out _))throw new ArgumentException("D1 identity/account mismatch.");
        var end=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60*60).AddMinutes(-3);var start=end.AddHours(-24);var now=DateTimeOffset.UtcNow;
        double? size=null,tables=null;string jurisdiction="",replication="";
        using(var info=await Request($"accounts/{account}/d1/database/{key.Id}",null,ct))
        {
            var metaRow=info.RootElement.GetProperty("result");if(metaRow.ValueKind!=JsonValueKind.Object||Dimension(metaRow,"uuid",64)!=key.Id)throw new SourceFailure(SourceState.Error,"D1 metadata identity mismatch");
            size=OptionalNonNegative(metaRow,"file_size");tables=OptionalNonNegative(metaRow,"num_tables");
            if(metaRow.TryGetProperty("jurisdiction",out var j)&&j.ValueKind==JsonValueKind.String){jurisdiction=j.GetString()??"";if(jurisdiction is not ("" or "eu" or "fedramp" or "us"))throw new SourceFailure(SourceState.Error,"Unexpected D1 jurisdiction");}
            if(metaRow.TryGetProperty("read_replication",out var rr)&&rr.ValueKind==JsonValueKind.Object&&rr.TryGetProperty("mode",out var mode)&&mode.ValueKind==JsonValueKind.String){replication=mode.GetString()??"";if(replication is not ("" or "auto" or "disabled"))throw new SourceFailure(SourceState.Error,"Unexpected D1 replication mode");}
        }
        using var doc=await ResourceGraph(D1ResourceQuery,new{accountTag=account,databaseId=key.Id,start=start.UtcDateTime.ToString("O"),end=end.UtcDateTime.ToString("O")},ct);
        var rows=Dataset(doc.RootElement,"d1AnalyticsAdaptiveGroups",1);
        if(rows.Length==0)return new(key,SourceState.NoData,now,start,end,DatabaseSizeBytes:size,TableCount:tables,Jurisdiction:jurisdiction,ReplicationMode:replication,Detail:"D1 metadata ok; son 24 saat analiz satırı yok. Bu sıfır trafik iddiası değildir.");
        var row=rows[0];var dimensions=row.GetProperty("dimensions");if(Dimension(dimensions,"databaseId",64)!=key.Id)throw new SourceFailure(SourceState.Error,"D1 analytics identity mismatch");
        var sum=row.GetProperty("sum");double read=NonNegative(sum,"readQueries"),write=NonNegative(sum,"writeQueries"),rowsRead=NonNegative(sum,"rowsRead"),rowsWritten=NonNegative(sum,"rowsWritten"),bytes=NonNegative(sum,"queryBatchResponseBytes");
        double? p90=null;if(row.TryGetProperty("quantiles",out var q)&&q.ValueKind==JsonValueKind.Object)p90=OptionalNonNegative(q,"queryBatchTimeMsP90");
        var state=(read+write)>0&&!p90.HasValue?SourceState.Partial:SourceState.Ok;
        string detail="Son 24 saat, son üç dakika hariç. Query sayısı faturalama satırı değildir; rows read/written maliyet sinyalleridir. P90 sunucu tarafı query response/serialization süresidir. Ham SQL/insights toplanmaz.";
        return new(key,state,now,start,end,read,write,rowsRead,rowsWritten,bytes,p90,size,tables,jurisdiction,replication,detail);
    }
    public async Task<R2Analytics> R2Detail(ResourceKey key,CancellationToken ct)
    {
        key.Validate();if(key.Kind!=ResourceKind.R2||key.Scope!="default"||!string.Equals(key.AccountId,account,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("R2 identity/account/scope mismatch.");
        var end=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60*60).AddMinutes(-3);var start=end.AddHours(-24);var storageStart=end.AddDays(-30);var now=DateTimeOffset.UtcNow;
        using var opsDoc=await ResourceGraph(R2OperationsQuery,new{accountTag=account,bucketName=key.Id,start=start.UtcDateTime.ToString("O"),end=end.UtcDateTime.ToString("O")},ct);
        var rows=Dataset(opsDoc.RootElement,"r2OperationsAdaptiveGroups",1000);double total=0,success=0,user=0,internalErrors=0;HashSet<string> groups=[];List<R2Operation> operations=[];
        foreach(var row in rows)
        {
            var d=row.GetProperty("dimensions");if(Dimension(d,"bucketName",256)!=key.Id)throw new SourceFailure(SourceState.Error,"R2 operation identity mismatch");string action=Dimension(d,"actionType",96),status=Dimension(d,"actionStatus",32);if(status is not ("success" or "userError" or "internalError"))throw new SourceFailure(SourceState.Error,"Unexpected R2 operation status");
            if(!groups.Add(action+"\n"+status))throw new SourceFailure(SourceState.Error,"Duplicate R2 operation group");double requests=NonNegative(row.GetProperty("sum"),"requests");total+=requests;if(!double.IsFinite(total)||total>1e16)throw new SourceFailure(SourceState.Error,"R2 request total out of range");
            if(status=="success")success+=requests;else if(status=="userError")user+=requests;else internalErrors+=requests;operations.Add(new(action,status,requests));
        }
        using var storageDoc=await ResourceGraph(R2StorageQuery,new{accountTag=account,bucketName=key.Id,start=storageStart.UtcDateTime.ToString("O"),end=end.UtcDateTime.ToString("O")},ct);
        var storage=Dataset(storageDoc.RootElement,"r2StorageAdaptiveGroups",1);double? payload=null,metadata=null,objects=null,uploads=null;DateTimeOffset? storageAt=null;
        if(storage.Length==1)
        {
            var d=storage[0].GetProperty("dimensions");if(Dimension(d,"bucketName",256)!=key.Id)throw new SourceFailure(SourceState.Error,"R2 storage identity mismatch");string when=Dimension(d,"datetime",64);if(!DateTimeOffset.TryParse(when,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var parsed)||parsed>end.AddMinutes(5)||parsed<storageStart.AddMinutes(-5))throw new SourceFailure(SourceState.Error,"Invalid R2 storage timestamp");storageAt=parsed;
            var max=storage[0].GetProperty("max");payload=OptionalNonNegative(max,"payloadSize");metadata=OptionalNonNegative(max,"metadataSize");objects=OptionalNonNegative(max,"objectCount");uploads=OptionalNonNegative(max,"uploadCount");
        }
        bool hasOps=rows.Length>0,hasStorage=storage.Length>0;var state=hasOps&&hasStorage?SourceState.Ok:hasOps||hasStorage?SourceState.Partial:SourceState.NoData;
        var top=operations.OrderByDescending(x=>x.Requests).ThenBy(x=>x.ActionType,StringComparer.Ordinal).ThenBy(x=>x.Status,StringComparer.Ordinal).Take(8).ToArray();
        string detail="Operasyonlar son 24 saat, son üç dakika hariç. Storage son 30 günlük saklama penceresindeki en yeni bucket snapshot'ıdır. Yalnız default jurisdiction envanteri; object adları toplanmaz.";
        return new(key,state,now,start,end,hasOps?total:null,hasOps?success:null,hasOps?user:null,hasOps?internalErrors:null,payload,metadata,objects,uploads,storageAt,top,detail);
    }
}

public sealed partial class AppEngine
{
    private readonly Dictionary<ResourceKey,(D1Analytics Value,DateTimeOffset Next)> d1Details=[];
    private readonly Dictionary<ResourceKey,(R2Analytics Value,DateTimeOffset Next)> r2Details=[];
    private Task<D1Analytics>? d1DetailTask;private ResourceKey? d1DetailKey;
    private Task<R2Analytics>? r2DetailTask;private ResourceKey? r2DetailKey;
    public Task<D1Analytics> ReadD1Detail(ResourceKey key,CancellationToken external)
    {
        key.Validate();lock(gate){ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);if(key.Kind!=ResourceKind.D1||!Inventory.Items.Any(i=>i.Key==key))throw new ArgumentException("Önce keşfedilmiş bir D1 seçin.");if(d1Details.TryGetValue(key,out var cached)&&DateTimeOffset.UtcNow<cached.Next)return Task.FromResult(cached.Value);if(d1DetailTask is{IsCompleted:false})return d1DetailKey==key?d1DetailTask.WaitAsync(external):throw new InvalidOperationException("Başka bir D1 okuması sürüyor.");d1DetailKey=key;d1DetailTask=Task.Run(()=>ReadD1Core(key,external));tasks.RemoveAll(t=>t.IsCompletedSuccessfully||t.IsCanceled);tasks.Add(d1DetailTask);return d1DetailTask;}
    }
    public Task<R2Analytics> ReadR2Detail(ResourceKey key,CancellationToken external)
    {
        key.Validate();lock(gate){ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);if(key.Kind!=ResourceKind.R2||!Inventory.Items.Any(i=>i.Key==key))throw new ArgumentException("Önce keşfedilmiş bir R2 bucket seçin.");if(r2Details.TryGetValue(key,out var cached)&&DateTimeOffset.UtcNow<cached.Next)return Task.FromResult(cached.Value);if(r2DetailTask is{IsCompleted:false})return r2DetailKey==key?r2DetailTask.WaitAsync(external):throw new InvalidOperationException("Başka bir R2 okuması sürüyor.");r2DetailKey=key;r2DetailTask=Task.Run(()=>ReadR2Core(key,external));tasks.RemoveAll(t=>t.IsCompletedSuccessfully||t.IsCanceled);tasks.Add(r2DetailTask);return r2DetailTask;}
    }
    private async Task<D1Analytics> ReadD1Core(ResourceKey key,CancellationToken external)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(external,stop.Token);var ct=linked.Token;D1Analytics result;try{var profile=Accounts.Single(a=>string.Equals(a.Profile.AccountId,key.AccountId,StringComparison.OrdinalIgnoreCase)).Profile;string? token=settings.ReadTokenForAccount(profile.AccountId);if(!profile.Enabled||string.IsNullOrEmpty(token))throw new SourceFailure(SourceState.Denied,"D1 hesabı/token kullanılamıyor.");using var client=new CloudflareClient(profile.AccountId,token);await apiGate.WaitAsync(ct);try{result=await client.D1Detail(key,ct);}finally{apiGate.Release();}}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch(Exception e)when(e is SourceFailure or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException){var now=DateTimeOffset.UtcNow;result=new(key,e is SourceFailure sf?sf.State:SourceState.Error,now,now.AddHours(-24),now,Detail:e is SourceFailure f?f.Message:"D1 ayrıntısı alınamadı; sıfırla değiştirilmedi.",RetrySeconds:e is SourceFailure r?r.RetrySeconds:60);}lock(gate){if(d1Details.Count>=128&&!d1Details.ContainsKey(key))d1Details.Remove(d1Details.MinBy(p=>p.Value.Next).Key);d1Details[key]=(result,DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(result.RetrySeconds,60,86400)));}return result;
    }
    private async Task<R2Analytics> ReadR2Core(ResourceKey key,CancellationToken external)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(external,stop.Token);var ct=linked.Token;R2Analytics result;try{var profile=Accounts.Single(a=>string.Equals(a.Profile.AccountId,key.AccountId,StringComparison.OrdinalIgnoreCase)).Profile;string? token=settings.ReadTokenForAccount(profile.AccountId);if(!profile.Enabled||string.IsNullOrEmpty(token))throw new SourceFailure(SourceState.Denied,"R2 hesabı/token kullanılamıyor.");using var client=new CloudflareClient(profile.AccountId,token);await apiGate.WaitAsync(ct);try{result=await client.R2Detail(key,ct);}finally{apiGate.Release();}}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch(Exception e)when(e is SourceFailure or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException){var now=DateTimeOffset.UtcNow;result=new(key,e is SourceFailure sf?sf.State:SourceState.Error,now,now.AddHours(-24),now,Detail:e is SourceFailure f?f.Message:"R2 ayrıntısı alınamadı; sıfırla değiştirilmedi.",RetrySeconds:e is SourceFailure r?r.RetrySeconds:60);}lock(gate){if(r2Details.Count>=128&&!r2Details.ContainsKey(key))r2Details.Remove(r2Details.MinBy(p=>p.Value.Next).Key);r2Details[key]=(result,DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(result.RetrySeconds,60,86400)));}return result;
    }
}
