using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
namespace OpsDeck.Core;

public sealed record SiteResult(string Host,int? HttpCode,double? HeaderMs,bool Healthy,string State,
    int Redirects=0,string? FinalHost=null,int? FirstHttpCode=null,string? Url=null);
public sealed class HealthChecks : IDisposable
{
    private readonly HttpClient http;
    public HealthChecks(HttpMessageHandler? handler=null)
    {
        http=new(handler??new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false,
            PooledConnectionLifetime=TimeSpan.FromMinutes(2),ConnectCallback=ConnectPublic}){Timeout=TimeSpan.FromSeconds(6)};
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpsDeck/0.2 (+read-only-health)");
    }
    public const int MaxRedirects=3;
    public async Task<SiteResult> Check(string url,CancellationToken ct)
    {
        var original=HostConfig.ValidateSite(url);var uri=original;var clock=Stopwatch.StartNew();
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(8));
        var seen=new HashSet<string>(StringComparer.Ordinal){original.AbsoluteUri};int redirects=0;int? first=null;
        SiteResult Result(int? code,bool healthy,string state)=>new(original.Host,code,clock.Elapsed.TotalMilliseconds,healthy,state,redirects,uri.Host,first,original.AbsoluteUri);
        try {
            while(true){
                budget.Token.ThrowIfCancellationRequested();
                using var req=new HttpRequestMessage(HttpMethod.Get,uri);
                // Each hop opens only a validated public HTTPS target. No body, cookies, auth or auto-redirect.
                using var res=await http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,budget.Token);
                int code=(int)res.StatusCode;first??=code;
                if(code>=200&&code<300)return Result(code,true,redirects==0?$"HTTP {code}":$"HTTP {first} -> {code} ({redirects} redirect; {uri.Host})");
                if(code is not (301 or 302 or 303 or 307 or 308))return Result(code,false,$"HTTP {code}");
                if(redirects>=MaxRedirects)return Result(code,false,"Redirect limit reached");
                var location=res.Headers.Location;
                if(location==null)return Result(code,false,"Redirect target missing");
                Uri next;
                try{next=ValidateRedirect(uri,location);}catch(ArgumentException){return Result(code,false,"Unsafe/unsupported redirect target");}
                if(!seen.Add(next.AbsoluteUri))return Result(code,false,"Redirect loop");
                uri=next;redirects++;
            }
        }catch(Exception e)when(e is HttpRequestException or OperationCanceledException){
            ct.ThrowIfCancellationRequested();return Result(null,false,e is OperationCanceledException?"Timeout":"Connection/TLS/DNS failure");
        }
    }
    public static Uri ValidateRedirect(Uri current,Uri location)
    {
        Uri candidate;
        try{candidate=location.IsAbsoluteUri?location:new Uri(current,location);}catch(UriFormatException e){throw new ArgumentException("Invalid redirect",e);}
        var target=HostConfig.ValidateSite(candidate.AbsoluteUri);
        if(IPAddress.TryParse(target.IdnHost.Trim('[',']'),out var ip)&&!IsPublicAddress(ip))throw new ArgumentException("Non-public redirect");
        // Host names are resolved and revalidated against private addresses by ConnectPublic for every connection.
        return target;
    }
    private static async ValueTask<Stream> ConnectPublic(SocketsHttpConnectionContext ctx,CancellationToken ct)
    {
        var addresses=await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host,ct);
        var safe=addresses.Where(IsPublicAddress).ToArray();
        if(safe.Length==0||safe.Length!=addresses.Length)throw new HttpRequestException("Health target resolves to a non-public address.");
        foreach(var address in safe){
            var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp){NoDelay=true};
            try{await socket.ConnectAsync(new IPEndPoint(address,ctx.DnsEndPoint.Port),ct);return new NetworkStream(socket,true);}
            catch(SocketException){socket.Dispose();}
            catch{socket.Dispose();throw;}
        }
        throw new HttpRequestException("No reachable public address.");
    }
    public static bool IsPublicAddress(IPAddress ip)
    {
        if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();
        if(IPAddress.IsLoopback(ip))return false;
        var b=ip.GetAddressBytes();
        if(b.Length==16)return (b[0]&0xe0)==0x20 && !(b[0]==0x20&&b[1]==1&&b[2]==0x0d&&b[3]==0xb8);
        return b[0]>0&&b[0]<224&&b[0]!=10&&b[0]!=127&&!(b[0]==100&&b[1]>=64&&b[1]<=127)&&
            !(b[0]==169&&b[1]==254)&&!(b[0]==172&&b[1]>=16&&b[1]<=31)&&
            !(b[0]==192&&(b[1]==168||b[1]==0))&&!(b[0]==198&&(b[1]==18||b[1]==19||b[1]==51&&b[2]==100))&&
            !(b[0]==203&&b[1]==0&&b[2]==113);
    }
    public void Dispose()=>http.Dispose();
}
