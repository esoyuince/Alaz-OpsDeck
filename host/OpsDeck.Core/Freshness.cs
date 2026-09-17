namespace OpsDeck.Core;

/// <summary>Shared data-age rules for host labels, panel frames and totals.</summary>
public static class Freshness
{
    public static bool IsCurrent(DateTimeOffset? observed, DateTimeOffset now, int ttl)
    {
        if (ttl < 1) throw new ArgumentOutOfRangeException(nameof(ttl));
        if (!observed.HasValue) return false;
        var seconds = (now - observed.Value).TotalSeconds;
        return seconds >= -5 && seconds <= ttl;
    }

    public static SourceState MetricState(Metric metric, DateTimeOffset now, int ttl)
    {
        bool ageMatters = metric.State == SourceState.Ok ||
                         (metric.State == SourceState.Partial && metric.Value.HasValue);
        if(ageMatters&&metric.UsageCharges&&!metric.LastRead){
            if(!IsCurrent(metric.CollectedAt,now,ttl))return SourceState.Stale;
            if(metric.SourceEnd.HasValue&&!IsCurrent(metric.SourceEnd,now,MetricPresentation.UsageSourceTtl))return SourceState.Stale;
            if(!metric.SourceEnd.HasValue||!metric.PeriodStart.HasValue||!metric.PeriodEnd.HasValue||metric.PeriodEnd<=metric.PeriodStart)return SourceState.Partial;
        }
        return ageMatters && !IsCurrent(metric.CollectedAt, now, ttl)
            ? SourceState.Stale : metric.State;
    }

    public static int AgeSeconds(DateTimeOffset? observed, DateTimeOffset now)
    {
        if (!observed.HasValue || (now - observed.Value).TotalSeconds < -5) return -1;
        return (int)Math.Clamp((now - observed.Value).TotalSeconds, 0, 604800);
    }
}
