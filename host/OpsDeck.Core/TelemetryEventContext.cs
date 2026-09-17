namespace OpsDeck.Core;

public sealed record TelemetryEventContext(
    DateTimeOffset From,DateTimeOffset To,OperationalEvent[] Events,
    int DomainCount,int WarningCount,int ErrorCount)
{
    public const string Disclaimer="Time-related, not causal.";
}

public static class TelemetryEventContextEngine
{
    public static TelemetryEventContext Build(DateTimeOffset from,DateTimeOffset to,IEnumerable<OperationalEvent> events,int limit=200)
    {
        if(from==default||to==default||from>to||to-from>TimeSpan.FromDays(OperationalEventStore.RetentionDays+1))
            throw new ArgumentException("Invalid telemetry event window.");
        if(limit is <1 or > OperationalEventStore.MaxRead)throw new ArgumentOutOfRangeException(nameof(limit));
        long lo=from.ToUnixTimeMilliseconds(),hi=to.ToUnixTimeMilliseconds();
        var rows=events.Where(x=>x.At.ToUnixTimeMilliseconds()>=lo&&x.At.ToUnixTimeMilliseconds()<=hi)
            .OrderByDescending(x=>x.At).ThenBy(x=>x.Code,StringComparer.Ordinal)
            .Take(limit).ToArray();
        foreach(var row in rows)row.Validate();
        return new(from,to,rows,rows.Select(x=>x.Domain).Distinct().Count(),
            rows.Count(x=>x.Severity==OperationalSeverity.Warning),
            rows.Count(x=>x.Severity==OperationalSeverity.Error));
    }
}
