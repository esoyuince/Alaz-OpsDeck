namespace OpsDeck.Core;

public sealed record OperationalRangeSummary(
    int Total,int Info,int Warning,int Error,int Domains,
    int LinkRecovery,int LinkRecovered,int PanelReboot,int HostStarted,int HostStopped,
    int PowerSuspend,int PowerResume,int HttpsDegraded,int HttpsRestored,
    int TaskApproval,int TaskBlocked,int CloudStateChanges)
{
    public static OperationalRangeSummary Empty=>new(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0);
}

public sealed record OpsBrief(
    TelemetryWindow Window,DateTimeOffset From,DateTimeOffset To,
    OperationalRangeSummary Events,TelemetryTrend Telemetry,LifecycleTrend Lifecycle,
    AlertRollup CurrentAlerts,OperationalEvent[] LatestEvents,string[] Highlights)
{
    public const string Disclaimer="Deterministic evidence summary; time proximity is not causality.";
}

public static class OpsBriefEngine
{
    public static OpsBrief Build(TelemetryWindow window,DateTimeOffset from,DateTimeOffset to,
        OperationalRangeSummary events,TelemetryTrend telemetry,LifecycleTrend lifecycle,
        AlertRollup alerts,OperationalEvent[] latest)
    {
        if(window is not (TelemetryWindow.H24 or TelemetryWindow.D7))throw new ArgumentException("Ops brief supports 24h or 7d only.");
        if(from==default||to==default||from>to)throw new ArgumentException("Invalid brief window.");
        var notes=new List<string>();
        notes.Add($"Current alert level: {AlertSemantics.Label(alerts.Level)} ({alerts.Items.Length} active)");
        if(events.Error>0)notes.Add($"Operational error events: {events.Error}");
        if(events.Warning>0)notes.Add($"Operational warning events: {events.Warning}");
        if(events.LinkRecovery>0||lifecycle.LinkReopens>0||lifecycle.LinkRomProbes>0)
            notes.Add($"Panel recovery activity: events {events.LinkRecovery}, reopen +{lifecycle.LinkReopens}, ROM +{lifecycle.LinkRomProbes}");
        int panelReboots=Math.Max(events.PanelReboot,lifecycle.PanelReboots);if(panelReboots>0)notes.Add($"Panel reboot observations: {panelReboots}");
        if(events.HttpsDegraded>0)notes.Add($"HTTPS degradation events: {events.HttpsDegraded}; restored: {events.HttpsRestored}");
        if(events.TaskBlocked>0||events.TaskApproval>0)notes.Add($"Codex task events: blocked {events.TaskBlocked}, approval {events.TaskApproval}");
        var disk=telemetry.Volumes.OrderByDescending(x=>x.MaxUsed??-1).FirstOrDefault();
        if(disk?.MaxUsed is double used&&used>=AlertSemantics.DiskAttentionPercent)
            notes.Add(FormattableString.Invariant($"Peak volume use: {disk.Id} {used:0.0}% ({(used>=AlertSemantics.DiskDegradedPercent?"DEGRADED":"ATTENTION")} policy)"));
        if(lifecycle.HostPrivateMiB.Min is double minMem&&lifecycle.HostPrivateMiB.Latest is double latestMem)
            notes.Add(FormattableString.Invariant($"Host private memory min/latest: {minMem:0.#}/{latestMem:0.#} MiB"));
        foreach(string key in new[]{"cpu_temp","chassis_temp","gpu_temp"}){
            var m=telemetry.Metrics.FirstOrDefault(x=>x.Key==key);if(m?.Max is double max)notes.Add(FormattableString.Invariant($"{m.Label} max: {max:0.#} {m.Unit} (informational; no temperature threshold policy)"));
        }
        notes=notes.Take(10).ToList();
        return new(window,from,to,events,telemetry,lifecycle,alerts,latest.Take(10).ToArray(),notes.ToArray());
    }
}
