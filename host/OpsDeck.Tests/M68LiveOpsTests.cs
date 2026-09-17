using System.Text;
using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;

public static class M68LiveOpsTests
{
    public static async Task Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m68-liveops-"+n,ok);
        var snap=new ProcessSnapshot(DateTimeOffset.UtcNow,25,[new(10,"alpha",12.5,256),new(20,"beta",3.2,128)]);
        string wire=snap.Wire();using(var doc=JsonDocument.Parse(wire))
        {
            var root=doc.RootElement;C("process-type",root.GetProperty("type").GetString()=="opsdeck.processes.v1");
            C("process-bounded",Encoding.UTF8.GetByteCount(wire)<3000&&root.GetProperty("rows").GetArrayLength()<=ProcessSnapshot.PanelLimit);
            C("process-no-path",!wire.Contains("command",StringComparison.OrdinalIgnoreCase)&&!wire.Contains("path",StringComparison.OrdinalIgnoreCase));
        }
        var sampler=new ProcessSampler();_ = sampler.Sample();await Task.Delay(300);var live=sampler.Sample();
        C("process-live",live.Rows.Length<=ProcessSnapshot.PanelLimit&&live.TotalCount>=live.Rows.Length&&live.Rows.All(x=>x.Pid>0&&x.Name.Length is >0 and <32&&x.CpuPercent is >=0 and <=100&&x.RamMiB>=0));
        var now=DateTimeOffset.UtcNow;var reset=now.AddHours(2);
        var usage=new CodexUsageSnapshot(SourceState.Ok,now,PlanType:"pro",Quotas:[new CodexQuota("codex","Codex","pro",new CodexQuotaWindow(20,300,reset),null)]);
        string status=FleetState.Empty.Wire(now,false,null,usage,null);using(var doc=JsonDocument.Parse(status))
        {
            var agents=doc.RootElement.GetProperty("agents");
            C("reset-wire",agents.GetProperty("codex_quota_reset_local").GetString()==reset.ToLocalTime().ToString("dd.MM HH:mm"));
            C("reset-budget",Encoding.UTF8.GetByteCount(status)<3000);
        }
    }
}
