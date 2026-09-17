using System.Net;
using System.Text;
using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M46WorkerTests
{
    public static async Task Run(Action<string,bool> check)
    {
        void C(string n,bool v)=>check("m46-worker-"+n,v);
        void Reject(string n,Action a){try{a();C(n,false);}catch(Exception e)when(e is SourceFailure or ArgumentException or InvalidOperationException){C(n,true);}}
        JsonElement J(string s)=>JsonDocument.Parse(s).RootElement.Clone();
        var key=new ResourceKey(new string('a',32),ResourceKind.Worker,"account","demo");var now=new DateTimeOffset(2026,9,14,12,0,0,TimeSpan.Zero);var start=now.AddMinutes(-8);var end=now.AddMinutes(-3);
        Dictionary<string,string> descriptions=new(){["cpuTimeP50"]="CPU time 50th percentile - microseconds",["cpuTimeP99"]="CPU time 99th percentile - microseconds",["wallTimeP50"]="Wall time milliseconds",["wallTimeP99"]="Wall time milliseconds"};
        string Row(string script="demo",double req=100,double err=2,double cpu50=1500,double cpu99=3000)=>JsonSerializer.Serialize(new{dimensions=new{scriptName=script},sum=new{requests=req,errors=err},quantiles=new{__typename="AccountWorkerQuantiles",cpuTimeP50=cpu50,cpuTimeP99=cpu99,wallTimeP50=4d,wallTimeP99=6d}});
        var sample=CloudflareClient.ParseWorkerDetail(J(Row()),key,start,end,now,descriptions,true);
        C("rate",sample.Requests==100&&sample.Errors==2&&sample.ErrorPercent==2);
        C("verified-us-to-ms",sample.CpuP50Ms==1.5&&sample.CpuP99Ms==3);
        C("wall-independent",sample.WallP50Ms==4&&sample.WallP99Ms==6);
        C("source-window",sample.Start==start&&sample.End==end&&sample.CollectedAt==now);
        C("no-cpu-billing-estimate",sample.Detail.Contains("faturalanan toplam CPU değildir"));
        var unknown=CloudflareClient.ParseWorkerDetail(J(Row()),key,start,end,now,new Dictionary<string,string>(),false);
        C("unknown-units-not-guessed",unknown.CpuP50Ms==null&&unknown.WallP50Ms==null&&unknown.Requests==100&&unknown.State==SourceState.Partial);
        C("zero-denominator-undefined",CloudflareClient.ParseWorkerDetail(J(Row(req:0,err:0)),key,start,end,now,descriptions,true).ErrorPercent==null);
        C("zero-count-no-quantiles",CloudflareClient.ParseWorkerDetail(J(Row(req:0,err:0)),key,start,end,now,descriptions,true).CpuP50Ms==null);
        C("no-wall-fallback-to-cpu",CloudflareClient.ParseWorkerDetail(J(Row()),key,start,end,now,descriptions,false).WallP50Ms==null);
        C("stale-boundary",sample.IsCurrent(now.AddSeconds(180))&&!sample.IsCurrent(now.AddSeconds(181)));
        Reject("wrong-identity",()=>CloudflareClient.ParseWorkerDetail(J(Row(script:"different")),key,start,end,now,descriptions,true));
        Reject("errors-over-requests",()=>CloudflareClient.ParseWorkerDetail(J(Row(err:101)),key,start,end,now,descriptions,true));
        Reject("negative-count",()=>CloudflareClient.ParseWorkerDetail(J(Row(req:-1)),key,start,end,now,descriptions,true));
        Reject("negative-cpu",()=>CloudflareClient.ParseWorkerDetail(J(Row(cpu50:-1)),key,start,end,now,descriptions,true));
        Reject("inverted-percentiles",()=>CloudflareClient.ParseWorkerDetail(J(Row(cpu50:4000)),key,start,end,now,descriptions,true));
        Reject("wrong-window",()=>CloudflareClient.ParseWorkerDetail(J(Row()),key,start.AddSeconds(1),end,now,descriptions,true));
        Reject("wrong-kind",()=>CloudflareClient.ParseWorkerDetail(J(Row()),key with{Kind=ResourceKind.D1},start,end,now,descriptions,true));
        C("units-us",CloudflareClient.MillisecondsPerUnit("CPU microseconds")==.001);
        C("units-ns",CloudflareClient.MillisecondsPerUnit("CPU nanoseconds")==.000001);
        C("units-ms",CloudflareClient.MillisecondsPerUnit("CPU milliseconds")==1);
        C("units-s",CloudflareClient.MillisecondsPerUnit("CPU seconds")==1000);
        C("ambiguous-unit-rejected",CloudflareClient.MillisecondsPerUnit("milliseconds or microseconds")==null);
        C("missing-unit-rejected",CloudflareClient.MillisecondsPerUnit("CPU time")==null&&CloudflareClient.MillisecondsPerUnit(null)==null);
        string Schema(bool wall)=>JsonSerializer.Serialize(new{data=new{__type=new{fields=descriptions.Where(p=>wall||!p.Key.StartsWith("wall",StringComparison.Ordinal)).Select(p=>new{name=p.Key,description=p.Value}).ToArray()}}});
        C("schema-fields",CloudflareClient.ParseTimeDescriptions(J(Schema(true))).Count==4);
        Reject("duplicate-schema",()=>CloudflareClient.ParseTimeDescriptions(J("{\"data\":{\"__type\":{\"fields\":[{\"name\":\"cpuTimeP50\",\"description\":\"microseconds\"},{\"name\":\"cpuTimeP50\",\"description\":\"seconds\"}]}}}")));
        string Envelope(string row)=>"{\"data\":{\"viewer\":{\"accounts\":[{\"workersInvocationsAdaptive\":["+row+"]}]}}}";
        foreach(bool withWall in new[]{true,false})
        {
            var handler=new Handler(Envelope(Row()),Schema(withWall));using var client=new CloudflareClient(key.AccountId,"offline-not-real-token",handler);
            var read=await client.WorkerDetail(key,CancellationToken.None);
            C("query-count-"+withWall,handler.Bodies.Count==(withWall?3:2));
            C("queried-values-"+withWall,read.Requests==100&&read.CpuP50Ms==1.5&&(withWall?read.WallP99Ms==6:read.WallP99Ms==null));
            C("fixed-host-post-"+withWall,handler.SafeRoute);
            using var query=JsonDocument.Parse(handler.Bodies[0]);var vars=query.RootElement.GetProperty("variables");
            C("script-variable-"+withWall,vars.GetProperty("scriptName").GetString()==key.Id);
            C("bounded-read-query-"+withWall,query.RootElement.GetProperty("query").GetString()!.Contains("limit: 1")&&!handler.Bodies.Any(s=>s.Contains("mutation")));
        }
        var emptyHandler=new Handler(Envelope(""),Schema(true));using(var client=new CloudflareClient(key.AccountId,"offline-not-real-token",emptyHandler)){var read=await client.WorkerDetail(key,CancellationToken.None);C("empty-not-zero",read.State==SourceState.NoData&&read.Requests==null&&emptyHandler.Bodies.Count==1);}
        var zeroHandler=new Handler(Envelope(Row(req:0,err:0)),Schema(true));using(var client=new CloudflareClient(key.AccountId,"offline-not-real-token",zeroHandler)){var read=await client.WorkerDetail(key,CancellationToken.None);C("zero-skips-time-queries",read.Requests==0&&read.CpuP50Ms==null&&zeroHandler.Bodies.Count==1);}
        var deniedSchema=new Handler(Envelope(Row()),"{\"errors\":[{\"message\":\"not permitted\"}]}");using(var client=new CloudflareClient(key.AccountId,"offline-not-real-token",deniedSchema)){var read=await client.WorkerDetail(key,CancellationToken.None);C("schema-denied-keeps-counts",read.Requests==100&&read.CpuP50Ms==null&&read.State==SourceState.Partial);}
        var wrongAccount=new Handler(Envelope(Row()),Schema(true));using(var client=new CloudflareClient(new string('b',32),"offline-not-real-token",wrongAccount)){try{await client.WorkerDetail(key,CancellationToken.None);C("account-guard",false);}catch(ArgumentException){C("account-guard",wrongAccount.Bodies.Count==0);}}
        var foreign=new Handler(Envelope(Row(script:"other")),Schema(true));using(var client=new CloudflareClient(key.AccountId,"offline-not-real-token",foreign)){try{await client.WorkerDetail(key,CancellationToken.None);C("reply-identity-guard",false);}catch(SourceFailure){C("reply-identity-guard",true);}}
    }
    private sealed class Handler(string row,string schema):HttpMessageHandler
    {
        public List<string> Bodies=[];public bool SafeRoute=true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();SafeRoute&=request.Method==HttpMethod.Post&&request.RequestUri?.AbsoluteUri=="https://api.cloudflare.com/client/v4/graphql";
            string body=await request.Content!.ReadAsStringAsync(ct);Bodies.Add(body);
            return new(HttpStatusCode.OK){Content=new StringContent(body.Contains("OpsDeckWorkerUnits",StringComparison.Ordinal)?schema:row,Encoding.UTF8,"application/json")};
        }
    }
}
