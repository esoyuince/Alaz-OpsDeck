using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
namespace OpsDeck.Core;

// GET-only discovery. Raw responses (including Pages environment variables) never leave this reader.
public sealed class InventoryClient : IDisposable
{
    public const int PageSize=50, PagesPageSize=10, MaxPages=100, MaxItems=1000;
    private readonly HttpClient http; private readonly CloudAccountConfig profile;
    private readonly SemaphoreSlim gate; private readonly bool ownGate; private DateTimeOffset notBefore;
    public InventoryClient(CloudAccountConfig profile,string token,SemaphoreSlim? sharedGate=null,HttpMessageHandler? handler=null)
    {
        profile.Validate(); if(profile.AccountId.Length!=32) throw new ArgumentException("Account is required.");
        this.profile=profile with{AccountId=profile.AccountId.ToLowerInvariant()};
        gate=sharedGate??new SemaphoreSlim(2); ownGate=sharedGate==null;
        http=new(handler??new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false})
            {Timeout=TimeSpan.FromSeconds(12),MaxResponseContentBufferSize=2*1024*1024};
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AlazOpsDeck/0.4 (read-only-inventory)");
    }
    private async Task<JsonDocument> Get(ResourceKind kind,int page,string? cursor,CancellationToken ct)
    {
        if(DateTimeOffset.UtcNow<notBefore) throw new SourceFailure(SourceState.Error,"Account discovery is cooling down",(int)Math.Ceiling((notBefore-DateTimeOffset.UtcNow).TotalSeconds));
        string path=kind switch {ResourceKind.Worker=>"workers/scripts",ResourceKind.D1=>"d1/database",ResourceKind.R2=>"r2/buckets",ResourceKind.Pages=>"pages/projects",_=>throw new ArgumentException("Unsupported kind")};
        string query=kind==ResourceKind.Worker?"":kind==ResourceKind.R2?"?per_page=50&order=name&direction=asc"+(cursor==null?"":"&cursor="+Uri.EscapeDataString(cursor)):$"?page={page}&per_page={(kind==ResourceKind.Pages?PagesPageSize:PageSize)}";
        using var req=new HttpRequestMessage(HttpMethod.Get,$"https://api.cloudflare.com/client/v4/accounts/{profile.AccountId}/{path}{query}");
        if(kind==ResourceKind.R2) req.Headers.Add("cf-r2-jurisdiction","default");
        await gate.WaitAsync(ct);
        try {
            using var response=await http.SendAsync(req,ct);
            if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new SourceFailure(SourceState.Denied,"Read permission denied",900);
            if((int)response.StatusCode==429) {
                var retry=response.Headers.RetryAfter;
                int seconds=(int)Math.Clamp(retry?.Delta?.TotalSeconds??(retry?.Date-DateTimeOffset.UtcNow)?.TotalSeconds??120,60,86400);
                notBefore=DateTimeOffset.UtcNow.AddSeconds(seconds); throw new SourceFailure(SourceState.Error,"Rate limited",seconds);
            }
            if(!response.IsSuccessStatusCode) throw new SourceFailure(SourceState.Error,$"HTTP {(int)response.StatusCode}");
            var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct),new JsonDocumentOptions{MaxDepth=48});
            try {
                var root=doc.RootElement;
                if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty("success",out var ok) || ok.ValueKind!=JsonValueKind.True ||
                    (root.TryGetProperty("errors",out var errors) && (errors.ValueKind!=JsonValueKind.Array || errors.GetArrayLength()>0)))
                    throw new SourceFailure(SourceState.Error,"Discovery envelope did not confirm success");
                return doc;
            } catch {doc.Dispose();throw;}
        } finally {gate.Release();}
    }
    public async Task<ResourceSet> Read(ResourceKind kind,CancellationToken ct)
    {
        if(!Enum.IsDefined(kind)) throw new ArgumentException("Unsupported kind");
        var items=new List<CloudResource>(); var keys=new HashSet<ResourceKey>(); var cursors=new HashSet<string>(StringComparer.Ordinal);
        string? cursor=null; int? expected=null;
        string scope=kind==ResourceKind.R2?"default":"account";
        ResourceSet Result(SourceState state,bool complete,string detail,int retry=0)=>new(profile.AccountId,profile.Name,kind,state,items.ToArray(),complete,DateTimeOffset.UtcNow,detail,retry);
        try {
            for(int page=1;page<=MaxPages;page++) {
                ct.ThrowIfCancellationRequested(); using var doc=await Get(kind,page,cursor,ct); var root=doc.RootElement;
                var rows=root.GetProperty("result"); if(kind==ResourceKind.R2) rows=rows.GetProperty("buckets");
                if(rows.ValueKind!=JsonValueKind.Array) throw new SourceFailure(SourceState.Error,"Missing resource list");
                int count=rows.GetArrayLength(); JsonElement info=default;
                if(root.TryGetProperty("result_info",out var ri) && ri.ValueKind!=JsonValueKind.Null) {
                    if(ri.ValueKind!=JsonValueKind.Object) throw new SourceFailure(SourceState.Error,"Invalid pagination metadata"); info=ri;
                    if(kind is ResourceKind.D1 or ResourceKind.Pages) {
                        if(info.TryGetProperty("page",out var pn) && (!pn.TryGetInt32(out int p)||p!=page)) throw new SourceFailure(SourceState.Partial,"Unexpected page number");
                    }
                    if(info.TryGetProperty("total_count",out var tc)) {
                        if(!tc.TryGetInt32(out int total)||total<0) throw new SourceFailure(SourceState.Partial,"Invalid result count");
                        if(expected.HasValue&&expected.Value!=total) throw new SourceFailure(SourceState.Partial,"Inventory changed while paging; retry later"); expected=total;
                    }
                }
                foreach(var row in rows.EnumerateArray()) {
                    string id=Text(row,kind==ResourceKind.D1?"uuid":kind==ResourceKind.R2?"name":"id",256);
                    string name=kind==ResourceKind.Worker?id:Text(row,"name",256);
                    var key=new ResourceKey(profile.AccountId,kind,scope,id); key.Validate();
                    if(!keys.Add(key)) throw new SourceFailure(SourceState.Partial,"Duplicate resource or repeated page; list is incomplete");
                    if(items.Count>=MaxItems) throw new SourceFailure(SourceState.Partial,"Resource display limit reached; list is incomplete");
                    items.Add(new(key,name,kind==ResourceKind.Pages?Deployment(row):"Not queried"));
                }
                if(expected.HasValue&&items.Count>expected.Value) throw new SourceFailure(SourceState.Partial,"Result total mismatch");
                if(kind==ResourceKind.R2) {
                    cursor=info.ValueKind==JsonValueKind.Object&&info.TryGetProperty("cursor",out var c)&&c.ValueKind!=JsonValueKind.Null?c.GetString():null;
                    if(!string.IsNullOrEmpty(cursor)) {
                        if(cursor.Length>2048||cursor.Any(char.IsControl)||!cursors.Add(cursor)) throw new SourceFailure(SourceState.Partial,"Invalid/repeated cursor");
                        continue;
                    }
                } else if(kind!=ResourceKind.Worker && count>0 && (!expected.HasValue||items.Count<expected.Value)) continue;
                if(expected.HasValue&&items.Count!=expected.Value) throw new SourceFailure(SourceState.Partial,"List ended before the advertised total");
                return Result(SourceState.Ok,true,kind==ResourceKind.R2?"Complete for default jurisdiction only; EU/US/restricted jurisdictions not queried":"Complete listing; presence is not health");
            }
            return Result(SourceState.Partial,false,"Page limit reached; list is incomplete");
        } catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
        catch(SourceFailure e) {return Result(e.State,false,e.Message,e.RetrySeconds);}
        catch(Exception e) when(e is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException) {
            return Result(SourceState.Error,false,"Discovery failed or unsupported schema; previous rows may be partial");
        }
    }
    private static string Text(JsonElement row,string field,int max)
    {
        if(row.ValueKind!=JsonValueKind.Object || !row.TryGetProperty(field,out var v) || v.ValueKind!=JsonValueKind.String) throw new SourceFailure(SourceState.Error,"Resource ID/name missing");
        var s=v.GetString()!; if(string.IsNullOrWhiteSpace(s)||s.Length>max||s.Any(char.IsControl)) throw new SourceFailure(SourceState.Error,"Invalid resource ID/name"); return s;
    }
    private static string Deployment(JsonElement row)
    {
        if(!row.TryGetProperty("canonical_deployment",out var d)||d.ValueKind!=JsonValueKind.Object) return "Production deployment unavailable";
        if(!d.TryGetProperty("environment",out var e)||e.GetString()!="production") return "Production scope unconfirmed";
        if(!d.TryGetProperty("latest_stage",out var s)||s.ValueKind!=JsonValueKind.Object) return "Production stage unavailable";
        string name=Text(s,"name",32),state=Text(s,"status",32);
        return (name is "queued" or "initialize" or "clone_repo" or "build" or "deploy") && (state is "idle" or "active" or "success" or "failure" or "canceled") ? $"Production {name}: {state}" : "Production stage unknown";
    }
    public void Dispose() {http.Dispose();if(ownGate)gate.Dispose();}
}
