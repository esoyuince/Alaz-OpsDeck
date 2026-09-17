using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M55AlertTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m55-"+n,ok);var now=DateTimeOffset.UtcNow;
        var healthy=new OperationalAssessment(OperationalHealthState.Healthy,100,[]);
        var link=LinkHealthSnapshot.Initial(now) with{State=SourceState.Ok,LastForwardAt=now};
        var info=AlertSemantics.Evaluate(healthy,link,new PcSample(),true,PanelHealthSnapshot.Empty,now);
        C("healthy-info",info.Level==AlertLevel.Info&&info.Items.Length==0);
        var recovering=link with{State=SourceState.Partial,Recovering=true,LastAction=SerialRecoveryAction.ReopenPort,LastReason=SerialRecoveryReason.ForwardStalled};
        var rec=AlertSemantics.Evaluate(healthy,recovering,new PcSample(),true,PanelHealthSnapshot.Empty,now);
        C("recovering",rec.Level==AlertLevel.Recovering&&rec.Items.Single().Code=="LINK_RECOVERING_ACTIVE");
        var warnFinding=new OperationalFinding(OperationalSeverity.Warning,"TASK_APPROVAL","Approval required");
        var attention=new OperationalAssessment(OperationalHealthState.Attention,90,[warnFinding]);
        C("assessment-attention",AlertSemantics.Evaluate(attention,link,new PcSample(),true,PanelHealthSnapshot.Empty,now).Level==AlertLevel.Attention);
        var errorFinding=new OperationalFinding(OperationalSeverity.Error,"HTTPS_DOWN","No HTTPS endpoint reachable");
        var degraded=new OperationalAssessment(OperationalHealthState.Degraded,75,[errorFinding]);
        C("degraded-precedence",AlertSemantics.Evaluate(degraded,recovering,new PcSample(),true,PanelHealthSnapshot.Empty,now).Level==AlertLevel.Degraded);
        var below=V("C:",10000,1001,now);var at90=V("C:",1000,100,now);var at98=V("C:",1000,20,now);
        C("disk-below",AlertSemantics.VolumeLevel(below,true,now)==AlertLevel.Info);
        C("disk-90-attention",AlertSemantics.VolumeLevel(at90,true,now)==AlertLevel.Attention);
        C("disk-98-degraded",AlertSemantics.VolumeLevel(at98,true,now)==AlertLevel.Degraded);
        C("disk-stale-neutral",AlertSemantics.VolumeLevel(at98,false,now)==AlertLevel.Info);
        var diskRollup=AlertSemantics.Evaluate(healthy,link,new PcSample(Volumes:[at90]),true,PanelHealthSnapshot.Empty,now);
        C("disk-rollup",diskRollup.Level==AlertLevel.Attention&&diskRollup.Items.Single().Code=="DISK_C_ATTENTION");
        var hotOnly=AlertSemantics.Evaluate(healthy,link,new PcSample(CpuTemp:105,ChassisTemp:90,GpuTemp:100),true,PanelHealthSnapshot.Empty,now);
        C("no-temp-threshold",hotOnly.Level==AlertLevel.Info&&hotOnly.Items.Length==0);
        var sdError=AlertSemantics.Evaluate(healthy,link,new PcSample(),true,new(true,now,1,1,1,1,1,3),now);
        C("sd-error-attention",sdError.Level==AlertLevel.Attention&&sdError.Items.Single().Code=="SD_CARD_ERROR");
        var sdUnavailable=AlertSemantics.Evaluate(healthy,link,new PcSample(),true,new(true,now,1,1,1,1,1,2),now);
        C("sd-unavailable-neutral",sdUnavailable.Level==AlertLevel.Info);
        var oldRecoveringFinding=new OperationalFinding(OperationalSeverity.Warning,"LINK_RECOVERING","Panel link recovery is active");
        var oldAssessment=new OperationalAssessment(OperationalHealthState.Attention,90,[oldRecoveringFinding]);
        var dedup=AlertSemantics.Evaluate(oldAssessment,recovering,new PcSample(),true,PanelHealthSnapshot.Empty,now);
        C("recovering-replaces-warning",dedup.Items.Length==1&&dedup.Items[0].Level==AlertLevel.Recovering);
        C("labels",AlertSemantics.Label(AlertLevel.Info)=="INFO"&&AlertSemantics.Label(AlertLevel.Degraded)=="DEGRADED");
        try{new AlertItem(AlertLevel.Attention,"BAD","X","bad\nline").Validate();C("reject-control",false);}catch(ArgumentException){C("reject-control",true);}
    }
    private static VolumeReading V(string id,long total,long free,DateTimeOffset at)=>new(id,total,free,free,at,"test");
}
