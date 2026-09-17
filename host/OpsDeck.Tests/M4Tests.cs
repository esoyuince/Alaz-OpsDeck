using System.Net;
using System.Text;
using System.Text.Json;
using OpsDeck.Core;
internal static class M4Tests
{
    public static async Task Run(Action<string,bool> Check,Action<string,Action> Throws)
    {
        const string aid="11111111111111111111111111111111",bid="22222222222222222222222222222222";
        CloudAccountConfig Profile(string id=aid)=>new(){ProfileId="p",Name="Profile",AccountId=id,Enabled=true};
        string Envelope(string rows,string? info=null)=>"{\"success\":true,\"errors\":[],\"result\":"+rows+(info==null?"":",\"result_info\":"+info)+"}";
        var worker=new InventoryHandler((n,r)=>Envelope("[{\"id\":\"api\",\"bindings\":[{\"secret\":\"PRIVATE_SENTINEL\"}]}]"));
        using(var client=new InventoryClient(Profile(),"fake-inventory-token",handler:worker)) {
            var data=await client.Read(ResourceKind.Worker,CancellationToken.None);
            Check("m4-worker-list",data.Complete&&data.Items.Length==1&&data.Items[0].Key.AccountId==aid);
            Check("m4-get-only-allowlisted",worker.Methods.All(m=>m==HttpMethod.Get)&&worker.Uris.All(u=>u.Host=="api.cloudflare.com"&&u.AbsolutePath==$"/client/v4/accounts/{aid}/workers/scripts"));
            Check("m4-no-raw-binding-persistence",!JsonSerializer.Serialize(data).Contains("PRIVATE_SENTINEL"));
            Check("m4-worker-presence-not-health",data.Items[0].Deployment=="Not queried");
        }
        var d1=new InventoryHandler((n,r)=>Envelope($"[{{\"uuid\":\"db{n}\",\"name\":\"Database {n}\"}}]",$"{{\"page\":{n},\"per_page\":1,\"total_count\":2}}"));
        using(var client=new InventoryClient(Profile(),"fake-inventory-token",handler:d1)) {
            var data=await client.Read(ResourceKind.D1,CancellationToken.None);
            Check("m4-d1-pages-followed",data.Complete&&data.Items.Length==2&&d1.Uris[1].Query.Contains("page=2"));
        }
        var pages=new InventoryHandler((n,r)=>Envelope(n==1?"[{\"id\":\"p1\",\"name\":\"website\",\"canonical_deployment\":{\"environment\":\"production\",\"latest_stage\":{\"name\":\"deploy\",\"status\":\"success\"},\"env_vars\":{\"SECRET\":\"PRIVATE_SENTINEL\"}}}]":"[]"));
        using(var client=new InventoryClient(Profile(),"fake-inventory-token",handler:pages)) {
            var data=await client.Read(ResourceKind.Pages,CancellationToken.None);
            Check("m4-missing-page-metadata-drain",data.Complete&&pages.Uris.Count==2);
            Check("m4-production-stage-not-uptime",data.Items[0].Deployment=="Production deploy: success");
            Check("m4-no-env-var-persistence",!JsonSerializer.Serialize(data).Contains("PRIVATE_SENTINEL"));
        }
        var r2=new InventoryHandler((n,r)=>Envelope($"{{\"buckets\":[{{\"name\":\"bucket{n}\"}}]}}",n==1?"{\"cursor\":\"next+/=&x\"}":"{}"));
        using(var client=new InventoryClient(Profile(),"fake-inventory-token",handler:r2)) {
            var data=await client.Read(ResourceKind.R2,CancellationToken.None);
            Check("m4-r2-cursor-followed",data.Complete&&data.Items.Length==2&&r2.Uris[1].Query.Contains("cursor=next%2B%2F%3D%26x",StringComparison.OrdinalIgnoreCase));
            Check("m4-r2-jurisdiction-explicit",data.Detail.Contains("default")&&data.Items.All(x=>x.Key.Scope=="default")&&r2.Jurisdictions.All(x=>x=="default"));
        }
        async Task<ResourceSet> Read(ResourceKind kind,InventoryHandler handler){using var client=new InventoryClient(Profile(),"fake-inventory-token",handler:handler);return await client.Read(kind,CancellationToken.None);}
        var duplicate=await Read(ResourceKind.D1,new InventoryHandler((n,r)=>Envelope("[{\"uuid\":\"same\",\"name\":\"same\"}]")));
        Check("m4-repeated-page-incomplete",!duplicate.Complete&&duplicate.State==SourceState.Partial&&duplicate.Items.Length==1);
        var repeated=await Read(ResourceKind.R2,new InventoryHandler((n,r)=>Envelope($"{{\"buckets\":[{{\"name\":\"bucket{n}\"}}]}}","{\"cursor\":\"same\"}")));
        Check("m4-repeated-cursor-incomplete",!repeated.Complete&&repeated.Items.Length==2);
        var wrongPage=await Read(ResourceKind.D1,new InventoryHandler((n,r)=>Envelope("[]","{\"page\":99}")));
        Check("m4-wrong-page-rejected",!wrongPage.Complete&&wrongPage.State==SourceState.Partial);
        var early=await Read(ResourceKind.D1,new InventoryHandler((n,r)=>Envelope("[]","{\"total_count\":100}")));
        Check("m4-early-end-incomplete",!early.Complete);
        var empty=await Read(ResourceKind.Worker,new InventoryHandler((n,r)=>Envelope("[]")));
        Check("m4-true-empty-is-complete",empty.Complete&&empty.Items.Length==0);
        var absent=await Read(ResourceKind.Worker,new InventoryHandler((n,r)=>"{\"success\":true}"));
        Check("m4-missing-result-not-empty",!absent.Complete&&absent.State==SourceState.Error);
        var failedEnvelope=await Read(ResourceKind.Worker,new InventoryHandler((n,r)=>"{\"success\":false,\"result\":[]}"));
        Check("m4-failed-envelope-rejected",!failedEnvelope.Complete);
        foreach(int code in new[]{401,403,429,500,302}) {
            var denied=await Read(ResourceKind.Worker,new InventoryHandler((n,r)=>"{}",code));
            Check("m4-discovery-http-"+code,!denied.Complete&&denied.State==(code is 401 or 403?SourceState.Denied:SourceState.Error));
        }
        var throttle=new InventoryHandler((n,r)=>"{}",429);
        using(var client=new InventoryClient(Profile(),"fake-inventory-token",handler:throttle)) {
            var a=await client.Read(ResourceKind.Worker,CancellationToken.None);var b=await client.Read(ResourceKind.D1,CancellationToken.None);
            Check("m4-429-cooldown-no-extra-request",a.RetrySeconds>=300&&b.RetrySeconds>=299&&throttle.Uris.Count==1);
        }
        var tooMany=await Read(ResourceKind.Worker,new InventoryHandler((n,r)=>Envelope(JsonSerializer.Serialize(Enumerable.Range(0,1001).Select(x=>new{id="w"+x})))));
        Check("m4-resource-cap-not-complete",tooMany.Items.Length==1000&&!tooMany.Complete);
        using(var client=new InventoryClient(Profile(),"fake-inventory-token",handler:new InventoryHandler((n,r)=>Envelope("[]")))) {
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try{await client.Read(ResourceKind.Worker,cancel.Token);Check("m4-cancellation",false);}catch(OperationCanceledException){Check("m4-cancellation",true);}
        }
        var ha=new InventoryHandler((n,r)=>Envelope("[{\"id\":\"same\"}]"));var hb=new InventoryHandler((n,r)=>Envelope("[{\"id\":\"same\"}]"));
        using(var a=new InventoryClient(Profile(),"test-token-a",handler:ha))using(var b=new InventoryClient(Profile(bid),"test-token-b",handler:hb)) {
            var da=await a.Read(ResourceKind.Worker,CancellationToken.None);var db=await b.Read(ResourceKind.Worker,CancellationToken.None);
            Check("m4-cross-account-same-name-distinct",da.Items[0].Key!=db.Items[0].Key);
            Check("m4-token-scope-isolated",ha.Tokens.Single()=="test-token-a"&&hb.Tokens.Single()=="test-token-b");
        }
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m4-"+Guid.NewGuid().ToString("N"));
        try {
            var map=new ProjectMap(dir);var before=map.Load();var a=new ResourceKey(aid,ResourceKind.Worker,"account","api");var b=new ResourceKey(bid,ResourceKind.Worker,"account","api");
            var saved=map.Save([new(a,"VetaKeep"),new(b,"CaptainCalc")],before.Revision);
            Check("m4-project-roundtrip",map.Load().Assignments.Length==2);
            Check("m4-project-account-isolation",map.Load().Assignments.Single(x=>x.Key==a).Project=="VetaKeep");
            Throws("m4-project-conflict",()=>map.Save([],before.Revision));
            Throws("m4-project-duplicate",()=>map.Save([new(a,"one"),new(a,"two")],saved.Revision));
            Throws("m4-project-invalid-name",()=>map.Save([new(a,"bad\nname")],saved.Revision));
            var changed=map.Save([new(a,"VetaKeep renamed"),new(b,"CaptainCalc")],saved.Revision);
            Check("m4-project-other-account-preserved",changed.Assignments.Single(x=>x.Key==b).Project=="CaptainCalc");
            Check("m4-project-previous-version",File.Exists(Path.Combine(dir,"project-map.json.previous")));
            var unassigned=ProjectMap.Label(new(aid,ResourceKind.D1,"account","missing"),changed.Assignments.ToDictionary(x=>x.Key,x=>x.Project));
            Check("m4-no-name-inference",unassigned=="Unassigned");
        } finally {if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    private sealed class InventoryHandler(Func<int,HttpRequestMessage,string> body,int status=200):HttpMessageHandler
    {
        public readonly List<Uri> Uris=[];public readonly List<HttpMethod> Methods=[];
        public readonly List<string> Tokens=[],Jurisdictions=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Uris.Add(request.RequestUri!);Methods.Add(request.Method);Tokens.Add(request.Headers.Authorization?.Parameter??"");
            Jurisdictions.Add(request.Headers.TryGetValues("cf-r2-jurisdiction",out var values)?values.Single():"");
            var response=new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(body(Uris.Count,request),Encoding.UTF8,"application/json")};
            if(status==429)response.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(300));
            return Task.FromResult(response);
        }
    }
}
