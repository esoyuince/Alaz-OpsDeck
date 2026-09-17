namespace OpsDeck.Core;

public sealed record LinkHealthSnapshot(
    SourceState State,bool Recovering,int ReopenCount,int RomProbeCount,int RecoveredCount,
    SerialRecoveryAction LastAction,SerialRecoveryReason LastReason,DateTimeOffset StartedAt,
    DateTimeOffset? LastForwardAt=null,DateTimeOffset? LastRecoveryAt=null)
{
    public static LinkHealthSnapshot Initial(DateTimeOffset now)=>new(SourceState.NoData,false,0,0,0,
        SerialRecoveryAction.None,SerialRecoveryReason.None,now);

    public LinkHealthSnapshot OnSerialOpen()=>this with{State=Recovering?SourceState.Partial:SourceState.NoData};

    public LinkHealthSnapshot OnRecovery(SerialRecoveryDecision decision,DateTimeOffset now)
    {
        int reopen=ReopenCount,probe=RomProbeCount;
        if(decision.Action==SerialRecoveryAction.ReopenPort)reopen=Math.Min(1_000_000,reopen+1);
        if(decision.Action==SerialRecoveryAction.RomProbeReset)probe=Math.Min(1_000_000,probe+1);
        return this with{State=SourceState.Partial,Recovering=true,ReopenCount=reopen,RomProbeCount=probe,
            LastAction=decision.Action,LastReason=decision.Reason,LastRecoveryAt=now};
    }

    public LinkHealthSnapshot OnForward(DateTimeOffset now)
        =>this with{State=SourceState.Ok,Recovering=false,RecoveredCount=Math.Min(1_000_000,RecoveredCount+(Recovering?1:0)),LastForwardAt=now};

    public LinkHealthSnapshot OnError()=>this with{State=SourceState.Error};

    public object Wire(DateTimeOffset now)=>new{
        state=(int)State,recovering=Recovering,reopen_count=ReopenCount,rom_probe_count=RomProbeCount,recovered_count=RecoveredCount,
        last_action=(int)LastAction,last_reason=(int)LastReason,
        host_uptime_s=(int)Math.Clamp((now-StartedAt).TotalSeconds,0,2_678_400),
        forward_age_s=Freshness.AgeSeconds(LastForwardAt,now),recovery_age_s=Freshness.AgeSeconds(LastRecoveryAt,now)
    };

    public static string ActionLabel(SerialRecoveryAction action)=>action switch{
        SerialRecoveryAction.ReopenPort=>"REOPEN",SerialRecoveryAction.RomProbeReset=>"ROM PROBE",_=>"NONE"};
    public static string ReasonLabel(SerialRecoveryReason reason)=>reason switch{
        SerialRecoveryReason.DeviceSilent=>"DEVICE SILENT",SerialRecoveryReason.ForwardStalled=>"FORWARD STALL",_=>"NONE"};
}
