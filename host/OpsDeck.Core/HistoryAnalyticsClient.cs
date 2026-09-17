using System.Text.Json;
namespace OpsDeck.Core;
public sealed partial class CloudflareClient
{
    private static double RequiredHistoryNumber(JsonElement row,string field)=>OptionalNonNegative(row,field)??throw new SourceFailure(SourceState.Error,"Missing numeric history metric");
    private const string HistoryPrefix="query OpsDeckQueueHistory($accountTag: string!, $queueId: string!, $start: Time!, $end: Time!) { viewer { accounts(filter: {accountTag: $accountTag}) { accountTag ";
    private const string HistoryFilter="filter: {queueId: $queueId, datetime_geq: $start, datetime_lt: $end}";
    private const string BacklogHistoryQuery=HistoryPrefix+"queueBacklogAdaptiveGroups(limit: 24, "+HistoryFilter+", orderBy: [datetimeHour_ASC]) { dimensions { queueId datetimeHour } avg { messages bytes } } } } }";
    private const string ConsumerHistoryQuery=HistoryPrefix+"queueConsumerMetricsAdaptiveGroups(limit: 24, "+HistoryFilter+", orderBy: [datetimeHour_ASC]) { dimensions { queueId datetimeHour } avg { concurrency } } } } }";
    private const string OperationHistoryQuery=HistoryPrefix+"queueMessageOperationsAdaptiveGroups(limit: 1000, "+HistoryFilter+", orderBy: [datetimeHour_ASC]) { count dimensions { queueId datetimeHour actionType outcome } sum { billableOperations bytes } avg { lagTime retryCount } } } } }";
    private const string AiHistoryQuery="query OpsDeckAiHistory($accountTag: string!, $start: Time!, $end: Time!) { viewer { accounts(filter: {accountTag: $accountTag}) { accountTag aiInferenceAdaptiveGroups(limit: 1000, filter: {datetime_geq: $start, datetime_lt: $end}, orderBy: [datetimeHour_ASC]) { count dimensions { datetimeHour modelId requestSource errorCode } sum { totalInputTokens totalOutputTokens totalNeurons totalInferenceTimeMs } } } } }";
    private async Task<AnalyticsSlice<T>> HistorySlice<T>(string query,object variables,string dataset,int limit,Func<JsonElement,T> parse,Func<T,string> identity,Func<T,bool> complete,CancellationToken ct)
    {
        try
        {
            using var doc=await ResourceGraph(query,variables,ct);var accounts=doc.RootElement.GetProperty("data").GetProperty("viewer").GetProperty("accounts");
            if(accounts.ValueKind!=JsonValueKind.Array||accounts.GetArrayLength()!=1||!string.Equals(Dimension(accounts[0],"accountTag",32),account,StringComparison.OrdinalIgnoreCase))throw new SourceFailure(SourceState.Error,"Analytics account identity mismatch");
            var raw=Dataset(doc.RootElement,dataset,limit);var rows=raw.Select(parse).ToArray();
            if(rows.Select(identity).Distinct(StringComparer.Ordinal).Count()!=rows.Length)throw new SourceFailure(SourceState.Error,"Duplicate analytics group");
            bool capped=limit==1000&&rows.Length==limit,missing=rows.Any(r=>!complete(r));
            var state=rows.Length==0?SourceState.NoData:capped||missing?SourceState.Partial:SourceState.Ok;
            return new(state,rows,rows.Length==0?"Analiz satırı yok; ölçüm yokluğu sıfır kullanım olarak yorumlanmaz.":capped?"1000 satır sınırına ulaşıldı; yalnız bilinen alt kapsam gösteriliyor.":missing?"Bazı ölçümler eksik; bilinmeyen alanlar sıfıra çevrilmedi.":"Dönen gruplar doğrulandı; analitik veri örneklenmiş olabilir.");
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is SourceFailure or HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
        {return new(e is SourceFailure sf?sf.State:SourceState.Error,[],e is SourceFailure f?f.Message:"Analitik okuma tamamlanamadı; değer uydurulmadı.",e is SourceFailure r?r.RetrySeconds:60,e is SourceFailure {RateLimited:true});}
    }
    public async Task<QueueHistory> QueueHistory24(QueueInfo queue,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(!QueueIdValid(queue.Id)||!string.Equals(queue.AccountId,account,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Queue history identity mismatch");
        var w=AnalyticsWindow.Last24Hours(DateTimeOffset.UtcNow);object v=new{accountTag=account,queueId=queue.Id,start=w.Start.UtcDateTime.ToString("O"),end=w.End.UtcDateTime.ToString("O")};
        if(queue.Jurisdiction.Length>0)return new(queue,w,DateTimeOffset.UtcNow,new(SourceState.NoData,[],"Restricted jurisdiction desteklenmiyor."),new(SourceState.NoData,[]),new(SourceState.NoData,[]));
        var backlog=await HistorySlice(BacklogHistoryQuery,v,"queueBacklogAdaptiveGroups",24,r=>ParseBacklogHour(queue,w,r),r=>r.Hour.ToString("O"),r=>r.Messages.HasValue&&r.Bytes.HasValue,ct);
        var consumers=backlog.StopsFurtherReads?new AnalyticsSlice<QueueConsumerHour>(backlog.State,[],"İzin/bekleme durumu nedeniyle ek istek gönderilmedi.",backlog.RetrySeconds,backlog.RateLimited):
            await HistorySlice(ConsumerHistoryQuery,v,"queueConsumerMetricsAdaptiveGroups",24,r=>ParseConsumerHour(queue,w,r),r=>r.Hour.ToString("O"),r=>r.Concurrency.HasValue,ct);
        var operations=consumers.StopsFurtherReads?new AnalyticsSlice<QueueOperationHour>(consumers.State,[],"İzin/bekleme durumu nedeniyle ek istek gönderilmedi.",consumers.RetrySeconds,consumers.RateLimited):
            await HistorySlice(OperationHistoryQuery,v,"queueMessageOperationsAdaptiveGroups",1000,r=>ParseOperationHour(queue,w,r),r=>$"{r.Hour:O}|{r.Action}|{r.Outcome}",r=>r.BillableOperations.HasValue&&r.Bytes.HasValue&&(r.Action=="WriteMessage"||r.LagMs.HasValue&&r.Retries.HasValue),ct);
        return new(queue,w,DateTimeOffset.UtcNow,backlog,consumers,operations);
    }
    private static DateTimeOffset QueueHour(QueueInfo queue,AnalyticsWindow window,JsonElement row)
    {
        var d=row.GetProperty("dimensions");if(!string.Equals(Dimension(d,"queueId",32),queue.Id,StringComparison.OrdinalIgnoreCase))throw new SourceFailure(SourceState.Error,"Queue history response identity mismatch");
        return window.Hour(Dimension(d,"datetimeHour",64));
    }
    public static QueueBacklogHour ParseBacklogHour(QueueInfo queue,AnalyticsWindow w,JsonElement row)
    {var hour=QueueHour(queue,w,row);var avg=row.GetProperty("avg");return new(hour,OptionalNonNegative(avg,"messages"),OptionalNonNegative(avg,"bytes"));}
    public static QueueConsumerHour ParseConsumerHour(QueueInfo queue,AnalyticsWindow w,JsonElement row)
    {var hour=QueueHour(queue,w,row);return new(hour,OptionalNonNegative(row.GetProperty("avg"),"concurrency"));}
    public static QueueOperationHour ParseOperationHour(QueueInfo queue,AnalyticsWindow w,JsonElement row)
    {
        var hour=QueueHour(queue,w,row);var d=row.GetProperty("dimensions");string action=Dimension(d,"actionType",32),outcome=Dimension(d,"outcome",32);
        if(action is not ("WriteMessage" or "ReadMessage" or "DeleteMessage")||(action=="DeleteMessage"?outcome is not ("success" or "dlq" or "fail"):outcome!="none"))throw new SourceFailure(SourceState.Error,"Unexpected queue operation/outcome");
        var sum=row.GetProperty("sum");var avg=row.GetProperty("avg");return new(hour,action,outcome,RequiredHistoryNumber(row,"count"),OptionalNonNegative(sum,"billableOperations"),OptionalNonNegative(sum,"bytes"),action=="WriteMessage"?null:OptionalNonNegative(avg,"lagTime"),action=="WriteMessage"?null:OptionalNonNegative(avg,"retryCount"));
    }
    public async Task<AiHistory> AiHistory24(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var w=AnalyticsWindow.Last24Hours(DateTimeOffset.UtcNow);
        var rows=await HistorySlice(AiHistoryQuery,new{accountTag=account,start=w.Start.UtcDateTime.ToString("O"),end=w.End.UtcDateTime.ToString("O")},"aiInferenceAdaptiveGroups",1000,
            r=>ParseAiHour(w,r),r=>JsonSerializer.Serialize(new{r.Hour,r.Model,r.RequestSource,r.ErrorCode}),r=>r.InputTokens.HasValue&&r.OutputTokens.HasValue&&r.Neurons.HasValue&&r.InferenceTimeMs.HasValue,ct);
        var gateway=rows.StopsFurtherReads?new AnalyticsSlice<AiGatewayHour>(rows.State,[],"İzin/bekleme durumu nedeniyle ek istek gönderilmedi.",rows.RetrySeconds,rows.RateLimited):await GatewayHistory24(w,ct);
        return new(account,w,DateTimeOffset.UtcNow,rows,gateway);
    }
    private static string AnalyticsLabel(JsonElement row,string field,int max)
    {
        if(!row.TryGetProperty(field,out var v)||v.ValueKind!=JsonValueKind.String)throw new SourceFailure(SourceState.Error,"Missing analytics label");
        string text=v.GetString()??"";if(text.Length>max||text.Any(char.IsControl))throw new SourceFailure(SourceState.Error,"Invalid analytics label");return text;
    }
    public static AiInferenceHour ParseAiHour(AnalyticsWindow window,JsonElement row)
    {
        var d=row.GetProperty("dimensions");var sum=row.GetProperty("sum");double code=NonNegative(d,"errorCode");
        if(code>uint.MaxValue||code!=Math.Truncate(code))throw new SourceFailure(SourceState.Error,"Invalid Workers AI error code");
        return new(window.Hour(Dimension(d,"datetimeHour",64)),AnalyticsLabel(d,"modelId",256),AnalyticsLabel(d,"requestSource",128),(long)code,RequiredHistoryNumber(row,"count"),
            OptionalNonNegative(sum,"totalInputTokens"),OptionalNonNegative(sum,"totalOutputTokens"),OptionalNonNegative(sum,"totalNeurons"),OptionalNonNegative(sum,"totalInferenceTimeMs"));
    }
}
