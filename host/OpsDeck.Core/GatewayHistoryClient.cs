using System.Text.Json;
namespace OpsDeck.Core;
public sealed record AiGatewayHour(DateTimeOffset Hour,string Gateway,string Provider,string Model,bool RateLimited,double Count,double? Errors,double? Cached,double? InputTokens,double? OutputTokens);
public sealed partial class CloudflareClient
{
    private const string GatewayHistoryQuery="query OpsDeckGatewayHistory($accountTag: string!, $start: Time!, $end: Time!) { viewer { accounts(filter: {accountTag: $accountTag}) { accountTag aiGatewayRequestsAdaptiveGroups(limit: 1000, filter: {datetime_geq: $start, datetime_lt: $end}, orderBy: [datetimeHour_ASC]) { count dimensions { datetimeHour gateway provider model rateLimited } sum { erroredRequests cachedRequests tokensIn tokensOut } } } } }";
    private Task<AnalyticsSlice<AiGatewayHour>> GatewayHistory24(AnalyticsWindow w,CancellationToken ct)=>HistorySlice(GatewayHistoryQuery,
        new{accountTag=account,start=w.Start.UtcDateTime.ToString("O"),end=w.End.UtcDateTime.ToString("O")},"aiGatewayRequestsAdaptiveGroups",1000,
        r=>ParseGatewayHour(w,r),r=>JsonSerializer.Serialize(new{r.Hour,r.Gateway,r.Provider,r.Model,r.RateLimited}),r=>r.Errors.HasValue&&r.Cached.HasValue&&r.InputTokens.HasValue&&r.OutputTokens.HasValue,ct);
    public static AiGatewayHour ParseGatewayHour(AnalyticsWindow w,JsonElement row)
    {
        var d=row.GetProperty("dimensions");var sum=row.GetProperty("sum");double count=RequiredHistoryNumber(row,"count"),limited=NonNegative(d,"rateLimited");
        double? errors=OptionalNonNegative(sum,"erroredRequests"),cached=OptionalNonNegative(sum,"cachedRequests");
        if(limited is not (0 or 1)||errors>count||cached>count)throw new SourceFailure(SourceState.Error,"Inconsistent gateway request metrics");
        return new(w.Hour(Dimension(d,"datetimeHour",64)),AnalyticsLabel(d,"gateway",128),AnalyticsLabel(d,"provider",128),AnalyticsLabel(d,"model",256),limited==1,count,errors,cached,OptionalNonNegative(sum,"tokensIn"),OptionalNonNegative(sum,"tokensOut"));
    }
}
