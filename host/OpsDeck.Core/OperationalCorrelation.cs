using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public sealed record OperationalCorrelation(DateTimeOffset Start,DateTimeOffset End,OperationalSeverity Severity,
    OperationalDomain[] Domains,string[] Codes,string Summary)
{
    public void Validate()
    {
        if(Start==default||End<Start||End-Start>OperationalCorrelationEngine.Window||Domains.Length<2||Domains.Length>7||Codes.Length<2||Codes.Length>6)
            throw new ArgumentException("Invalid operational correlation bounds.");
        if(Domains.Distinct().Count()!=Domains.Length||Codes.Distinct(StringComparer.Ordinal).Count()!=Codes.Length)
            throw new ArgumentException("Duplicate operational correlation fields.");
        if(Codes.Any(x=>!Regex.IsMatch(x,"^[A-Z0-9_]{1,32}$"))||string.IsNullOrWhiteSpace(Summary)||Summary.Length>180||Summary.Any(char.IsControl))
            throw new ArgumentException("Invalid operational correlation text.");
    }
}

public static class OperationalCorrelationEngine
{
    public static readonly TimeSpan Window=TimeSpan.FromMinutes(5);
    private static bool IsLifecycleNoise(OperationalEvent e)=>e.Code is "HOST_STARTED" or "HOST_STOPPED";
    public static OperationalCorrelation[] Analyze(IEnumerable<OperationalEvent> source,DateTimeOffset now)
    {
        var rows=source.Where(x=>!IsLifecycleNoise(x)&&x.At>=now.AddHours(-24)&&x.At<=now.AddMinutes(5))
            .OrderBy(x=>x.At).ThenBy(x=>x.Code,StringComparer.Ordinal).ToArray();
        var result=new List<OperationalCorrelation>();var cluster=new List<OperationalEvent>();
        void Flush()
        {
            if(cluster.Count<2){cluster.Clear();return;}
            var domains=cluster.Select(x=>x.Domain).Distinct().OrderBy(x=>x).ToArray();
            if(domains.Length<2){cluster.Clear();return;}
            var codes=cluster.Select(x=>x.Code).Distinct(StringComparer.Ordinal).Take(6).ToArray();
            if(codes.Length<2){cluster.Clear();return;}
            var severity=cluster.Max(x=>x.Severity);var start=cluster[0].At;var end=cluster[^1].At;
            var shown=codes.Take(3).ToArray();string extra=codes.Length>shown.Length?$" +{codes.Length-shown.Length}":"";
            string summary=$"Related, not causal: {string.Join(" + ",shown)}{extra} ({Math.Max(0,(int)(end-start).TotalSeconds)}s span)";
            var correlation=new OperationalCorrelation(start,end,severity,domains,codes,summary);correlation.Validate();result.Add(correlation);cluster.Clear();
        }
        foreach(var e in rows)
        {
            if(cluster.Count>0&&(e.At-cluster[0].At>Window||e.At-cluster[^1].At>Window))Flush();
            cluster.Add(e);
        }
        Flush();return result.OrderByDescending(x=>x.End).Take(20).ToArray();
    }
}
