using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpsDeck.Tests;

public static partial class M49HistoryTests
{
    internal sealed class Handler : HttpMessageHandler
    {
        public int Code { get; init; }=200;
        public int RetrySeconds { get; init; }=60;
        public string Dataset { get; init; }="";
        public string Scenario { get; init; }="";
        public bool Block { get; init; }
        public int Calls { get; private set; }
        public bool Safe { get; private set; }=true;
        public List<string> Bodies { get; }=[];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;
            if(request.Method==HttpMethod.Get)
            {
                string path=request.RequestUri?.PathAndQuery??"";
                Safe&=request.RequestUri?.Host=="api.cloudflare.com"&&
                    (path==$"/client/v4/accounts/{Account}/queues?page=1&per_page=100"||path==$"/client/v4/accounts/{Account}/queues/{QueueId}/metrics");
                if(path.EndsWith("/metrics",StringComparison.Ordinal))
                    return Response("{\"success\":true,\"result\":{\"backlog_count\":0,\"backlog_bytes\":0,\"oldest_message_timestamp_ms\":0}}");
                return Response(JsonSerializer.Serialize(new{success=true,result=new[]{new{queue_id=QueueId,queue_name="offline-test",settings=new{delivery_paused=false,message_retention_period=86400},consumers_total_count=1}},result_info=new{page=1,per_page=100,total_count=1,total_pages=1}}));
            }
            string body=request.Content==null?"":await request.Content.ReadAsStringAsync(ct);
            Bodies.Add(body);
            Safe&=request.Method==HttpMethod.Post&&request.RequestUri?.Host=="api.cloudflare.com"&&
                request.RequestUri.AbsolutePath=="/client/v4/graphql"&&request.Headers.Authorization?.Scheme=="Bearer"&&
                !body.Contains("mutation",StringComparison.OrdinalIgnoreCase)&&!body.Contains("messages/pull",StringComparison.OrdinalIgnoreCase)&&!body.Contains("/ai/run",StringComparison.OrdinalIgnoreCase);
            if(Block)await Task.Delay(Timeout.InfiniteTimeSpan,ct);
            if(Code!=200){
                var failed=new HttpResponseMessage((HttpStatusCode)Code){Content=new StringContent("{}",Encoding.UTF8,"application/json")};
                if(Code==429)failed.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(RetrySeconds));
                return failed;
            }
            if(Scenario=="bad-json")return Response("{");

            using var input=JsonDocument.Parse(body);
            JsonElement variables=input.RootElement.GetProperty("variables");
            string dataset=DetectDataset(input.RootElement.GetProperty("query").GetString()??"");
            var start=DateTimeOffset.Parse(variables.GetProperty("start").GetString()??throw new InvalidOperationException("Missing test start"));
            string queueId=variables.TryGetProperty("queueId",out var q)?q.GetString()??QueueId:QueueId;
            bool targeted=Dataset.Length==0||Dataset==dataset;
            string account=targeted&&Scenario=="wrong-account"?new string('b',32):Account;
            object[] rows=RowsFor(dataset,start,queueId,targeted?Scenario:"");
            string responseDataset=targeted&&Scenario=="wrong-dataset"?dataset+"Wrong":dataset;
            var accountRow=new Dictionary<string,object?>{{"accountTag",account},{responseDataset,rows}};
            object envelope=new Dictionary<string,object?>
            {
                ["data"]=new Dictionary<string,object?>
                {
                    ["viewer"]=new Dictionary<string,object?>
                    {
                        ["accounts"]=new[]{accountRow}
                    }
                }
            };
            if(targeted&&Scenario=="bad-envelope")envelope=new Dictionary<string,object?>{{"data",new Dictionary<string,object?>()}};
            return Response(JsonSerializer.Serialize(envelope));
        }

        private static HttpResponseMessage Response(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")};
        private static string DetectDataset(string query)
        {
            foreach(string value in new[]{"queueBacklogAdaptiveGroups","queueConsumerMetricsAdaptiveGroups","queueMessageOperationsAdaptiveGroups","aiInferenceAdaptiveGroups","aiGatewayRequestsAdaptiveGroups"})
                if(query.Contains(value,StringComparison.Ordinal))return value;
            throw new InvalidOperationException("Unexpected test query");
        }

        private static object[] RowsFor(string dataset,DateTimeOffset start,string queueId,string scenario)
        {
            if(scenario=="empty")return [];
            if(scenario=="cap")return Enumerable.Range(0,1000).Select(i=>(object)Ai(start,i)).ToArray();
            List<Dictionary<string,object?>> rows=dataset switch
            {
                "queueBacklogAdaptiveGroups"=>[Backlog(start,queueId,4,1024),Backlog(start.AddHours(2),queueId,0,0)],
                "queueConsumerMetricsAdaptiveGroups"=>[Consumer(start,queueId,1.25),Consumer(start.AddHours(2),queueId,0)],
                "queueMessageOperationsAdaptiveGroups"=>[Operation(start,queueId,"ReadMessage","none",5,10,2048,1200,.5),Operation(start,queueId,"WriteMessage","none",2,2,512,null,null),Operation(start.AddHours(2),queueId,"DeleteMessage","success",0,0,0,0,0)],
                "aiInferenceAdaptiveGroups"=>[Ai(start,0)],
                "aiGatewayRequestsAdaptiveGroups"=>[Gateway(start)],
                _=>throw new InvalidOperationException("Unexpected dataset")
            };
            if(scenario=="duplicate")rows.Add(Clone(rows[0]));
            if(scenario=="future")Dimensions(rows[0])["datetimeHour"]=start.AddHours(24).ToString("O");
            if(scenario=="wrong-queue")Dimensions(rows[0])["queueId"]=new string('2',32);
            if(scenario=="bad-outcome")Dimensions(rows[0])["outcome"]="success";
            if(scenario=="bad-code")Dimensions(rows[0])["errorCode"]=1.5;
            if(scenario=="bad-count")rows[0]["count"]="bad";
            if(scenario=="bad-errors")Values(rows[0],"sum")["erroredRequests"]=6d;
            if(scenario=="missing")RemoveCompletenessField(dataset,rows[0]);
            if(scenario=="negative")SetMetric(dataset,rows[0],-1);
            if(scenario=="bad-fields")rows[0]["dimensions"]="bad";
            return rows.Cast<object>().ToArray();
        }

        private static Dictionary<string,object?> Backlog(DateTimeOffset h,string q,double messages,double bytes)=>new(){{"dimensions",new Dictionary<string,object?>{{"queueId",q},{"datetimeHour",h.ToString("O")}}},{"avg",new Dictionary<string,object?>{{"messages",messages},{"bytes",bytes}}}};
        private static Dictionary<string,object?> Consumer(DateTimeOffset h,string q,double concurrency)=>new(){{"dimensions",new Dictionary<string,object?>{{"queueId",q},{"datetimeHour",h.ToString("O")}}},{"avg",new Dictionary<string,object?>{{"concurrency",concurrency}}}};
        private static Dictionary<string,object?> Operation(DateTimeOffset h,string q,string action,string outcome,double count,double billable,double bytes,double? lag,double? retry)=>new(){{"count",count},{"dimensions",new Dictionary<string,object?>{{"queueId",q},{"datetimeHour",h.ToString("O")},{"actionType",action},{"outcome",outcome}}},{"sum",new Dictionary<string,object?>{{"billableOperations",billable},{"bytes",bytes}}},{"avg",new Dictionary<string,object?>{{"lagTime",lag},{"retryCount",retry}}}};
        private static Dictionary<string,object?> Ai(DateTimeOffset h,int index)=>new(){{"count",3d},{"dimensions",new Dictionary<string,object?>{{"datetimeHour",h.ToString("O")},{"modelId",index==0?"offline-model":"offline-model-"+index},{"requestSource","binding"},{"errorCode",0d}}},{"sum",new Dictionary<string,object?>{{"totalInputTokens",100d},{"totalOutputTokens",50d},{"totalNeurons",9d},{"totalInferenceTimeMs",12000d}}}};
        private static Dictionary<string,object?> Gateway(DateTimeOffset h)=>new(){{"count",5d},{"dimensions",new Dictionary<string,object?>{{"datetimeHour",h.ToString("O")},{"gateway","offline-gateway"},{"provider","workers-ai"},{"model","offline-model"},{"rateLimited",0d}}},{"sum",new Dictionary<string,object?>{{"erroredRequests",1d},{"cachedRequests",2d},{"tokensIn",200d},{"tokensOut",75d}}}};
        private static Dictionary<string,object?> Clone(Dictionary<string,object?> value)=>JsonSerializer.Deserialize<Dictionary<string,object?>>(JsonSerializer.Serialize(value))!;
        private static Dictionary<string,object?> Dimensions(Dictionary<string,object?> row)=>(Dictionary<string,object?>)row["dimensions"]!;
        private static Dictionary<string,object?> Values(Dictionary<string,object?> row,string key)=>(Dictionary<string,object?>)row[key]!;

        private static void RemoveCompletenessField(string dataset,Dictionary<string,object?> row)
        {
            string container=dataset.Contains("Backlog")||dataset.Contains("Consumer")?"avg":"sum";
            Values(row,container).Remove(dataset switch{"queueBacklogAdaptiveGroups"=>"bytes","queueConsumerMetricsAdaptiveGroups"=>"concurrency","queueMessageOperationsAdaptiveGroups"=>"bytes","aiInferenceAdaptiveGroups"=>"totalNeurons",_=>"cachedRequests"});
        }
        private static void SetMetric(string dataset,Dictionary<string,object?> row,double value)
        {
            if(dataset is "queueMessageOperationsAdaptiveGroups" or "aiInferenceAdaptiveGroups" or "aiGatewayRequestsAdaptiveGroups")row["count"]=value;
            else Values(row,"avg")[dataset=="queueBacklogAdaptiveGroups"?"messages":"concurrency"]=value;
        }
    }
}
