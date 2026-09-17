using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;

public static class M414LinkHealthTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string name,bool ok)=>check("m414-"+name,ok);
        var start=new DateTimeOffset(2026,9,15,10,0,0,TimeSpan.Zero);
        var h=LinkHealthSnapshot.Initial(start);
        C("initial",h.State==SourceState.NoData&&!h.Recovering&&h.ReopenCount==0&&h.RomProbeCount==0);
        h=h.OnSerialOpen();C("serial-open",h.State==SourceState.NoData&&!h.Recovering);
        h=h.OnRecovery(new(SerialRecoveryAction.ReopenPort,SerialRecoveryReason.ForwardStalled,1),start.AddSeconds(20));
        C("reopen-recovery",h.State==SourceState.Partial&&h.Recovering&&h.ReopenCount==1&&h.LastReason==SerialRecoveryReason.ForwardStalled);
        h=h.OnRecovery(new(SerialRecoveryAction.RomProbeReset,SerialRecoveryReason.ForwardStalled,2),start.AddSeconds(40));
        C("rom-probe-recovery",h.RomProbeCount==1&&h.LastAction==SerialRecoveryAction.RomProbeReset);
        h=h.OnForward(start.AddSeconds(45));
        C("forward-recovers",h.State==SourceState.Ok&&!h.Recovering&&h.RecoveredCount==1&&h.LastForwardAt==start.AddSeconds(45));
        var now=start.AddMinutes(2);
        string wire=FleetState.Empty.Wire(now,false,h);
        using var doc=JsonDocument.Parse(wire);var link=doc.RootElement.GetProperty("link");
        C("wire-state",link.GetProperty("state").GetInt32()==1&&!link.GetProperty("recovering").GetBoolean());
        C("wire-counts",link.GetProperty("reopen_count").GetInt32()==1&&link.GetProperty("rom_probe_count").GetInt32()==1&&link.GetProperty("recovered_count").GetInt32()==1);
        C("wire-action-reason",link.GetProperty("last_action").GetInt32()==2&&link.GetProperty("last_reason").GetInt32()==2);
        C("wire-ages",link.GetProperty("host_uptime_s").GetInt32()==120&&link.GetProperty("forward_age_s").GetInt32()==75&&link.GetProperty("recovery_age_s").GetInt32()==80);
        C("wire-budget",System.Text.Encoding.UTF8.GetByteCount(wire)<3000);
        string locked=FleetState.Empty.Wire(now,true,h);
        using var lockedDoc=JsonDocument.Parse(locked);C("lock-keeps-link-health",lockedDoc.RootElement.GetProperty("link").GetProperty("state").GetInt32()==1);
        C("labels",LinkHealthSnapshot.ActionLabel(SerialRecoveryAction.RomProbeReset)=="ROM PROBE"&&LinkHealthSnapshot.ReasonLabel(SerialRecoveryReason.ForwardStalled)=="FORWARD STALL");
        string dir=Path.Combine(AppContext.BaseDirectory,"m414-fixtures");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"status-link.json"),wire);
    }
}
