using System.Globalization;
using System.Text.Json;
namespace OpsDeck.Core;
public interface IAnalyticsReading { int RetrySeconds { get; } }
public sealed record AnalyticsWindow(DateTimeOffset Start,DateTimeOffset End)
{
    public static AnalyticsWindow Last24Hours(DateTimeOffset now){var end=DateTimeOffset.FromUnixTimeSeconds(now.AddMinutes(-3).ToUnixTimeSeconds()/3600*3600);return new(end.AddHours(-24),end);}
    public void Validate(){if(End-Start!=TimeSpan.FromHours(24)||Start.UtcTicks%TimeSpan.TicksPerHour!=0||End.UtcTicks%TimeSpan.TicksPerHour!=0)throw new ArgumentException("Expected 24 aligned UTC hours.");}
    public DateTimeOffset[] Hours(){Validate();return Enumerable.Range(0,24).Select(i=>Start.AddHours(i)).ToArray();}
    public DateTimeOffset Hour(string value){Validate();if(!DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var t)||t<Start||t>=End||t.UtcTicks%TimeSpan.TicksPerHour!=0)throw new SourceFailure(SourceState.Error,"Out-of-window/misaligned analytics hour");return t.ToUniversalTime();}
}
public sealed record AnalyticsSlice<T>(SourceState State,T[] Rows,string Detail="",int RetrySeconds=60,bool RateLimited=false)
{
    public bool StopsFurtherReads=>RateLimited||State==SourceState.Denied||RetrySeconds>60;
}
public sealed record QueueBacklogHour(DateTimeOffset Hour,double? Messages,double? Bytes);
public sealed record QueueConsumerHour(DateTimeOffset Hour,double? Concurrency);
public sealed record QueueOperationHour(DateTimeOffset Hour,string Action,string Outcome,double Count,double? BillableOperations,double? Bytes,double? LagMs,double? Retries);
public sealed record QueueHistory(QueueInfo Queue,AnalyticsWindow Window,DateTimeOffset CollectedAt,
    AnalyticsSlice<QueueBacklogHour> Backlog,AnalyticsSlice<QueueConsumerHour> Consumers,AnalyticsSlice<QueueOperationHour> Operations):IAnalyticsReading
{
    public int RetrySeconds=>Math.Max(Backlog.RetrySeconds,Math.Max(Consumers.RetrySeconds,Operations.RetrySeconds));
    public SourceState State=>AnalyticsHistory.State(Backlog.State,Consumers.State,Operations.State);
}
public sealed record AiInferenceHour(DateTimeOffset Hour,string Model,string RequestSource,long ErrorCode,double Count,
    double? InputTokens,double? OutputTokens,double? Neurons,double? InferenceTimeMs);
public sealed record AiHistory(string AccountId,AnalyticsWindow Window,DateTimeOffset CollectedAt,AnalyticsSlice<AiInferenceHour> Inference,AnalyticsSlice<AiGatewayHour> Gateway):IAnalyticsReading
{
    public int RetrySeconds=>Math.Max(Inference.RetrySeconds,Gateway.RetrySeconds);public SourceState State=>AnalyticsHistory.State(Inference.State,Gateway.State);
}
public static class AnalyticsHistory
{
    public static SourceState State(params SourceState[] states)
    {
        if(states.All(s=>s==SourceState.Ok))return SourceState.Ok;
        if(states.All(s=>s==SourceState.NoData))return SourceState.NoData;
        if(states.Any(s=>s is SourceState.Ok or SourceState.Partial))return SourceState.Partial;
        return states.Contains(SourceState.Denied)?SourceState.Denied:states.Contains(SourceState.Error)?SourceState.Error:SourceState.NoData;
    }
    public static double? Sum(IEnumerable<double?> source)
    {
        var values=source.ToArray();
        if(values.Any(v=>v.HasValue&&(!double.IsFinite(v.Value)||v.Value<0||v.Value>1e16)))throw new SourceFailure(SourceState.Error,"Invalid analytics summand");
        if(values.Length==0||values.Any(v=>!v.HasValue))return null;
        double result=values.Sum(v=>v!.Value);if(!double.IsFinite(result)||result<0||result>1e16)throw new SourceFailure(SourceState.Error,"Analytics sum outside numeric bounds");return result;
    }
    public static double?[] QueueSeries(QueueHistory s,string metric)=>s.Window.Hours().Select(t=>metric switch
    {
        "messages"=>s.Backlog.Rows.SingleOrDefault(r=>r.Hour==t)?.Messages,
        "bytes"=>s.Backlog.Rows.SingleOrDefault(r=>r.Hour==t)?.Bytes,
        "concurrency"=>s.Consumers.Rows.SingleOrDefault(r=>r.Hour==t)?.Concurrency,
        "lag"=>s.Operations.Rows.SingleOrDefault(r=>r.Hour==t&&r.Action=="ReadMessage"&&r.Outcome=="none")?.LagMs,
        "retries"=>s.Operations.Rows.SingleOrDefault(r=>r.Hour==t&&r.Action=="ReadMessage"&&r.Outcome=="none")?.Retries,
        "read"=>Sum(s.Operations.Rows.Where(r=>r.Hour==t&&r.Action=="ReadMessage").Select(r=>(double?)r.Count)),
        "write"=>Sum(s.Operations.Rows.Where(r=>r.Hour==t&&r.Action=="WriteMessage").Select(r=>(double?)r.Count)),
        _=>throw new ArgumentException("Unknown queue chart metric")
    }).ToArray();
    public static double?[] AiSeries(AiHistory s,string metric)=>s.Window.Hours().Select(t=>Sum(s.Inference.Rows.Where(r=>r.Hour==t).Select(r=>metric switch
    {"count"=>(double?)r.Count,"neurons"=>r.Neurons,"input"=>r.InputTokens,"output"=>r.OutputTokens,_=>throw new ArgumentException("Unknown AI chart metric")}))).ToArray();
}
