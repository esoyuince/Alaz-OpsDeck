using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M411BridgeTaskProbe
{
    public static int Run(string[] args)
    {
        int i=Array.IndexOf(args,"--m411-live-bridge-tasks");
        if(i<0||i+1>=args.Length){Console.Error.WriteLine("Missing bridge task DB path");return 2;}
        string path=HostConfig.ValidateBridgeTaskDatabase(args[i+1]);
        var now=DateTimeOffset.UtcNow;var s=new BridgeTaskReader(path).Read(now);
        var result=new {scope="READ-ONLY SANITIZED BRIDGE TASK METADATA; NO TITLES, BODIES, RESULTS, ERRORS, ACTORS OR IDS",
            time=now,state=s.State.ToString(),s.Open,s.Claimed,s.InProgress,s.NeedsApproval,s.Completed,s.Blocked,s.Cancelled,
            s.ExpiredClaims,s.LatestTaskAt,s.LatestEvent,s.LatestEventAt,latest_age_s=Freshness.AgeSeconds(s.LatestTaskAt,now),s.Detail};
        Console.WriteLine(JsonSerializer.Serialize(result,new JsonSerializerOptions(Json.Options){WriteIndented=true}));
        return s.State==SourceState.Error?1:0;
    }
}
