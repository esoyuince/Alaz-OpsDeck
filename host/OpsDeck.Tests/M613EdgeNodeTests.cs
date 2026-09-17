using System.Net;
using System.Text;
using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;

public static class M613EdgeNodeTests
{
    public static async Task Run(Action<string,bool> check)
    {
        check("edge-endpoint-tsnet",EdgeNodeEndpoint.Validate("http://alaz-edge.example.ts.net:8787/latest.json").Port==8787);
        check("edge-endpoint-cgnat",EdgeNodeEndpoint.Validate("http://100.64.0.10:8787/latest.json").Host=="100.64.0.10");
        foreach(string bad in new[]{
            "http://192.168.1.2:8787/latest.json","http://1.1.1.1:8787/latest.json",
            "https://alaz-edge.example.ts.net:8787/latest.json","http://alaz-edge.example.ts.net:8788/latest.json",
            "http://alaz-edge.example.ts.net:8787/other","http://u:p@alaz-edge.example.ts.net:8787/latest.json"})
        {
            try{EdgeNodeEndpoint.Validate(bad);check("edge-reject-"+bad,false);}catch(ArgumentException){check("edge-reject-"+bad,true);}
        }
        var now=DateTimeOffset.UtcNow;
        using(var client=Client(Payload(now,"0x0"))){var r=await client.Read(CancellationToken.None);check("edge-live-ok",r.State==SourceState.Ok&&r.Snapshot?.Host=="alaz-edge");}
        using(var client=Client(Payload(now.AddSeconds(-120),"0x0"))){var r=await client.Read(CancellationToken.None);check("edge-stale",r.State==SourceState.Stale);}
        using(var client=Client(Payload(now,"0x50000"))){var r=await client.Read(CancellationToken.None);check("edge-throttle-partial",r.State==SourceState.Partial);}
        using(var client=Client("{\"bad\":true}")){var r=await client.Read(CancellationToken.None);check("edge-invalid-payload",r.State==SourceState.Error);}
    }

    public static async Task<int> Live()
    {
        string? endpoint=Environment.GetEnvironmentVariable("OPSDECK_EDGE_STATUS_URL");
        if(string.IsNullOrWhiteSpace(endpoint)){Console.WriteLine("OPSDECK_EDGE_STATUS_URL is not set; live edge probe skipped.");return 2;}
        using var client=new EdgeNodeStatusClient(EdgeNodeEndpoint.Validate(endpoint));
        var reading=await client.Read(CancellationToken.None);
        Console.WriteLine($"state={reading.State} detail={reading.Detail} host={reading.Snapshot?.Host} ts_ip={reading.Snapshot?.TailscaleIp} temp={reading.Snapshot?.TempC}");
        return reading.State is SourceState.Ok or SourceState.Partial?0:1;
    }

    private static EdgeNodeStatusClient Client(string body)=>new(
        new Uri("http://100.64.0.10:8787/latest.json"),
        new Handler(HttpStatusCode.OK,body));

    private static string Payload(DateTimeOffset ts,string throttled)=>JsonSerializer.Serialize(new{
        ts,host="alaz-edge",temp_c=40.2,throttled,load1=0.12,uptime_s=5000,
        mem_total_kb=3886840,mem_available_kb=3500000,disk_used_pct=7,
        eth_ip="192.168.1.76",wifi_ip="192.168.1.95",tailscale_ip="100.64.0.10",
        services=new{homeassistant="exited",mosquitto="running",edge_status="running",tailscale="active"}},Json.Options);

    private sealed class Handler(HttpStatusCode status,string body):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
            =>Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(body,Encoding.UTF8,"application/json")});
    }
}
