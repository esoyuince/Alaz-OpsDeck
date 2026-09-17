using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public sealed record WorkerAnalytics(ResourceKey Key,SourceState State,DateTimeOffset CollectedAt,
    DateTimeOffset Start,DateTimeOffset End,double? Requests=null,double? Errors=null,
    double? CpuP50Ms=null,double? CpuP99Ms=null,double? WallP50Ms=null,double? WallP99Ms=null,
    string Detail="",int RetrySeconds=60)
{
    public double? ErrorPercent=>Requests>0&&Errors.HasValue?Errors.Value/Requests.Value*100:null;
    public bool IsCurrent(DateTimeOffset now)=>Freshness.IsCurrent(CollectedAt,now,180);
}
public sealed partial class CloudflareClient
{
    private const string WorkerBaseQuery="query OpsDeckWorkerDetail($accountTag: string!, $scriptName: string!, $start: Time!, $end: Time!) { viewer { accounts(filter: {accountTag: $accountTag}) { workersInvocationsAdaptive(limit: 1, filter: {scriptName: $scriptName, datetime_geq: $start, datetime_lt: $end}) { dimensions { scriptName } sum { requests errors } quantiles { __typename cpuTimeP50 cpuTimeP99 } } } } }";
    private Task<JsonDocument> DetailQuery(ResourceKey key,DateTimeOffset start,DateTimeOffset end,bool wall,CancellationToken ct)
    {
        string query=wall?WorkerBaseQuery.Replace("__typename cpuTimeP50 cpuTimeP99","__typename cpuTimeP50 cpuTimeP99 wallTimeP50 wallTimeP99",StringComparison.Ordinal):WorkerBaseQuery;
        return Request("graphql",JsonSerializer.Serialize(new{query,variables=new{accountTag=account,scriptName=key.Id,start=start.UtcDateTime.ToString("O"),end=end.UtcDateTime.ToString("O")}}),ct);
    }
    public async Task<WorkerAnalytics> WorkerDetail(ResourceKey key,CancellationToken ct)
    {
        key.Validate();if(key.Kind!=ResourceKind.Worker||key.Scope!="account"||!string.Equals(key.AccountId,account,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Worker identity/account mismatch.");
        var end=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60*60).AddMinutes(-3);var start=end.AddMinutes(-5);
        using var first=await DetailQuery(key,start,end,false,ct);var rows=Rows(first.RootElement,"workersInvocationsAdaptive");
        if(rows.GetArrayLength()==0)return new(key,SourceState.NoData,DateTimeOffset.UtcNow,start,end,Detail:"Bu beş dakikalık pencerede analiz kaydı yok; sıfır trafik iddiası değil.");
        var raw=rows[0].Clone();if(NonNegative(raw.GetProperty("sum"),"requests")==0)return ParseWorkerDetail(raw,key,start,end,DateTimeOffset.UtcNow,new Dictionary<string,string>(),false);
        Dictionary<string,string> descriptions=[];bool wall=false;string note="";
        try
        {
            string? type=raw.GetProperty("quantiles").GetProperty("__typename").GetString();
            if(type==null||!Regex.IsMatch(type,"^[_A-Za-z][_A-Za-z0-9]{0,127}$"))throw new SourceFailure(SourceState.Partial,"Quantile type unavailable.");
            // The only dynamic query text is a validated GraphQL type name, JSON quoted.
            string schemaQuery="query OpsDeckWorkerUnits { __type(name: "+JsonSerializer.Serialize(type)+") { fields { name description } } }";
            using var schema=await Request("graphql",JsonSerializer.Serialize(new{query=schemaQuery}),ct);
            descriptions=ParseTimeDescriptions(schema.RootElement);
            wall=MillisecondsPerUnit(descriptions.GetValueOrDefault("wallTimeP50"))!=null&&MillisecondsPerUnit(descriptions.GetValueOrDefault("wallTimeP99"))!=null;
            if(wall){using var complete=await DetailQuery(key,start,end,true,ct);var completeRows=Rows(complete.RootElement,"workersInvocationsAdaptive");if(completeRows.GetArrayLength()==1)raw=completeRows[0].Clone();else{wall=false;note="Ek süre sorgusunda kayıt yok; ilk okuma korundu.";}}
        }
        catch(Exception e)when(e is SourceFailure or KeyNotFoundException or InvalidOperationException or JsonException or HttpRequestException)
        {note="Süre şeması/ek alanlar doğrulanamadı; istek ve hata okuması korunuyor.";}
        return ParseWorkerDetail(raw,key,start,end,DateTimeOffset.UtcNow,descriptions,wall,note);
    }
    public static Dictionary<string,string> ParseTimeDescriptions(JsonElement root)
    {
        var fields=root.GetProperty("data").GetProperty("__type").GetProperty("fields");
        if(fields.ValueKind!=JsonValueKind.Array||fields.GetArrayLength()>256)throw new SourceFailure(SourceState.Error,"Invalid time schema.");
        Dictionary<string,string> result=[];string[] wanted=["cpuTimeP50","cpuTimeP99","wallTimeP50","wallTimeP99"];
        foreach(var f in fields.EnumerateArray())
        {
            string? name=f.GetProperty("name").GetString();if(name==null||!wanted.Contains(name,StringComparer.Ordinal))continue;
            string? description=f.TryGetProperty("description",out var d)&&d.ValueKind==JsonValueKind.String?d.GetString():null;
            if(description!=null&&description.Length<=1024&&!result.TryAdd(name,description))throw new SourceFailure(SourceState.Error,"Duplicate time schema field.");
        }
        return result;
    }
    public static double? MillisecondsPerUnit(string? description)
    {
        if(description==null)return null;
        // Require one unambiguous unit from the provider's own live field description.
        var units=new[]{("nanoseconds",.000001),("microseconds",.001),("milliseconds",1d),("seconds",1000d)}
            .Where(u=>Regex.IsMatch(description,@"\b"+u.Item1+@"\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)).ToArray();
        return units.Length==1?units[0].Item2:null;
    }
    public static WorkerAnalytics ParseWorkerDetail(JsonElement row,ResourceKey key,DateTimeOffset start,DateTimeOffset end,DateTimeOffset now,IReadOnlyDictionary<string,string> descriptions,bool wall,string note="")
    {
        key.Validate();if(key.Kind!=ResourceKind.Worker||key.Scope!="account"||end<=start||end-start!=TimeSpan.FromMinutes(5))throw new ArgumentException("Invalid Worker/window.");
        if(row.GetProperty("dimensions").GetProperty("scriptName").GetString()!=key.Id)throw new SourceFailure(SourceState.Error,"Worker response identity mismatch.");
        var sum=row.GetProperty("sum");double requests=NonNegative(sum,"requests"),errors=NonNegative(sum,"errors");
        if(errors>requests)throw new SourceFailure(SourceState.Error,"Inconsistent Worker counts.");
        var q=row.GetProperty("quantiles");
        double? Time(string name)
        {
            if(requests==0)return null;
            double? factor=MillisecondsPerUnit(descriptions.GetValueOrDefault(name));if(factor==null)return null;
            if(!q.TryGetProperty(name,out var v)||v.ValueKind==JsonValueKind.Null)return null;
            double n=NonNegative(q,name)*factor.Value;if(!double.IsFinite(n)||n>1e16)throw new SourceFailure(SourceState.Error,"Invalid time metric.");return n;
        }
        var cpu50=Time("cpuTimeP50");var cpu99=Time("cpuTimeP99");var wall50=wall?Time("wallTimeP50"):null;var wall99=wall?Time("wallTimeP99"):null;
        if(cpu99<cpu50||wall99<wall50)throw new SourceFailure(SourceState.Error,"Inverted time percentiles.");
        bool complete=cpu50.HasValue&&cpu99.HasValue&&wall50.HasValue&&wall99.HasValue;
        string detail="Beş dakikalık pencere; son üç dakika hariç. P50 medyan, P99 yüzde 99 sınırıdır. CPU ve duvar saati süresi farklıdır; ağ/HTTPS gecikmesi veya faturalanan toplam CPU değildir. ";
        if(!cpu50.HasValue||!cpu99.HasValue)detail+="CPU süre alanı/birimi doğrulanmadı veya örnek yok. ";
        if(!wall50.HasValue||!wall99.HasValue)detail+="Duvar saati süre alanı/birimi doğrulanmadı veya örnek yok. ";
        return new(key,complete||requests==0?SourceState.Ok:SourceState.Partial,now,start,end,requests,errors,cpu50,cpu99,wall50,wall99,detail+note);
    }
}

public sealed partial class AppEngine
{
    private readonly Dictionary<ResourceKey,(WorkerAnalytics Value,DateTimeOffset Next)> workerDetails=[];
    private Task<WorkerAnalytics>? workerDetailTask;
    private ResourceKey? workerDetailKey;
    public Task<WorkerAnalytics> ReadWorkerDetail(ResourceKey key,CancellationToken external)
    {
        key.Validate();lock(gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
            if(key.Kind!=ResourceKind.Worker||!Inventory.Items.Any(i=>i.Key==key))throw new ArgumentException("Önce keşfedilmiş bir Worker seçin.");
            if(workerDetails.TryGetValue(key,out var cached)&&DateTimeOffset.UtcNow<cached.Next)return Task.FromResult(cached.Value);
            if(workerDetailTask is {IsCompleted:false})return workerDetailKey==key?workerDetailTask.WaitAsync(external):throw new InvalidOperationException("Başka bir Worker okuması sürüyor.");
            workerDetailKey=key;workerDetailTask=Task.Run(()=>ReadWorkerDetailCore(key,external));
            // Dispose takes the same lock before snapshotting outstanding work.
            tasks.RemoveAll(t=>t.IsCompletedSuccessfully||t.IsCanceled);tasks.Add(workerDetailTask);return workerDetailTask;
        }
    }
    private async Task<WorkerAnalytics> ReadWorkerDetailCore(ResourceKey key,CancellationToken external)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(external,stop.Token);var ct=linked.Token;WorkerAnalytics result;
        try
        {
            var profile=Accounts.Single(a=>string.Equals(a.Profile.AccountId,key.AccountId,StringComparison.OrdinalIgnoreCase)).Profile;
            if(!profile.Enabled)throw new SourceFailure(SourceState.Setup,"Hesap izleme kapalı.");
            string? token=settings.ReadTokenForAccount(profile.AccountId);if(string.IsNullOrEmpty(token))throw new SourceFailure(SourceState.Denied,"Kayıtlı hesap anahtarı yok.");
            using var client=new CloudflareClient(profile.AccountId,token);await apiGate.WaitAsync(ct);
            try{result=await client.WorkerDetail(key,ct);}finally{apiGate.Release();}
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is SourceFailure or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            var now=DateTimeOffset.UtcNow;result=new(key,e is SourceFailure sf?sf.State:SourceState.Error,now,now.AddMinutes(-8),now.AddMinutes(-3),Detail:e is SourceFailure failure?failure.Message:"Worker ölçümü alınamadı; sıfırla değiştirilmedi.",RetrySeconds:e is SourceFailure retry?retry.RetrySeconds:60);
        }
        lock(gate)
        {
            if(workerDetails.Count>=128&&!workerDetails.ContainsKey(key))workerDetails.Remove(workerDetails.MinBy(p=>p.Value.Next).Key);
            workerDetails[key]=(result,DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(result.RetrySeconds,60,86400)));
        }
        return result;
    }
}
