using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;
// Pure model/parser checks. These do not replace the pending HTTP integration suite.
public static class M49BoundaryTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string name,bool result)=>check("m49-boundary-"+name,result);
        void Reject(string name,Action action)
        {
            try{action();C(name,false);}
            catch(Exception e)when(e is SourceFailure or ArgumentException or InvalidOperationException){C(name,true);}
        }
        var shortLimit=new AnalyticsSlice<int>(SourceState.Error,[],RetrySeconds:60,RateLimited:true);
        C("rate-limit-at-60-stops",shortLimit.StopsFurtherReads);
        C("ordinary-60-error-is-independent",!new AnalyticsSlice<int>(SourceState.Error,[]).StopsFurtherReads);
        C("permission-stops",new AnalyticsSlice<int>(SourceState.Denied,[]).StopsFurtherReads);
        C("long-backoff-stops",new AnalyticsSlice<int>(SourceState.Error,[],RetrySeconds:600).StopsFurtherReads);
        C("rate-flag-preserved",new SourceFailure(SourceState.Error,"Rate limited",60,rateLimited:true).RateLimited);
        C("ordinary-failure-not-rate-limited",!new SourceFailure(SourceState.Error,"HTTP 500").RateLimited);
        Reject("negative-cannot-cancel-positive",()=>AnalyticsHistory.Sum([-1,2]));
        Reject("invalid-known-not-hidden-by-null",()=>AnalyticsHistory.Sum([-1,null]));
        Reject("nan",()=>AnalyticsHistory.Sum([double.NaN]));
        Reject("infinity",()=>AnalyticsHistory.Sum([double.PositiveInfinity]));
        Reject("individual-upper-bound",()=>AnalyticsHistory.Sum([1e16+2]));
        Reject("aggregate-upper-bound",()=>AnalyticsHistory.Sum([6e15,6e15]));
        C("unknown-stays-unknown",AnalyticsHistory.Sum([0,null])==null);
        C("known-zero",AnalyticsHistory.Sum([0,0])==0);
        C("upper-bound-inclusive",AnalyticsHistory.Sum([1e16])==1e16);
        var end=DateTimeOffset.Parse("2026-09-14T17:00:00Z");var w=new AnalyticsWindow(end.AddHours(-24),end);
        C("three-minute-before-boundary",AnalyticsWindow.Last24Hours(end.AddMinutes(3).AddTicks(-1)).End==end.AddHours(-1));
        C("three-minute-boundary-inclusive",AnalyticsWindow.Last24Hours(end.AddMinutes(3)).End==end);
        C("timezone-independent",AnalyticsWindow.Last24Hours(end.AddMinutes(3).ToOffset(TimeSpan.FromHours(3)))==w);
        C("exactly-24-hours",w.Hours().Length==24&&w.Hours()[0]==w.Start&&w.Hours()[23]==end.AddHours(-1));
        Reject("end-exclusive",()=>w.Hour(end.ToString("O")));
        Reject("unaligned-hour",()=>w.Hour(w.Start.AddTicks(1).ToString("O")));
        var queue=new QueueInfo(new string('a',32),new string('1',32),"model-test");
        string json=JsonSerializer.Serialize(new
        {
            count=1,
            dimensions=new{queueId=queue.Id,datetimeHour=w.Start.ToString("O"),actionType="ReadMessage",outcome="none",modelId="offline-model",requestSource="binding",errorCode=0,gateway="local",provider="workers-ai",model="offline-model",rateLimited=0},
            avg=new{lagTime=1200,retryCount=0.5},
            sum=new{billableOperations=2,bytes=1024,totalInputTokens=100,totalOutputTokens=50,totalNeurons=9,totalInferenceTimeMs=12000,erroredRequests=0,cachedRequests=0,tokensIn=100,tokensOut=50}
        });
        JsonElement Count(string value){using var doc=JsonDocument.Parse(json.Replace("\"count\":1","\"count\":"+value,StringComparison.Ordinal));return doc.RootElement.Clone();}
        (string Name,Func<JsonElement,double> Parse)[] readers=[("queue",r=>CloudflareClient.ParseOperationHour(queue,w,r).Count),("native",r=>CloudflareClient.ParseAiHour(w,r).Count),("gateway",r=>CloudflareClient.ParseGatewayHour(w,r).Count)];
        foreach(var reader in readers)
        {
            foreach(string bad in new[]{"-1","null","\"2\"","1e17","1e400"})Reject(reader.Name+"-count-"+bad,()=>reader.Parse(Count(bad)));
            C(reader.Name+"-zero-observed",reader.Parse(Count("0"))==0);
            C(reader.Name+"-fractional-adaptive-value",reader.Parse(Count("1.5"))==1.5);
            C(reader.Name+"-inclusive-maximum",reader.Parse(Count("1e16"))==1e16);
        }
        JsonElement Changed(string from,string to){using var doc=JsonDocument.Parse(json.Replace(from,to,StringComparison.Ordinal));return doc.RootElement.Clone();}
        Reject("gateway-rate-flag-bounded",()=>CloudflareClient.ParseGatewayHour(w,Changed("\"rateLimited\":0","\"rateLimited\":2")));
        Reject("gateway-errors-cannot-exceed-count",()=>CloudflareClient.ParseGatewayHour(w,Changed("\"erroredRequests\":0","\"erroredRequests\":2")));
        Reject("gateway-cache-cannot-exceed-count",()=>CloudflareClient.ParseGatewayHour(w,Changed("\"cachedRequests\":0","\"cachedRequests\":2")));
        Reject("native-error-code-integral",()=>CloudflareClient.ParseAiHour(w,Changed("\"errorCode\":0","\"errorCode\":0.5")));
        Reject("queue-identity-bound",()=>CloudflareClient.ParseOperationHour(queue with{Id=new string('2',32)},w,Count("1")));
        Reject("queue-outcome-bound",()=>CloudflareClient.ParseOperationHour(queue,w,Changed("\"outcome\":\"none\"","\"outcome\":\"success\"")));
        C("write-lag-not-applicable",CloudflareClient.ParseOperationHour(queue,w,Changed("ReadMessage","WriteMessage")).LagMs==null);
        var qh=new QueueHistory(queue,w,end,new(SourceState.Ok,[new(w.Start,4,1024),new(w.Start.AddHours(2),0,0)]),new(SourceState.NoData,[]),new(SourceState.NoData,[]));
        var points=AnalyticsHistory.QueueSeries(qh,"messages");
        C("hourly-gap-not-imputed",points.Length==24&&points[0]==4&&points[1]==null&&points[2]==0&&points.Count(p=>p.HasValue)==2);
        C("mixed-source-status-partial",qh.State==SourceState.Partial);
        var ai=new AiHistory(queue.AccountId,w,end,new(SourceState.Ok,[new(w.Start,"native","binding",0,3,100,50,9,12000)]),new(SourceState.Ok,[new(w.Start,"gateway","workers-ai","model",false,5,0,0,200,100)]));
        C("native-series-excludes-gateway",AnalyticsHistory.AiSeries(ai,"count")[0]==3);
        C("native-unobserved-hour-null",AnalyticsHistory.AiSeries(ai,"count")[1]==null);
    }
}
