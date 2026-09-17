namespace OpsDeck.Core;

public sealed record TelemetryMetricDescriptor(string Key,string Label,string Unit);
public sealed record MetricAveragePoint(DateTimeOffset At,double Value);
public sealed record DeviationMetric(string Key,string Label,string Unit,bool Sufficient,
    double? RecentAvg,double? BaselineAvg,double? BaselineStdDev,double? ZScore,double? DeltaPercent,
    int RecentPoints,int BaselinePoints)
{
    public double Rank=>ZScore.HasValue?Math.Abs(ZScore.Value):-1;
}
public sealed record BaselineDeviationReport(DateTimeOffset BaselineFrom,DateTimeOffset BaselineTo,
    DateTimeOffset RecentFrom,DateTimeOffset RecentTo,DeviationMetric[] Metrics)
{
    public const string Disclaimer="Statistical deviation only; not a fault diagnosis, SLA, or temperature limit.";
}

public static class BaselineDeviationEngine
{
    public const int MinRecentPoints=5,MinBaselinePoints=30;
    public static DeviationMetric Analyze(TelemetryMetricDescriptor d,IReadOnlyList<MetricAveragePoint> baseline,IReadOnlyList<MetricAveragePoint> recent)
    {
        var b=baseline.Select(x=>x.Value).Where(double.IsFinite).ToArray();var r=recent.Select(x=>x.Value).Where(double.IsFinite).ToArray();
        if(b.Length<MinBaselinePoints||r.Length<MinRecentPoints)return new(d.Key,d.Label,d.Unit,false,null,null,null,null,null,r.Length,b.Length);
        double ba=b.Average(),ra=r.Average();double variance=b.Length>1?b.Sum(x=>(x-ba)*(x-ba))/(b.Length-1):0;double sd=Math.Sqrt(Math.Max(0,variance));
        double? z=sd>1e-9?(ra-ba)/sd:null;double? delta=Math.Abs(ba)>1e-9?(ra-ba)/Math.Abs(ba)*100:null;
        return new(d.Key,d.Label,d.Unit,true,ra,ba,sd,z,delta,r.Length,b.Length);
    }
}
