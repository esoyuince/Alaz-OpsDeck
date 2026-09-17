namespace OpsDeck.Core;

public static class AgentTaskPresentation
{
    public static string Count(int? value)
    {
        if(!value.HasValue||value.Value<0)return "—";
        int v=value.Value;
        if(v>=1_000_000)return (v/1_000_000d).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+"M";
        if(v>=1_000)return (v/1_000d).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+"K";
        return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Age(int ageSeconds)
    {
        if(ageSeconds<0)return "—";
        if(ageSeconds<60)return $"{ageSeconds}s";
        if(ageSeconds<3600)return $"{ageSeconds/60}m";
        if(ageSeconds<86400)return $"{ageSeconds/3600}h";
        return $"{ageSeconds/86400}d";
    }

    public static string Age(DateTimeOffset? observedAt,DateTimeOffset now)
        =>Age(Freshness.AgeSeconds(observedAt,now));

    public static string EventLabel(string eventType)=>eventType switch
    {
        "created"=>"CREATED",
        "context"=>"CONTEXT",
        "claimed"=>"CLAIMED",
        "runner_claimed"=>"RUNNER CLAIMED",
        "status"=>"STATUS",
        "result"=>"RESULT",
        "runner_approved"=>"APPROVED",
        "e2e_requeued"=>"REQUEUED",
        _=>"—"
    };

    public static int? Active(BridgeTaskSample? sample)=>sample?.Active;
}
