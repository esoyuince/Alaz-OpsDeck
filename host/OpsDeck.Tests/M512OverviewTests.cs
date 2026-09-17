using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M512OverviewTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m512-"+n,ok);var now=DateTimeOffset.UtcNow;
        var finding=new OperationalFinding(OperationalSeverity.Warning,"TEST_WARN","Test warning");
        var assessment=new OperationalAssessment(OperationalHealthState.Attention,90,[finding]);
        var item=new AlertItem(AlertLevel.Attention,"TEST_ALERT","OPS","Test attention");
        var alerts=new AlertRollup(AlertLevel.Attention,[item]);
        var range=new OperationalRangeSummary(12,9,2,1,4,1,1,0,1,0,0,0,1,1,0,0,2);
        var latest=Enumerable.Range(0,7).Select(i=>new OperationalEvent(now.AddMinutes(-i-1),OperationalSeverity.Info,
            OperationalDomain.Host,"TEST_"+i,$"Sanitized event {i}")).ToArray();
        var brief=new OpsBrief(TelemetryWindow.H24,now.AddHours(-24),now,range,null!,null!,alerts,latest,[]);
        const string A="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        ProjectHealthSummary P(string name,ProjectHealthState h)=>new(A,"Other",name,true,h,1,1,h==ProjectHealthState.Ok?1:0,
            h==ProjectHealthState.Attention?1:0,h==ProjectHealthState.Degraded?1:0,h==ProjectHealthState.Unknown?1:0);
        var projects=new[]{P("ok",ProjectHealthState.Ok),P("att",ProjectHealthState.Attention),P("deg",ProjectHealthState.Degraded),P("unk",ProjectHealthState.Unknown)};
        var empty=new ReliabilityMetric("x","",null,null,null,null,null,0);
        var reliability=new ReliabilityReport(ReliabilityWindow.H8,now.AddHours(-8),now,480,400,400d/480*100,
            now.AddHours(-8),now.AddMinutes(-1),5,empty,empty,empty,empty,empty,empty,empty,1000,1,2,3,1,2,.5,.25,SourceState.Ok,1);
        DeviationMetric D(string key,string label,bool sufficient,double? z)=>new(key,label,"%",sufficient,50,40,5,z,25,sufficient?10:1,sufficient?60:1);
        var deviation=new BaselineDeviationReport(now.AddHours(-25),now.AddMinutes(-15),now.AddMinutes(-15),now,
            [D("a","A",true,.5),D("b","B",true,3.5),D("c","C",true,-2.1),D("d","D",true,4.2),D("e","E",false,9)]);
        var correlation=new OperationalCorrelation(now.AddMinutes(-4),now.AddMinutes(-2),OperationalSeverity.Warning,
            [OperationalDomain.Link,OperationalDomain.Cloud],["LINK_RECOVERY","CLOUD_STATE"],"Related, not causal: LINK_RECOVERY + CLOUD_STATE");
        var r=M5OverviewEngine.Build(now,assessment,alerts,brief,projects,reliability,deviation,[correlation]);
        C("current-state",r.AlertLevel==AlertLevel.Attention&&r.AssessmentScore==90&&r.ActiveAlerts==1);
        C("event-counts",r.Events24h==12&&r.WarningEvents24h==2&&r.ErrorEvents24h==1);
        C("project-counts",r.ProjectsOk==1&&r.ProjectsAttention==1&&r.ProjectsDegraded==1&&r.ProjectsUnknown==1);
        C("reliability",r.ReliabilityObservedMinutes==400&&r.ReliabilityExpectedMinutes==480&&r.HostRestarts8h==1&&r.PanelReboots8h==2&&r.RecoveryActions8h==4);
        C("latest-bounded",r.LatestEvents.Length==5&&r.LatestEvents[0].Code=="TEST_0");
        C("deviation-bounded",r.TopDeviations.Length==3&&r.TopDeviations.Select(x=>x.Label).SequenceEqual(new[]{"D","B","C"}));
        C("insufficient-excluded",r.TopDeviations.All(x=>x.Sufficient)&&r.TopDeviations.All(x=>x.Label!="E"));
        C("correlation-preserved",r.LatestCorrelation==correlation&&r.Highlights.Any(x=>x.Contains("not causal",StringComparison.OrdinalIgnoreCase)));
        C("disclaimer",M5OverviewSummary.Disclaimer.Contains("Read-only",StringComparison.Ordinal)&&M5OverviewSummary.Disclaimer.Contains("do not imply causality",StringComparison.Ordinal));
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m512-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try{var engine=new AppEngine(new HostConfig(),new LocalSettings(dir));engine.Start(false,false);Thread.Sleep(120);var live=engine.ReadM5Overview();C("engine-safe",live.AssessmentScore is >=0 and <=100&&live.LatestEvents.Length<=5&&live.TopDeviations.Length<=3);engine.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
}
