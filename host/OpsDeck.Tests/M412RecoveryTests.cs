using OpsDeck.Core;
namespace OpsDeck.Tests;

public static class M412RecoveryTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string name,bool ok)=>check("m412-"+name,ok);
        var p=new SerialRecoveryPolicy();
        C("no-action-before-grace",p.Evaluate(10_000,10_000,1_000,10_000,20).Action==SerialRecoveryAction.None);
        C("no-action-before-frame-budget",p.Evaluate(20_000,20_000,1_000,20_000,5).Action==SerialRecoveryAction.None);
        C("recent-forward-progress-healthy",p.Evaluate(20_000,20_000,1_000,2_000,20).Action==SerialRecoveryAction.None&&p.UnresolvedConnections==0);
        var first=p.Evaluate(30_000,20_000,1_000,20_000,20);
        C("first-forward-stall-reopens",first.Action==SerialRecoveryAction.ReopenPort&&first.Reason==SerialRecoveryReason.ForwardStalled&&first.Attempt==1);
        var second=p.Evaluate(50_000,20_000,1_000,20_000,20);
        C("second-forward-stall-reopens-no-reset",second.Action==SerialRecoveryAction.ReopenPort&&second.Reason==SerialRecoveryReason.ForwardStalled&&second.Attempt==2);
        var cooldown=p.Evaluate(70_000,20_000,1_000,20_000,20);
        C("repeated-forward-stall-stays-reopen",cooldown.Action==SerialRecoveryAction.ReopenPort&&cooldown.Attempt==3);
        p.ObserveForwardProgress();
        C("forward-progress-clears-escalation",p.UnresolvedConnections==0&&!p.Recovering);
        var afterRecovery=p.Evaluate(80_000,20_000,1_000,20_000,20);
        C("post-recovery-stall-starts-at-reopen",afterRecovery.Action==SerialRecoveryAction.ReopenPort&&afterRecovery.Attempt==1);
        p.ObserveForwardProgress();
        var silent1=p.Evaluate(100_000,25_000,25_000,25_000,20);
        C("device-silence-reopens",silent1.Action==SerialRecoveryAction.ReopenPort&&silent1.Reason==SerialRecoveryReason.DeviceSilent);
        var silent2=p.Evaluate(400_001,25_000,25_000,25_000,20);
        C("persistent-silence-never-hard-resets",silent2.Action==SerialRecoveryAction.ReopenPort&&silent2.Reason==SerialRecoveryReason.DeviceSilent);
        C("constants-bounded",SerialRecoveryPolicy.GraceMs>=10_000&&SerialRecoveryPolicy.RomProbeCooldownMs>=300_000&&SerialRecoveryPolicy.MinimumFramesSent>=4);
        C("automatic-hardware-reset-disabled",!SerialRecoveryPolicy.AutomaticHardwareResetEnabled);
        var probeArgs=SerialRecoveryTool.SafeRomProbeArguments("COM9");
        C("manual-rom-probe-fixed-read-only-args",probeArgs.SequenceEqual(new[]{"--port","COM9","--before","default_reset","--after","hard_reset","chip_id"})&&!probeArgs.Any(x=>x.Contains("flash",StringComparison.OrdinalIgnoreCase)||x.Contains("erase",StringComparison.OrdinalIgnoreCase)));
        C("manual-rom-probe-timeout-bounded",SerialRecoveryTool.TimeoutSeconds>=5&&SerialRecoveryTool.TimeoutSeconds<=30);
        bool badPort=false;try{SerialRecoveryTool.SafeRomProbeArguments("COM9;erase");}catch(ArgumentException){badPort=true;}C("manual-rom-probe-port-guard",badPort);
        string acct=new string('a',32);
        CloudResource R(ResourceKind kind,int i)=>new(new ResourceKey(acct,kind,kind==ResourceKind.R2?"default":"account",$"id-{i:D3}"),$"R{i}");
        var inventory=new ResourceInventory([
            new(acct,"A",ResourceKind.Worker,SourceState.Ok,Enumerable.Range(0,40).Select(i=>R(ResourceKind.Worker,i)).ToArray(),true,DateTimeOffset.UtcNow,""),
            new(acct,"A",ResourceKind.D1,SourceState.Ok,Enumerable.Range(0,2).Select(i=>R(ResourceKind.D1,i)).ToArray(),true,DateTimeOffset.UtcNow,""),
            new(acct,"A",ResourceKind.R2,SourceState.Ok,Enumerable.Range(0,2).Select(i=>R(ResourceKind.R2,i)).ToArray(),true,DateTimeOffset.UtcNow,""),
            new(acct,"A",ResourceKind.Pages,SourceState.Ok,Enumerable.Range(0,3).Select(i=>R(ResourceKind.Pages,i)).ToArray(),true,DateTimeOffset.UtcNow,"")]);
        var selected=PanelDetailsWarmPolicy.SelectResources(inventory);
        var firstPass=PanelDetailsWarmPolicy.FirstPass(inventory);
        var remaining=PanelDetailsWarmPolicy.Remaining(inventory);
        C("warm-firstpass-four-per-kind",firstPass.Length==8&&firstPass.Count(k=>k.Kind==ResourceKind.Worker)==4&&firstPass.Count(k=>k.Kind==ResourceKind.D1)==2&&firstPass.Count(k=>k.Kind==ResourceKind.R2)==2);
        C("warm-firstpass-kind-order",firstPass.Select(k=>k.Kind).SequenceEqual(new[]{ResourceKind.Worker,ResourceKind.Worker,ResourceKind.Worker,ResourceKind.Worker,ResourceKind.D1,ResourceKind.D1,ResourceKind.R2,ResourceKind.R2}));
        C("warm-pass-partition",firstPass.Length+remaining.Length==selected.Length&&!firstPass.Intersect(remaining).Any());
        C("warm-excludes-pages",selected.All(k=>k.Kind!=ResourceKind.Pages));
        C("warm-cap-per-kind",selected.Count(k=>k.Kind==ResourceKind.Worker)==PanelDetailsWarmPolicy.MaxResourcesPerKind);
        C("warm-keeps-d1-r2",selected.Count(k=>k.Kind==ResourceKind.D1)==2&&selected.Count(k=>k.Kind==ResourceKind.R2)==2);
        C("warm-cadence-bounded",PanelDetailsWarmPolicy.RefreshSeconds>=45&&PanelDetailsWarmPolicy.RefreshSeconds<=60&&PanelDetailsWarmPolicy.InterReadDelayMs>=100&&PanelDetailsWarmPolicy.MaxQueuesPerAccount<=32);
    }
}
