using System.Net;
using System.Text;
using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M47ResourceTests
{
    public static async Task Run(Action<string,bool> check)
    {
        void C(string n,bool v)=>check("m47-"+n,v);
        async Task RejectAsync(string n,Func<Task> a){try{await a();C(n,false);}catch(Exception e)when(e is SourceFailure or ArgumentException or InvalidOperationException){C(n,true);}}
        string account=new string('a',32),db="11111111-2222-3333-4444-555555555555";var d1=new ResourceKey(account,ResourceKind.D1,"account",db);var r2=new ResourceKey(account,ResourceKind.R2,"default","bucket-a");
        var d1Handler=new DetailHandler(account,db,"bucket-a");using(var cf=new CloudflareClient(account,"offline-not-real-token",d1Handler))
        {
            var x=await cf.D1Detail(d1,CancellationToken.None);
            C("d1-values",x.State==SourceState.Ok&&x.ReadQueries==10&&x.WriteQueries==2&&x.RowsRead==1000&&x.RowsWritten==5&&x.ResponseBytes==2048&&x.QueryP90Ms==12.5);
            C("d1-metadata",x.DatabaseSizeBytes==1048576&&x.TableCount==7&&x.Jurisdiction=="eu"&&x.ReplicationMode=="auto");
            C("d1-window",x.End-x.Start==TimeSpan.FromHours(24)&&x.End<DateTimeOffset.UtcNow.AddMinutes(-2));
            C("d1-routes-read-only",d1Handler.GetCount==1&&d1Handler.PostCount==1&&d1Handler.SafeHost&&d1Handler.Bodies.All(b=>!b.Contains("mutation",StringComparison.OrdinalIgnoreCase)));
            C("d1-id-variable",d1Handler.Bodies.Single().Contains(db,StringComparison.Ordinal));
            C("d1-no-sql-insights",!d1Handler.Bodies.Single().Contains("d1QueriesAdaptiveGroups",StringComparison.Ordinal)&&!d1Handler.Bodies.Single().Contains("query { query",StringComparison.OrdinalIgnoreCase));
        }
        var emptyD1=new DetailHandler(account,db,"bucket-a"){D1Empty=true};using(var cf=new CloudflareClient(account,"offline-not-real-token",emptyD1)){var x=await cf.D1Detail(d1,CancellationToken.None);C("d1-empty-not-zero",x.State==SourceState.NoData&&x.ReadQueries==null&&x.DatabaseSizeBytes==1048576);}
        var missingP90=new DetailHandler(account,db,"bucket-a"){D1MissingP90=true};using(var cf=new CloudflareClient(account,"offline-not-real-token",missingP90)){var x=await cf.D1Detail(d1,CancellationToken.None);C("d1-missing-p90-partial",x.State==SourceState.Partial&&x.ReadQueries==10&&x.QueryP90Ms==null);}
        var zeroD1=new DetailHandler(account,db,"bucket-a"){D1Zero=true,D1MissingP90=true};using(var cf=new CloudflareClient(account,"offline-not-real-token",zeroD1)){var x=await cf.D1Detail(d1,CancellationToken.None);C("d1-zero-row-valid",x.State==SourceState.Ok&&x.ReadQueries==0&&x.WriteQueries==0&&x.QueryP90Ms==null);}
        var wrongD1=new DetailHandler(account,db,"bucket-a"){WrongD1Identity=true};using(var cf=new CloudflareClient(account,"offline-not-real-token",wrongD1))await RejectAsync("d1-response-identity",async()=>{await cf.D1Detail(d1,CancellationToken.None);});
        var badD1=new DetailHandler(account,db,"bucket-a"){D1Negative=true};using(var cf=new CloudflareClient(account,"offline-not-real-token",badD1))await RejectAsync("d1-negative-rejected",async()=>{await cf.D1Detail(d1,CancellationToken.None);});
        using(var cf=new CloudflareClient(account,"offline-not-real-token",new DetailHandler(account,db,"bucket-a")))
        {
            await RejectAsync("d1-wrong-account",async()=>{await cf.D1Detail(d1 with{AccountId=new string('b',32)},CancellationToken.None);});
            await RejectAsync("d1-bad-uuid",async()=>{await cf.D1Detail(d1 with{Id="not-a-uuid"},CancellationToken.None);});
            await RejectAsync("d1-wrong-kind",async()=>{await cf.D1Detail(d1 with{Kind=ResourceKind.R2},CancellationToken.None);});
        }
        var r2Handler=new DetailHandler(account,db,"bucket-a");using(var cf=new CloudflareClient(account,"offline-not-real-token",r2Handler))
        {
            var x=await cf.R2Detail(r2,CancellationToken.None);
            C("r2-values",x.State==SourceState.Ok&&x.TotalRequests==103&&x.SuccessRequests==100&&x.UserErrors==2&&x.InternalErrors==1);
            C("r2-storage",x.PayloadBytes==1073741824&&x.MetadataBytes==1048576&&x.ObjectCount==42&&x.UploadCount==3&&x.StorageAt.HasValue);
            C("r2-top",x.TopOperations is{Length:3}&&x.TopOperations[0].Requests==100&&x.TopOperations[0].ActionType=="GetObject");
            C("r2-two-graphql",r2Handler.GetCount==0&&r2Handler.PostCount==2&&r2Handler.SafeHost);
            C("r2-no-object-name",r2Handler.Bodies.All(b=>!b.Contains("objectName",StringComparison.Ordinal)));
            C("r2-bounds",r2Handler.Bodies.Any(b=>b.Contains("limit: 1000"))&&r2Handler.Bodies.Any(b=>b.Contains("limit: 1")));
            C("r2-read-only",r2Handler.Bodies.All(b=>!b.Contains("mutation",StringComparison.OrdinalIgnoreCase)));
        }
        var emptyR2=new DetailHandler(account,db,"bucket-a"){R2EmptyOps=true,R2EmptyStorage=true};using(var cf=new CloudflareClient(account,"offline-not-real-token",emptyR2)){var x=await cf.R2Detail(r2,CancellationToken.None);C("r2-empty-not-zero",x.State==SourceState.NoData&&x.TotalRequests==null&&x.ObjectCount==null);}
        var storageOnly=new DetailHandler(account,db,"bucket-a"){R2EmptyOps=true};using(var cf=new CloudflareClient(account,"offline-not-real-token",storageOnly)){var x=await cf.R2Detail(r2,CancellationToken.None);C("r2-storage-only-partial",x.State==SourceState.Partial&&x.TotalRequests==null&&x.ObjectCount==42);}
        var badStatus=new DetailHandler(account,db,"bucket-a"){R2BadStatus=true};using(var cf=new CloudflareClient(account,"offline-not-real-token",badStatus))await RejectAsync("r2-status-guard",async()=>{await cf.R2Detail(r2,CancellationToken.None);});
        var wrongBucket=new DetailHandler(account,db,"bucket-a"){WrongR2Identity=true};using(var cf=new CloudflareClient(account,"offline-not-real-token",wrongBucket))await RejectAsync("r2-response-identity",async()=>{await cf.R2Detail(r2,CancellationToken.None);});
        var dup=new DetailHandler(account,db,"bucket-a"){R2Duplicate=true};using(var cf=new CloudflareClient(account,"offline-not-real-token",dup))await RejectAsync("r2-duplicate-group",async()=>{await cf.R2Detail(r2,CancellationToken.None);});
        var negR2=new DetailHandler(account,db,"bucket-a"){R2Negative=true};using(var cf=new CloudflareClient(account,"offline-not-real-token",negR2))await RejectAsync("r2-negative-rejected",async()=>{await cf.R2Detail(r2,CancellationToken.None);});
        using(var cf=new CloudflareClient(account,"offline-not-real-token",new DetailHandler(account,db,"bucket-a")))
        {
            await RejectAsync("r2-restricted-scope",async()=>{await cf.R2Detail(r2 with{Scope="eu"},CancellationToken.None);});
            await RejectAsync("r2-wrong-account",async()=>{await cf.R2Detail(r2 with{AccountId=new string('b',32)},CancellationToken.None);});
            await RejectAsync("r2-wrong-kind",async()=>{await cf.R2Detail(r2 with{Kind=ResourceKind.D1},CancellationToken.None);});
        }
    }
    private sealed class DetailHandler(string account,string db,string bucket):HttpMessageHandler
    {
        private readonly string expectedAccount=account;
        public bool D1Empty,D1MissingP90,D1Zero,WrongD1Identity,D1Negative,R2EmptyOps,R2EmptyStorage,R2BadStatus,WrongR2Identity,R2Duplicate,R2Negative;public int GetCount,PostCount;public bool SafeHost=true;public List<string>Bodies=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            SafeHost&=request.RequestUri?.Host=="api.cloudflare.com";string body=request.Content!=null?await request.Content.ReadAsStringAsync(ct):"";if(body.Length>0)Bodies.Add(body);
            if(request.Method==HttpMethod.Get){SafeHost&=request.RequestUri?.AbsolutePath==$"/client/v4/accounts/{expectedAccount}/d1/database/{db}";GetCount++;string id=WrongD1Identity?"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee":db;return Json(JsonSerializer.Serialize(new{success=true,result=new{uuid=id,name="db",file_size=1048576,num_tables=7,jurisdiction="eu",read_replication=new{mode="auto"}}}));}
            PostCount++;
            if(body.Contains("OpsDeckD1Resource",StringComparison.Ordinal))
            {
                if(D1Empty)return Graph("d1AnalyticsAdaptiveGroups",[]);
                string id=WrongD1Identity?"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee":db;double read=D1Zero?0:D1Negative?-1:10,write=D1Zero?0:2;var q=D1MissingP90?new{}:(object)new{queryBatchTimeMsP90=12.5};
                return Graph("d1AnalyticsAdaptiveGroups",[new{dimensions=new{databaseId=id},sum=new{readQueries=read,writeQueries=write,rowsRead=D1Zero?0:1000,rowsWritten=D1Zero?0:5,queryBatchResponseBytes=D1Zero?0:2048},quantiles=q}]);
            }
            if(body.Contains("OpsDeckR2Operations",StringComparison.Ordinal))
            {
                if(R2EmptyOps)return Graph("r2OperationsAdaptiveGroups",[]);string b=WrongR2Identity?"other":bucket;string status=R2BadStatus?"mystery":"success";double get=R2Negative?-1:100;
                var rows=new List<object>{new{dimensions=new{bucketName=b,actionType="GetObject",actionStatus=status},sum=new{requests=get}},new{dimensions=new{bucketName=b,actionType="PutObject",actionStatus="userError"},sum=new{requests=2}},new{dimensions=new{bucketName=b,actionType="DeleteObject",actionStatus="internalError"},sum=new{requests=1}}};if(R2Duplicate)rows.Add(rows[0]);return Graph("r2OperationsAdaptiveGroups",rows.ToArray());
            }
            if(body.Contains("OpsDeckR2Storage",StringComparison.Ordinal))
            {
                if(R2EmptyStorage)return Graph("r2StorageAdaptiveGroups",[]);string b=WrongR2Identity?"other":bucket;return Graph("r2StorageAdaptiveGroups",[new{dimensions=new{bucketName=b,datetime=DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O")},max=new{objectCount=42,uploadCount=3,payloadSize=1073741824d,metadataSize=1048576d}}]);
            }
            return new(HttpStatusCode.BadRequest){Content=new StringContent("{}")};
        }
        private static HttpResponseMessage Graph(string dataset,object[] rows)
        {
            object accountRow=dataset switch{"d1AnalyticsAdaptiveGroups"=>new{d1AnalyticsAdaptiveGroups=rows},"r2OperationsAdaptiveGroups"=>new{r2OperationsAdaptiveGroups=rows},"r2StorageAdaptiveGroups"=>new{r2StorageAdaptiveGroups=rows},_=>new{}};
            return Json(JsonSerializer.Serialize(new{data=new{viewer=new{accounts=new[]{accountRow}}}}));
        }
        private static HttpResponseMessage Json(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")};
    }
}
