using System.Globalization;
namespace OpsDeck.Core;
public static class MetricPresentation
{
    // Conservative product display policy, NOT a Cloudflare freshness SLA.
    public const int UsageSourceTtl=48*60*60;
    public static string? Date(DateTimeOffset? value)=>value?.UtcDateTime.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    public static string Ascii(string value,int max)=>new(value.Where(c=>c>=32&&c<=126).Take(max).ToArray());
    public static string Note(Metric m,DateTimeOffset now,int ttl)
    {
        if(m.State==SourceState.Denied)return "PERMISSION DENIED";
        if(m.State==SourceState.Error)return "API ERROR";
        if(m.LastRead)return Ascii(m.Note,40);
        if(m.State==SourceState.NoData)return "NO CURRENT DATA";
        if(m.UsageCharges&&m.Value.HasValue){
            if(!Freshness.IsCurrent(m.CollectedAt,now,ttl))return "COLLECTION STALE";
            if(m.SourceEnd.HasValue&&!Freshness.IsCurrent(m.SourceEnd,now,UsageSourceTtl))return "SOURCE STALE";
            if(!m.SourceEnd.HasValue)return "SOURCE DATE UNKNOWN";
            if(!m.PeriodStart.HasValue||(m.PeriodEnd.HasValue&&m.PeriodEnd<=m.PeriodStart))return "PERIOD UNKNOWN";
        }
        return Ascii(m.Note,40);
    }
    public static string Detail(Metric m)=>m.LastRead?m.Detail:m.UsageCharges||m.PeriodStart.HasValue||m.PeriodEnd.HasValue?
        $"{m.Detail} | Dönem (UTC): {Date(m.PeriodStart)??"?"} - {Date(m.PeriodEnd)??"?"}; kaynak sonu: {Date(m.SourceEnd)??"?"}. Kaynak tarihi son kayıt tarihidir, tamlık garantisi değildir.":m.Detail;
}
public static class HostingScope
{
    // URI path identity is case-sensitive; host casing is normalized by Uri.
    public static string Key(string url)=>HostConfig.ValidateSite(url).AbsoluteUri;
    public static string[] Urls(IEnumerable<string> urls)=>urls.Select(Key).Distinct(StringComparer.Ordinal).ToArray();
    public static Metric Summarize(IEnumerable<string> configured,IEnumerable<SiteResult> results,DateTimeOffset now,string note,string detail)
    {
        var keys=Urls(configured);if(keys.Length==0)return Metric.Setup("No configured HTTPS URLs") with{Note=note};
        var observations=results.Where(s=>s.Url!=null).GroupBy(s=>Key(s.Url!),StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
        int up=keys.Count(k=>observations.TryGetValue(k,out var s)&&s.Healthy);
        return new(up==keys.Length?SourceState.Ok:SourceState.Partial,up,keys.Length,now,Unit:"sites",Detail:detail,Note:note);
    }
}
