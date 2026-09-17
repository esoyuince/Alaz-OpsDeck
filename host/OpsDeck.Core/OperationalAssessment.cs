using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public enum OperationalHealthState { Healthy=0, Attention=1, Degraded=2 }
public sealed record OperationalFinding(OperationalSeverity Severity,string Code,string Text)
{
    public void Validate()
    {
        if(Severity==OperationalSeverity.Info)throw new ArgumentException("Operational findings must require attention.");
        if(!Regex.IsMatch(Code,"^[A-Z0-9_]{1,32}$")||string.IsNullOrWhiteSpace(Text)||Text.Length>140||Text.Any(char.IsControl))
            throw new ArgumentException("Invalid operational finding.");
    }
}
public sealed record OperationalAssessment(OperationalHealthState State,int Score,OperationalFinding[] Findings)
{
    public string PrimaryReason=>Findings.FirstOrDefault()?.Text??"No current operational findings";
    public void Validate()
    {
        if(!Enum.IsDefined(State)||Score is <0 or >100||Findings.Length>16)throw new ArgumentException("Invalid operational assessment.");
        foreach(var f in Findings)f.Validate();
        if(State==OperationalHealthState.Healthy&&Findings.Length>0)throw new ArgumentException("Healthy assessment cannot contain findings.");
        if(State==OperationalHealthState.Attention&&Findings.Any(x=>x.Severity==OperationalSeverity.Error))throw new ArgumentException("Attention assessment contains error.");
        if(State==OperationalHealthState.Degraded&&!Findings.Any(x=>x.Severity==OperationalSeverity.Error))throw new ArgumentException("Degraded assessment requires error.");
    }
}

public static class OperationalAssessmentEngine
{
    private static void AddState(List<OperationalFinding> f,string code,string label,SourceState state)
    {
        if(state is SourceState.Error or SourceState.Denied)f.Add(new(OperationalSeverity.Error,code,$"{label} state is {state.ToString().ToUpperInvariant()}"));
        else if(state is SourceState.Partial or SourceState.Stale)f.Add(new(OperationalSeverity.Warning,code,$"{label} state is {state.ToString().ToUpperInvariant()}"));
    }
    public static OperationalAssessment Evaluate(OperationalSnapshot s,IReadOnlyList<OperationalEvent> recent,DateTimeOffset now)
    {
        var findings=new List<OperationalFinding>();
        if(s.LinkState==SourceState.Error)findings.Add(new(OperationalSeverity.Error,"LINK_ERROR","Panel link is in ERROR state"));
        else if(s.LinkRecovering||s.LinkState is SourceState.Partial or SourceState.Stale)
            findings.Add(new(OperationalSeverity.Warning,"LINK_RECOVERING",s.LinkRecovering?"Panel link recovery is active":"Panel link is not fully healthy"));
        AddState(findings,"WORKERS_HEALTH","Workers",s.Workers);AddState(findings,"D1_HEALTH","D1",s.D1);
        AddState(findings,"R2_HEALTH","R2",s.R2);AddState(findings,"HOSTING_HEALTH","Hosting",s.Hosting);
        if(s.HttpsTotal>0&&s.HttpsUp==0)findings.Add(new(OperationalSeverity.Error,"HTTPS_DOWN","No configured HTTPS endpoint is reachable"));
        else if(s.HttpsTotal>0&&s.HttpsUp<s.HttpsTotal)findings.Add(new(OperationalSeverity.Warning,"HTTPS_PARTIAL",$"HTTPS reachable: {s.HttpsUp}/{s.HttpsTotal}"));
        if(s.NeedsApproval>0)findings.Add(new(OperationalSeverity.Warning,"TASK_APPROVAL",$"Codex tasks needing approval: {s.NeedsApproval}"));
        if(s.Blocked>0)findings.Add(new(OperationalSeverity.Warning,"TASK_BLOCKED",$"Blocked Codex tasks: {s.Blocked}"));
        if(s.ExpiredClaims>0)findings.Add(new(OperationalSeverity.Warning,"TASK_EXPIRED_CLAIM",$"Expired Codex claims: {s.ExpiredClaims}"));
        int recoveries=recent.Count(x=>x.Code=="LINK_RECOVERY"&&x.At>=now.AddHours(-1)&&x.At<=now.AddMinutes(5));
        if(recoveries>=3)findings.Add(new(OperationalSeverity.Warning,"LINK_RECOVERY_FREQUENT",$"Panel link recovery started {recoveries} times in the last hour"));
        if(s.PowerSuspended)findings.Add(new(OperationalSeverity.Warning,"OBSERVATION_SUSPENDED","PC observation is suspended"));
        findings=findings.OrderByDescending(x=>x.Severity).ThenBy(x=>x.Code,StringComparer.Ordinal).Take(16).ToList();
        int score=Math.Max(0,100-findings.Sum(x=>x.Severity==OperationalSeverity.Error?25:10));
        var state=findings.Any(x=>x.Severity==OperationalSeverity.Error)?OperationalHealthState.Degraded:
            findings.Count>0?OperationalHealthState.Attention:OperationalHealthState.Healthy;
        var result=new OperationalAssessment(state,score,findings.ToArray());result.Validate();return result;
    }
}
