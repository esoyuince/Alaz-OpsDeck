using OpsDeck.Core;
namespace OpsDeck.Tests;

public static class M413AgentTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string name,bool ok)=>check("m413-"+name,ok);
        C("count-null",AgentTaskPresentation.Count(null)=="—");
        C("count-small",AgentTaskPresentation.Count(999)=="999");
        C("count-thousand",AgentTaskPresentation.Count(1000)=="1.0K");
        C("count-million",AgentTaskPresentation.Count(1000000)=="1.0M");
        C("age-seconds",AgentTaskPresentation.Age(59)=="59s");
        C("age-minute",AgentTaskPresentation.Age(60)=="1m");
        C("age-hour",AgentTaskPresentation.Age(3600)=="1h");
        C("age-day",AgentTaskPresentation.Age(86400)=="1d");
        C("age-week",AgentTaskPresentation.Age(604800)=="7d");
        C("event-runner-claimed",AgentTaskPresentation.EventLabel("runner_claimed")=="RUNNER CLAIMED");
        C("event-requeued",AgentTaskPresentation.EventLabel("e2e_requeued")=="REQUEUED");
        C("event-unknown-hidden",AgentTaskPresentation.EventLabel("private_text")=="—");

        var now=DateTimeOffset.UtcNow;
        var task=new BridgeTaskSample(SourceState.Ok,Open:2,Claimed:3,InProgress:4,NeedsApproval:5,
            Completed:6,Blocked:7,Cancelled:8,ExpiredClaims:1,CollectedAt:now,LatestTaskAt:now.AddMinutes(-12),LatestEvent:"runner_claimed");
        C("active-count",task.Active==12);
        C("active-format",AgentTaskPresentation.Count(task.Active)=="12");
        C("relative-age",AgentTaskPresentation.Age(task.LatestTaskAt,now)=="12m");
        var fleet=new FleetState(new AgentSample(1,SourceState.Ok,SourceState.Ok,now,Tasks:task),Metric.Setup(),Metric.Setup(),Metric.Setup(),Metric.Setup(),Metric.Setup());
        string wire=fleet.Wire(now,false);
        C("wire-keeps-sanitized-event",wire.Contains("\"task_latest_event\":\"runner_claimed\"",StringComparison.Ordinal));
        C("wire-no-task-content",!wire.Contains("task_title",StringComparison.Ordinal)&&!wire.Contains("task_body",StringComparison.Ordinal)&&!wire.Contains("task_id",StringComparison.Ordinal));
        string locked=fleet.Wire(now,true);
        C("wire-lock-hides-event",!locked.Contains("runner_claimed",StringComparison.Ordinal));
    }
}
