using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
namespace OpsDeck.Core;

public sealed class SourceFailure(SourceState state,string code,int retrySeconds=60,bool rateLimited=false):Exception(code)
{
    public SourceState State{get;}=state;
    public int RetrySeconds{get;}=retrySeconds;
    public bool RateLimited{get;}=rateLimited;
}
public sealed partial class CloudflareClient : IDisposable
{
    private readonly HttpClient http;
    private readonly string account,token;
    public CloudflareClient(string account,string token,HttpMessageHandler? handler=null)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(account,@"^[a-fA-F0-9]{32}$"))throw new ArgumentException("Invalid account ID.");
        this.account=account;this.token=token;
        http=new(handler??new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}) {
            Timeout=TimeSpan.FromSeconds(12),MaxResponseContentBufferSize=2*1024*1024};
    }
    private async Task<JsonDocument> Request(string path,string? query,CancellationToken ct)
    {
        // Only these read-only resources are reachable; GraphQL bodies are queries, never mutations.
        string d1Prefix=$"accounts/{account}/d1/database/";
        bool d1Metadata=path.StartsWith(d1Prefix,StringComparison.Ordinal)&&Guid.TryParseExact(path[d1Prefix.Length..],"D",out _);
        if(path!="graphql"&&path!=$"accounts/{account}/r2/metrics"&&path!=$"accounts/{account}/billable-usage"&&path!=$"accounts/{account}/subscriptions"&&!d1Metadata&&!(query is null&&IsReadOnlyQueuePath(account,path)))
            throw new InvalidOperationException("Endpoint is not allowlisted.");
        using var request=new HttpRequestMessage(query==null?HttpMethod.Get:HttpMethod.Post,"https://api.cloudflare.com/client/v4/"+path);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        request.Headers.UserAgent.ParseAdd("OpsDeck/0.2 (read-only)");
        if(query!=null)request.Content=new StringContent(query,Encoding.UTF8,"application/json");
        using var response=await http.SendAsync(request,ct);
        if(response.StatusCode==HttpStatusCode.Unauthorized||response.StatusCode==HttpStatusCode.Forbidden)throw new SourceFailure(SourceState.Denied,"Permission denied",900);
        if((int)response.StatusCode==429){
            var retry=response.Headers.RetryAfter;
            var seconds=retry?.Delta?.TotalSeconds??(retry?.Date-DateTimeOffset.UtcNow)?.TotalSeconds??120;
            throw new SourceFailure(SourceState.Error,"Rate limited",(int)Math.Clamp(seconds,60,86400),rateLimited:true);
        }
        if(!response.IsSuccessStatusCode)throw new SourceFailure(SourceState.Error,$"HTTP {(int)response.StatusCode}");
        JsonDocument doc;
        try{doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct),new JsonDocumentOptions{MaxDepth=32});}
        catch(JsonException){throw new SourceFailure(SourceState.Error,"Invalid response JSON");}
        try {
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)throw new SourceFailure(SourceState.Error,"Unexpected response envelope");
            if(root.TryGetProperty("errors",out var errors)&&errors.ValueKind==JsonValueKind.Array&&errors.GetArrayLength()>0)throw new SourceFailure(SourceState.Error,"API returned errors");
            if(query==null&&(!root.TryGetProperty("success",out var success)||success.ValueKind!=JsonValueKind.True))throw new SourceFailure(SourceState.Error,"API did not confirm success");
            return doc;
        }catch{doc.Dispose();throw;}
    }
    public async Task<Metric> Workers(CancellationToken ct)
    {
        var end=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60*60).AddMinutes(-3);var start=end.AddMinutes(-5);
        const string query="query OpsDeckWorkers($accountTag: string!, $start: Time!, $end: Time!) { viewer { accounts(filter: {accountTag: $accountTag}) { workersInvocationsAdaptive(limit: 1, filter: {datetime_geq: $start, datetime_lt: $end}) { sum { requests errors } } } } }";
        using var doc=await Graph(query,start,end,ct);
        var a=Rows(doc.RootElement,"workersInvocationsAdaptive");
        if(a.GetArrayLength()==0)return new(SourceState.NoData,CollectedAt:DateTimeOffset.UtcNow,SourceEnd:end,Detail:"No analytics rows for 5-minute window");
        var sum=a[0].GetProperty("sum");double requests=NonNegative(sum,"requests"),errors=NonNegative(sum,"errors");
        if(errors>requests)throw new SourceFailure(SourceState.Error,"Inconsistent request/error counts");
        return new(SourceState.Ok,requests/5,errors,DateTimeOffset.UtcNow,end,"req/min","5-minute mean; analytics window ends 3 minutes before collection; not billing CPU");
    }
    public async Task<Metric> D1(CancellationToken ct)
    {
        var end=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60*60).AddMinutes(-3);var start=end.AddMinutes(-5);
        const string query="query OpsDeckD1($accountTag: string!, $start: Time!, $end: Time!) { viewer { accounts(filter: {accountTag: $accountTag}) { d1AnalyticsAdaptiveGroups(limit: 1, filter: {datetime_geq: $start, datetime_lt: $end}) { sum { rowsRead rowsWritten } } } } }";
        using var doc=await Graph(query,start,end,ct);var a=Rows(doc.RootElement,"d1AnalyticsAdaptiveGroups");
        if(a.GetArrayLength()==0)return new(SourceState.NoData,CollectedAt:DateTimeOffset.UtcNow,SourceEnd:end,Detail:"No D1 analytics rows");
        var sum=a[0].GetProperty("sum");
        return new(SourceState.Ok,NonNegative(sum,"rowsRead"),NonNegative(sum,"rowsWritten"),DateTimeOffset.UtcNow,end,"rows/5m","Read and written rows in a 5-minute window; not query count");
    }
    private Task<JsonDocument> Graph(string query,DateTimeOffset start,DateTimeOffset end,CancellationToken ct)=>Request("graphql",JsonSerializer.Serialize(new {query,variables=new {accountTag=account,start=start.UtcDateTime.ToString("O"),end=end.UtcDateTime.ToString("O")}}),ct);
    public static JsonElement Rows(JsonElement root,string dataset)
    {
        try {
            var accounts=root.GetProperty("data").GetProperty("viewer").GetProperty("accounts");
            if(accounts.ValueKind!=JsonValueKind.Array||accounts.GetArrayLength()!=1)throw new SourceFailure(SourceState.Error,"Account analytics not available");
            var rows=accounts[0].GetProperty(dataset);
            if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>1)throw new SourceFailure(SourceState.Error,"Unexpected aggregate group count");
            return rows;
        }catch(Exception e)when(e is KeyNotFoundException or InvalidOperationException){throw new SourceFailure(SourceState.Error,"Analytics schema not supported");}
    }
    public async Task<Metric> R2(CancellationToken ct)
    {
        using var doc=await Request($"accounts/{account}/r2/metrics",null,ct);
        return ParseR2(doc.RootElement);
    }
    public static Metric ParseR2(JsonElement root)
    {
        var result=root.GetProperty("result");double bytes=0,objects=0;bool found=false;
        foreach(string kind in new[]{"standard","infrequentAccess"}){
            if(!result.TryGetProperty(kind,out var cl)||cl.ValueKind!=JsonValueKind.Object||!cl.TryGetProperty("published",out var p)||p.ValueKind!=JsonValueKind.Object)continue;
            bytes+=NonNegative(p,"payloadSize");objects+=NonNegative(p,"objects");found=true;
        }
        return found?new(SourceState.Ok,bytes/1073741824d,objects,DateTimeOffset.UtcNow,Unit:"GiB",Detail:"Published object payload; excludes metadata and unfinished uploads; source timestamp unavailable"):
            new(SourceState.NoData,CollectedAt:DateTimeOffset.UtcNow,Detail:"Published storage metrics unavailable");
    }
    public async Task<Metric> Cost(CancellationToken ct)
    {
        using var doc=await Request($"accounts/{account}/billable-usage",null,ct);
        return ParseCost(doc.RootElement,account);
    }
    public static Metric ParseCost(JsonElement root,string account)=>ParseCost(root,account,DateTimeOffset.UtcNow);
    public static Metric ParseCost(JsonElement root,string account,DateTimeOffset now)
    {
        var rows=root.GetProperty("result");if(rows.ValueKind!=JsonValueKind.Array)throw new SourceFailure(SourceState.Error,"Unsupported billing schema");
        if(rows.GetArrayLength()==0)return new(SourceState.NoData,CollectedAt:now,Detail:"No billing rows; not a zero-cost claim");
        var parsed=new List<(JsonElement Row,DateTimeOffset PeriodStart,DateTimeOffset? PeriodEnd)>();DateTimeOffset? currentStart=null;
        foreach(var row in rows.EnumerateArray())
        {
            if(row.GetProperty("BillingAccountId").GetString()!=account||row.GetProperty("ChargeCategory").GetString()!="Usage")throw new SourceFailure(SourceState.Error,"Billing scope mismatch");
            string Field(string name)=>row.TryGetProperty(name,out var x)?x.ToString():"";
            if(!DateTimeOffset.TryParse(Field("BillingPeriodStart"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var ps))
                throw new SourceFailure(SourceState.Partial,"Billing period start unavailable; no total");
            DateTimeOffset? pe=null;string periodEndText=Field("BillingPeriodEnd");
            if(periodEndText.Length>0)
            {
                if(!DateTimeOffset.TryParse(periodEndText,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var parsedEnd))
                    throw new SourceFailure(SourceState.Error,"Invalid billing period end");
                pe=parsedEnd;
            }
            if(pe.HasValue&&pe<=ps)throw new SourceFailure(SourceState.Error,"Invalid billing period order");
            parsed.Add((row,ps,pe));
            if(ps<=now&&(!currentStart.HasValue||ps>currentStart.Value))currentStart=ps;
        }
        if(!currentStart.HasValue)return new(SourceState.Partial,CollectedAt:now,Detail:"No billing period has started yet; no total");
        var selected=parsed.Where(x=>x.PeriodStart==currentStart.Value).ToArray();
        DateTimeOffset? periodEnd=selected[0].PeriodEnd;
        if(selected.Any(x=>x.PeriodEnd!=periodEnd))return new(SourceState.Partial,CollectedAt:now,Detail:"Current billing period end differs across rows; no total");
        decimal total=0;string? currency=null;DateTimeOffset? sourceEnd=null;HashSet<string> keys=[];
        foreach(var item in selected)
        {
            var row=item.Row;
            string Field(string name)=>row.TryGetProperty(name,out var x)?x.ToString():"";
            string cur=row.GetProperty("BillingCurrency").GetString()??"";
            if(!System.Text.RegularExpressions.Regex.IsMatch(cur,"^[A-Z]{3}$"))throw new SourceFailure(SourceState.Error,"Invalid billing currency");
            if(currency!=null&&currency!=cur)throw new SourceFailure(SourceState.Partial,"Multiple currencies in current billing period; no combined total");currency=cur;
            string key=JsonSerializer.Serialize(new[]{Field("ServiceName"),Field("SubscriptionId"),Field("ZoneId"),Field("BillingPeriodStart"),Field("ChargePeriodStart"),Field("ChargePeriodEnd"),Field("PricingUnit")});
            if(!keys.Add(key))throw new SourceFailure(SourceState.Partial,"Ambiguous duplicate charge periods; no combined total");
            // ContractedCost is per charge interval. Never sum CumulatedContractedCost.
            if(!row.GetProperty("ContractedCost").TryGetDecimal(out var cost)||cost<0)throw new SourceFailure(SourceState.Error,"Invalid usage charge");
            total=checked(total+cost);
            if(DateTimeOffset.TryParse(Field("ChargePeriodEnd"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var end)&&(!sourceEnd.HasValue||end>sourceEnd))sourceEnd=end;
        }
        int ignoredPeriods=parsed.Select(x=>x.PeriodStart).Distinct().Count()-1;
        string detail=(ignoredPeriods>0?$"Current billing period only; {ignoredPeriods} other billing period(s) returned by API were excluded. ":"")+
            "Returned usage records only; excludes fixed fees/tax/credits. Not a current complete total or invoice. Source older than 48h is marked stale (OpsDeck policy).";
        var metric=new Metric(SourceState.Ok,(double)total,null,now,sourceEnd,currency!,detail,currentStart,periodEnd,Note:"CURRENT PERIOD / USAGE",UsageCharges:true);
        return metric with{State=Freshness.MetricState(metric,now,7200)};
    }
    public static double NonNegative(JsonElement o,string key)
    {
        if(!o.TryGetProperty(key,out var n)||!n.TryGetDouble(out var v)||!double.IsFinite(v)||v<0)throw new SourceFailure(SourceState.Error,"Missing or invalid numeric metric");
        return v;
    }
    public void Dispose()=>http.Dispose();
}
