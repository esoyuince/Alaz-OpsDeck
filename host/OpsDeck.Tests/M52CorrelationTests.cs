using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M52CorrelationTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m52-"+n,ok);var now=DateTimeOffset.UtcNow;
        OperationalEvent E(int sec,OperationalDomain domain,string code,OperationalSeverity sev=OperationalSeverity.Info)
            =>new(now.AddSeconds(sec),sev,domain,code,code.Replace('_',' '));
        var lifecycle=new[]{E(-30,OperationalDomain.Power,"POWER_RESUME"),E(-20,OperationalDomain.Link,"LINK_RECOVERY",OperationalSeverity.Warning),E(-5,OperationalDomain.Link,"LINK_RECOVERED")};
        var a=OperationalCorrelationEngine.Analyze(lifecycle,now);
        C("lifecycle-correlated",a.Length==1&&a[0].Domains.Contains(OperationalDomain.Power)&&a[0].Domains.Contains(OperationalDomain.Link));
        C("severity-max",a[0].Severity==OperationalSeverity.Warning);
        C("noncausal-copy",a[0].Summary.Contains("Related, not causal")&&!a[0].Summary.Contains("caused",StringComparison.OrdinalIgnoreCase));
        var cloud=new[]{E(-40,OperationalDomain.Cloud,"D1_STATE",OperationalSeverity.Error),E(-10,OperationalDomain.Https,"HTTPS_DEGRADED",OperationalSeverity.Warning)};
        C("cloud-https",OperationalCorrelationEngine.Analyze(cloud,now).Single().Severity==OperationalSeverity.Error);
        C("same-domain-ignored",OperationalCorrelationEngine.Analyze([E(-20,OperationalDomain.Link,"LINK_RECOVERY"),E(-10,OperationalDomain.Link,"LINK_RECOVERED")],now).Length==0);
        C("outside-window-ignored",OperationalCorrelationEngine.Analyze([E(-700,OperationalDomain.Power,"POWER_RESUME"),E(-10,OperationalDomain.Link,"LINK_RECOVERY")],now).Length==0);
        C("host-lifecycle-noise-ignored",OperationalCorrelationEngine.Analyze([E(-20,OperationalDomain.Host,"HOST_STARTED"),E(-10,OperationalDomain.Cloud,"D1_STATE")],now).Length==0);
        var many=new[]{E(-50,OperationalDomain.Power,"POWER_RESUME"),E(-40,OperationalDomain.Link,"LINK_RECOVERY"),E(-30,OperationalDomain.Cloud,"D1_STATE"),E(-20,OperationalDomain.Https,"HTTPS_DEGRADED"),E(-10,OperationalDomain.Agent,"TASK_BLOCKED")};
        var m=OperationalCorrelationEngine.Analyze(many,now).Single();
        C("bounded-codes",m.Codes.Length<=6&&m.Summary.Length<=180);
        C("span-bounded",m.End-m.Start<=OperationalCorrelationEngine.Window);
    }
}
