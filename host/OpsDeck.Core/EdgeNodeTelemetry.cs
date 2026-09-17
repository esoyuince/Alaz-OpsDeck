using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace OpsDeck.Core;

public sealed record EdgeNodeServices(
    string Homeassistant="",
    string Mosquitto="",
    string EdgeStatus="",
    string Tailscale="");

public sealed record EdgeNodeSnapshot(
    DateTimeOffset Ts,
    string Host,
    double TempC,
    string Throttled,
    double Load1,
    long UptimeS,
    long MemTotalKb,
    long MemAvailableKb,
    int DiskUsedPct,
    string EthIp,
    string WifiIp,
    string TailscaleIp,
    EdgeNodeServices Services);

public sealed record EdgeNodeReading(SourceState State,EdgeNodeSnapshot? Snapshot=null,DateTimeOffset? CollectedAt=null,string Detail="")
{
    public static EdgeNodeReading Setup=>new(SourceState.Setup,Detail:"EdgeNode disabled");
}

public static class EdgeNodeEndpoint
{
    public static Uri Validate(string value)
    {
        if(value.Length>512||!Uri.TryCreate(value,UriKind.Absolute,out var u)||u.Scheme!="http"||u.Port!=8787||u.UserInfo!=""||u.Query!=""||u.Fragment!=""||u.AbsolutePath!="/latest.json")
            throw new ArgumentException("EdgeNode endpoint must be http://<tailscale-host>:8787/latest.json without credentials or query strings.");
        if(IPAddress.TryParse(u.Host,out var ip))
        {
            if(ip.AddressFamily!=AddressFamily.InterNetwork||!IsTailscaleIPv4(ip))throw new ArgumentException("EdgeNode IP must be inside Tailscale 100.64.0.0/10.");
        }
        else if(!u.Host.EndsWith(".ts.net",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("EdgeNode hostname must be a Tailscale .ts.net name.");
        return u;
    }
    private static bool IsTailscaleIPv4(IPAddress ip)
    {
        var b=ip.GetAddressBytes();return b.Length==4&&b[0]==100&&b[1]>=64&&b[1]<=127;
    }
}

public sealed class EdgeNodeStatusClient : IDisposable
{
    private const int MaxPayloadBytes=16*1024;
    private readonly HttpClient http;
    private readonly Uri endpoint;
    public EdgeNodeStatusClient(Uri endpoint,HttpMessageHandler? handler=null)
    {
        this.endpoint=EdgeNodeEndpoint.Validate(endpoint.AbsoluteUri);
        http=handler==null?new HttpClient():new HttpClient(handler,false);
        http.Timeout=Timeout.InfiniteTimeSpan;
    }
    public async Task<EdgeNodeReading> Read(CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response=await http.GetAsync(endpoint,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if(!response.IsSuccessStatusCode)return new(SourceState.Error,CollectedAt:DateTimeOffset.UtcNow,Detail:$"HTTP {(int)response.StatusCode}");
            if(response.Content.Headers.ContentLength is long len&&len>MaxPayloadBytes)return new(SourceState.Error,CollectedAt:DateTimeOffset.UtcNow,Detail:"Payload too large");
            byte[] payload=await response.Content.ReadAsByteArrayAsync(timeout.Token);
            if(payload.Length>MaxPayloadBytes)return new(SourceState.Error,CollectedAt:DateTimeOffset.UtcNow,Detail:"Payload too large");
            EdgeNodeSnapshot? snapshot=JsonSerializer.Deserialize<EdgeNodeSnapshot>(payload,Json.Options);
            if(snapshot==null||!Valid(snapshot))return new(SourceState.Error,CollectedAt:DateTimeOffset.UtcNow,Detail:"Invalid EdgeNode payload");
            var now=DateTimeOffset.UtcNow;var age=now-snapshot.Ts;
            if(age<TimeSpan.FromSeconds(-30))return new(SourceState.Error,snapshot,now,"Timestamp is in the future");
            SourceState state=age>TimeSpan.FromSeconds(90)?SourceState.Stale:snapshot.Throttled=="0x0"?SourceState.Ok:SourceState.Partial;
            return new(state,snapshot,now,state==SourceState.Stale?$"Stale {Math.Max(0,(int)age.TotalSeconds)}s":snapshot.Throttled=="0x0"?"Live":"Throttling flagged");
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return new(SourceState.Error,CollectedAt:DateTimeOffset.UtcNow,Detail:"Timeout");}
        catch(HttpRequestException e){return new(SourceState.Error,CollectedAt:DateTimeOffset.UtcNow,Detail:e.GetType().Name);}
        catch(JsonException){return new(SourceState.Error,CollectedAt:DateTimeOffset.UtcNow,Detail:"Invalid JSON");}
    }
    private static bool Valid(EdgeNodeSnapshot s)
    {
        if(string.IsNullOrWhiteSpace(s.Host)||s.Host.Length>64||s.TempC is < -30 or >120||s.Load1 is <0 or >1000||s.UptimeS<0||s.MemTotalKb<=0||s.MemAvailableKb<0||s.MemAvailableKb>s.MemTotalKb||s.DiskUsedPct is <0 or >100)return false;
        if(s.Services is null||string.IsNullOrWhiteSpace(s.Throttled)||s.Throttled.Length>18||s.TailscaleIp is null||s.EthIp is null||s.WifiIp is null||s.TailscaleIp.Length>64||s.EthIp.Length>64||s.WifiIp.Length>64)return false;
        return true;
    }
    public void Dispose()=>http.Dispose();
}
