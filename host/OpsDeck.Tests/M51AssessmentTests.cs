using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M51AssessmentTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m51-"+n,ok);var now=DateTimeOffset.UtcNow;
        OperationalSnapshot S(SourceState link=SourceState.Ok,bool recovering=false,int approval=0,int blocked=0,int expired=0,
            SourceState workers=SourceState.Ok,SourceState d1=SourceState.Ok,SourceState r2=SourceState.Ok,SourceState hosting=SourceState.Ok,
            int httpsUp=14,int httpsTotal=14,bool suspended=false)
            =>new(false,suspended,link,recovering,0,0,0,SerialRecoveryAction.None,SerialRecoveryReason.None,
                SourceState.Ok,SourceState.Ok,SourceState.Ok,0,approval,blocked,expired,workers,d1,r2,hosting,httpsUp,httpsTotal);
        var healthy=OperationalAssessmentEngine.Evaluate(S(),[],now);
        C("healthy",healthy.State==OperationalHealthState.Healthy&&healthy.Score==100&&healthy.Findings.Length==0);
        var empty=OperationalAssessmentEngine.Evaluate(S(workers:SourceState.NoData,d1:SourceState.Setup,r2:SourceState.NoData),[],now);
        C("nodata-neutral",empty.State==OperationalHealthState.Healthy&&empty.Score==100);
        var partial=OperationalAssessmentEngine.Evaluate(S(d1:SourceState.Partial),[],now);
        C("partial-attention",partial.State==OperationalHealthState.Attention&&partial.Score==90&&partial.Findings.Single().Code=="D1_HEALTH");
        var error=OperationalAssessmentEngine.Evaluate(S(link:SourceState.Error),[],now);
        C("link-error-degraded",error.State==OperationalHealthState.Degraded&&error.Score==75&&error.Findings.Single().Code=="LINK_ERROR");
        var https=OperationalAssessmentEngine.Evaluate(S(httpsUp:13),[],now);
        C("https-partial",https.State==OperationalHealthState.Attention&&https.Findings.Any(x=>x.Code=="HTTPS_PARTIAL"));
        var down=OperationalAssessmentEngine.Evaluate(S(httpsUp:0),[],now);
        C("https-down",down.State==OperationalHealthState.Degraded&&down.Findings.Any(x=>x.Code=="HTTPS_DOWN"));
        var approval=OperationalAssessmentEngine.Evaluate(S(approval:2),[],now);
        C("approval-warning",approval.State==OperationalHealthState.Attention&&approval.Findings.Any(x=>x.Code=="TASK_APPROVAL"));
        var blocked=OperationalAssessmentEngine.Evaluate(S(blocked:1,expired:1),[],now);
        C("blocked-expired-warning",blocked.Score==80&&blocked.Findings.Length==2);
        var events=new[]{
            new OperationalEvent(now.AddMinutes(-50),OperationalSeverity.Warning,OperationalDomain.Link,"LINK_RECOVERY","Recovery started"),
            new OperationalEvent(now.AddMinutes(-20),OperationalSeverity.Warning,OperationalDomain.Link,"LINK_RECOVERY","Recovery started"),
            new OperationalEvent(now.AddMinutes(-1),OperationalSeverity.Warning,OperationalDomain.Link,"LINK_RECOVERY","Recovery started")};
        var frequent=OperationalAssessmentEngine.Evaluate(S(),events,now);
        C("frequent-recovery",frequent.State==OperationalHealthState.Attention&&frequent.Findings.Any(x=>x.Code=="LINK_RECOVERY_FREQUENT"));
        var old=OperationalAssessmentEngine.Evaluate(S(),[events[0] with{At=now.AddHours(-2)},events[1]],now);
        C("old-recovery-ignored",old.State==OperationalHealthState.Healthy);
        var severe=OperationalAssessmentEngine.Evaluate(S(link:SourceState.Error,workers:SourceState.Error,d1:SourceState.Denied,r2:SourceState.Error,hosting:SourceState.Denied,httpsUp:0),[],now);
        C("score-floor",severe.Score==0&&severe.State==OperationalHealthState.Degraded);
        C("reason-sanitized",severe.Findings.All(x=>x.Text.Length<=140&&!x.Text.Contains("SECRET",StringComparison.OrdinalIgnoreCase)));
        var suspended=OperationalAssessmentEngine.Evaluate(S(suspended:true),[],now);
        C("suspend-visible",suspended.State==OperationalHealthState.Attention&&suspended.Findings.Any(x=>x.Code=="OBSERVATION_SUSPENDED"));
        try{new OperationalAssessment(OperationalHealthState.Healthy,90,[new(OperationalSeverity.Warning,"WARN","warning")]).Validate();C("reject-inconsistent",false);}catch(ArgumentException){C("reject-inconsistent",true);}
    }
}
