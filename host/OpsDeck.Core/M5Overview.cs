namespace OpsDeck.Core;

public sealed record M5OverviewSummary(
    DateTimeOffset At,AlertLevel AlertLevel,int AssessmentScore,int ActiveAlerts,
    int Events24h,int WarningEvents24h,int ErrorEvents24h,
    int ProjectsOk,int ProjectsAttention,int ProjectsDegraded,int ProjectsUnknown,
    int ReliabilityObservedMinutes,int ReliabilityExpectedMinutes,double ReliabilityCoveragePercent,
    int HostRestarts8h,int PanelReboots8h,int RecoveryActions8h,
    DeviationMetric[] TopDeviations,OperationalCorrelation? LatestCorrelation,
    OperationalEvent[] LatestEvents,string[] Highlights)
{
    public const string Disclaimer="Read-only operational summary; correlation/deviation do not imply causality or fault diagnosis.";
}

public static class M5OverviewEngine
{
    public static M5OverviewSummary Build(DateTimeOffset at,OperationalAssessment assessment,AlertRollup alerts,
        OpsBrief brief,ProjectHealthSummary[] projects,ReliabilityReport reliability,
        BaselineDeviationReport deviation,OperationalCorrelation[] correlations)
    {
        alerts.Validate();if(assessment.Score is <0 or >100)throw new ArgumentException("Invalid assessment score.");
        var top=deviation.Metrics.Where(x=>x.Sufficient&&x.Rank>=0).OrderByDescending(x=>x.Rank)
            .ThenBy(x=>x.Label,StringComparer.Ordinal).Take(3).ToArray();
        int ok=projects.Count(x=>x.Health==ProjectHealthState.Ok),att=projects.Count(x=>x.Health==ProjectHealthState.Attention);
        int deg=projects.Count(x=>x.Health==ProjectHealthState.Degraded),unk=projects.Count(x=>x.Health==ProjectHealthState.Unknown);
        var notes=new List<string>{
            $"Current alert: {AlertSemantics.Label(alerts.Level)} ({alerts.Items.Length} active)",
            $"24h operational events: {brief.Events.Total} (warning {brief.Events.Warning}, error {brief.Events.Error})",
            $"Project health: OK {ok}, ATTENTION {att}, DEGRADED {deg}, UNKNOWN {unk}",
            FormattableString.Invariant($"8h reliability coverage: {reliability.ObservedMinutes}/{reliability.ExpectedMinutes} min ({reliability.CoveragePercent:0.0}%)")};
        if(reliability.HostRestarts+reliability.PanelReboots+reliability.LinkReopens+reliability.LinkRomProbes>0)
            notes.Add($"8h lifecycle/recovery evidence: host restart {reliability.HostRestarts}, panel reboot {reliability.PanelReboots}, reopen {reliability.LinkReopens}, ROM {reliability.LinkRomProbes}");
        if(top.Length>0)notes.Add(FormattableString.Invariant($"Largest statistical deviation: {top[0].Label} z={top[0].ZScore:0.00} (not a fault diagnosis)"));
        if(correlations.Length>0)notes.Add(correlations[0].Summary);
        return new(at,alerts.Level,assessment.Score,alerts.Items.Length,brief.Events.Total,brief.Events.Warning,brief.Events.Error,
            ok,att,deg,unk,reliability.ObservedMinutes,reliability.ExpectedMinutes,reliability.CoveragePercent,
            reliability.HostRestarts,reliability.PanelReboots,reliability.LinkReopens+reliability.LinkRomProbes,
            top,correlations.FirstOrDefault(),brief.LatestEvents.Take(5).ToArray(),notes.Take(8).ToArray());
    }
}
public sealed partial class AppEngine
{
    public M5OverviewSummary ReadM5Overview()
    {
        var now=DateTimeOffset.UtcNow;
        return M5OverviewEngine.Build(now,ReadOperationalAssessment(),ReadAlertRollup(),
            ReadOpsBrief(TelemetryWindow.H24),ReadProjectHealth(now),
            ReadReliabilityReport(ReliabilityWindow.H8),ReadBaselineDeviation(),ReadOperationalCorrelations());
    }
}
