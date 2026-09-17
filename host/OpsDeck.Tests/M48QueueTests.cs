using System.Net;
using System.Text;
using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M48QueueTests
{
    private static readonly string Account=new('a',32),Id=new('1',32);
    private static QueueInfo Queue=>new(Account,Id,"test-queue");
    private static JsonElement Parse(string s)=>JsonDocument.Parse(s).RootElement.Clone();
    private static object Item(string id,string name="test-queue")=>new{queue_id=id,queue_name=name,settings=new{delivery_paused=false,message_retention_period=86400},consumers_total_count=1};
    private static string Page(object[] items,object? info)=>JsonSerializer.Serialize(new{success=true,result=items,result_info=info});
    private static object Info(int page=1,int total=1,int pages=1,int per=100)=>new{page,per_page=per,total_count=total,total_pages=pages};
    public static async Task Run(Action<string,bool> check)
    {
        void C(string n,bool v)=>check("m48-queues-"+n,v);
        async Task Reject(string n,Func<Task> act){try{await act();C(n,false);}catch(Exception e)when(e is SourceFailure or ArgumentException or InvalidOperationException){C(n,true);}}
        var now=DateTimeOffset.UtcNow;string prefix=$"accounts/{Account}/queues";
        foreach(string suffix in new[]{"?page=1&per_page=100","?page=10&per_page=100","/"+Id+"/metrics"})C("allow-"+suffix,CloudflareClient.IsReadOnlyQueuePath(Account,prefix+suffix));
        foreach(string suffix in new[]{"","/"+Id,"/"+Id+"/messages","/"+Id+"/messages/pull","/"+Id+"/purge","/../subscriptions","?page=0&per_page=100","?page=11&per_page=100","?page=1&per_page=100&extra=1","/"+Id+"/metrics?x=1"})C("deny-"+suffix,!CloudflareClient.IsReadOnlyQueuePath(Account,prefix+suffix));
        C("allowlist-account",!CloudflareClient.IsReadOnlyQueuePath(new string('b',32),prefix+"?page=1&per_page=100"));
        C("allowlist-account-injection",!CloudflareClient.IsReadOnlyQueuePath(".*",prefix+"?page=1&per_page=100"));
        QueueBacklog Read(string json)=>CloudflareClient.ParseQueueMetrics(Queue,Parse(json),now);
        var zero=Read("{\"backlog_count\":0,\"backlog_bytes\":0,\"oldest_message_timestamp_ms\":0}");
        C("known-zero",zero.State==SourceState.Ok&&zero.Messages==0&&zero.Bytes==0&&zero.OldestAt==null&&zero.OldestAgeSeconds==null);
        var absent=Read("{}");C("absent-not-zero",absent.State==SourceState.NoData&&absent.Messages==null&&absent.Bytes==null);
        var partial=Read("{\"backlog_count\":3,\"backlog_bytes\":100,\"oldest_message_timestamp_ms\":0}");C("unknown-oldest-partial",partial.State==SourceState.Partial&&partial.Messages==3&&partial.OldestAt==null);
        var live=Read(JsonSerializer.Serialize(new{backlog_count=4,backlog_bytes=1024,oldest_message_timestamp_ms=now.AddSeconds(-30).ToUnixTimeMilliseconds()}));
        C("timestamp-unit-ms",live.State==SourceState.Ok&&live.OldestAgeSeconds is >=30 and <31);
        C("freshness",live.IsCurrent(now)&&!live.IsCurrent(now.AddMinutes(4)));
        C("missing-bytes",Read("{\"backlog_count\":2}").State==SourceState.Partial);
        foreach(string bad in new[]{"{\"backlog_count\":-1}","{\"backlog_bytes\":0.5}","{\"backlog_count\":\"2\"}","[]","{\"oldest_message_timestamp_ms\":-1}"})
            await Reject("bad-metric-"+bad,()=>{Read(bad);return Task.CompletedTask;});
        await Reject("future-timestamp",()=>{Read(JsonSerializer.Serialize(new{oldest_message_timestamp_ms=now.AddHours(1).ToUnixTimeMilliseconds()}));return Task.CompletedTask;});
        var handler=new Handler((_,_)=>Ok(Page([Item(Id)],Info())));
        using(var cf=new CloudflareClient(Account,"offline-queue-test-token",handler))
        {
            var list=await cf.QueuesList(CancellationToken.None);
            C("list-complete",list.Complete&&list.State==SourceState.Ok&&list.Items.Length==1);
            C("list-metadata",list.Items[0].DeliveryPaused==false&&list.Items[0].RetentionSeconds==86400&&list.Items[0].Consumers==1);
            C("list-route",handler.Paths.Single()=="/client/v4/"+prefix+"?page=1&per_page=100"&&handler.AllGet&&handler.FixedHost);
        }
        foreach(var entry in new[]{("empty",Page([],Info(total:0,pages:0)),SourceState.Ok),
            ("missing-pagination",Page([Item(Id)],null),SourceState.Partial),
            ("changed-total",Page([Item(Id)],Info(total:2)),SourceState.Partial)})
        {
            using var cf=new CloudflareClient(Account,"offline-queue-test-token",new Handler((_,_)=>Ok(entry.Item2)));
            var list=await cf.QueuesList(CancellationToken.None);C(entry.Item1,list.State==entry.Item3&&list.Complete==(entry.Item3==SourceState.Ok));
        }
        using(var cf=new CloudflareClient(Account,"offline-queue-test-token",new Handler((n,_)=>Ok(Page([Item(n.ToString("x32"))],Info(n,2,2,1))))))
        {var list=await cf.QueuesList(CancellationToken.None);C("pagination-two-pages",list.Complete&&list.Items.Length==2);}
        var cap=new Handler((n,_)=>Ok(Page([Item(n.ToString("x32"))],Info(n,11,11,1))));
        using(var cf=new CloudflareClient(Account,"offline-queue-test-token",cap))
        {var list=await cf.QueuesList(CancellationToken.None);C("pagination-bound",!list.Complete&&list.Items.Length==10&&cap.Paths.Count==10);}
        foreach(var bad in new[]{Page([Item(Id),Item(Id)],Info(total:2)),Page([Item("../metrics")],Info()),Page([Item(Id,"bad\nname")],Info()),Page([Item(Id)],Info(page:2))})
        {using var cf=new CloudflareClient(Account,"offline-queue-test-token",new Handler((_,_)=>Ok(bad)));await Reject("bad-list",async()=>{await cf.QueuesList(CancellationToken.None);});}
        var mh=new Handler((_,_)=>Ok("{\"success\":true,\"result\":{\"backlog_count\":0,\"backlog_bytes\":0,\"oldest_message_timestamp_ms\":0}}"));
        using(var cf=new CloudflareClient(Account,"offline-queue-test-token",mh))
        {
            var x=await cf.QueueMetrics(Queue,CancellationToken.None);C("metrics-live-route",x.Messages==0&&mh.AllGet&&mh.FixedHost&&mh.Paths.Single().EndsWith("/"+Id+"/metrics",StringComparison.Ordinal));
            await Reject("metrics-account-identity",async()=>{await cf.QueueMetrics(Queue with{AccountId=new string('b',32)},CancellationToken.None);});
            await Reject("metrics-id-injection",async()=>{await cf.QueueMetrics(Queue with{Id="../messages"},CancellationToken.None);});
            var restricted=await cf.QueueMetrics(Queue with{Jurisdiction="eu"},CancellationToken.None);
            C("restricted-no-request",restricted.State==SourceState.NoData&&mh.Paths.Count==1);
        }
        foreach(int code in new[]{403,429,500})
        {
            var h=new Handler((_,_)=>{var r=new HttpResponseMessage((HttpStatusCode)code){Content=new StringContent("private-error-body")};if(code==429)r.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(600));return r;});
            using var cf=new CloudflareClient(Account,"offline-queue-test-token",h);
            try{await cf.QueuesList(CancellationToken.None);C("http-"+code,false);}catch(SourceFailure e){C("http-"+code,e.State==(code==403?SourceState.Denied:SourceState.Error)&&!e.Message.Contains("private-error-body")&&(code!=429||e.RetrySeconds==600));}
        }
        string temp=Path.Combine(Path.GetTempPath(),"opsdeck-queues-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            var settings=new LocalSettings(temp);var cfg=new HostConfig{Accounts=[new(){ProfileId="test",Name="Test",AccountId=Account,Enabled=true}]};
            await using var engine=new AppEngine(cfg,settings);
            var missing=await engine.ReadQueues(Account,CancellationToken.None);var cached=await engine.ReadQueues(Account,CancellationToken.None);
            C("engine-denial-not-empty",missing.State==SourceState.Denied&&!missing.Complete&&missing.RetrySeconds==900);
            C("engine-backoff-cache",ReferenceEquals(missing,cached));
            await Reject("engine-unknown-account",async()=>{await engine.ReadQueues(new string('b',32),CancellationToken.None);});
            await Reject("engine-undiscovered-queue",async()=>{await engine.ReadQueueBacklog(Queue,CancellationToken.None);});
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try{await engine.ReadQueues(Account,cancel.Token);C("engine-cancel",false);}catch(OperationCanceledException){C("engine-cancel",true);}
        }
        finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
    }
    private static HttpResponseMessage Ok(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")};
    private sealed class Handler(Func<int,HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    {
        public List<string> Paths=[];public bool AllGet=true,FixedHost=true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();AllGet&=request.Method==HttpMethod.Get;FixedHost&=request.RequestUri?.Host=="api.cloudflare.com";Paths.Add(request.RequestUri!.PathAndQuery);return Task.FromResult(response(Paths.Count,request));}
    }
}
