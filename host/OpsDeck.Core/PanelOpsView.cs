using System.Text.Json;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public enum PanelOpsViewKind { History=0, Alerts=1, Timeline=2 }
public sealed record PanelOpsViewRequest(int Kind=0,int Window=1,int Metric=0,int Variant=0,int RequestId=1)
{
    public void Validate()
    {
        if(Kind is <0 or >2 || Window is <0 or >3 || Metric is <0 or >9 || Variant is <0 or >1 || RequestId<1)
            throw new ArgumentException("Invalid panel ops-view request.");
    }
    public static bool TryParse(string line,out PanelOpsViewRequest? request)
    {
        request=null;
        var m=Regex.Match(line,@"opsdeck\.ui: OPSVIEW_REQUEST kind=([0-2]) window=([0-3]) metric=([0-9]) variant=([0-1]) request=([0-9]{1,10})$");
        if(!m.Success||!int.TryParse(m.Groups[5].Value,out int id))return false;
        var q=new PanelOpsViewRequest(int.Parse(m.Groups[1].Value),int.Parse(m.Groups[2].Value),int.Parse(m.Groups[3].Value),int.Parse(m.Groups[4].Value),id);
        try{q.Validate();request=q;return true;}catch(ArgumentException){return false;}
    }
}

public sealed partial class AppEngine
{
    private static TelemetryWindow PanelWindow(int value)=>value switch{0=>TelemetryWindow.M15,1=>TelemetryWindow.H1,2=>TelemetryWindow.H24,_=>TelemetryWindow.D7};
    private static string Short(string value,int max)
    {
        if(value.Length<=max)return value;
        return value[..Math.Max(1,max-3)]+"...";
    }
    private static TrendMetric? Trend(TelemetryTrend trend,string key)=>trend.Metrics.FirstOrDefault(x=>x.Key==key);
    private static double? R(double? value)=>value.HasValue?Math.Round(value.Value,2):null;
    private static (string Key,string Label,string Unit,string? Key2,string? Label2,string? Unit2,string? Key3,string? Label3,string? Unit3) MetricDef(int metric)=>metric switch
    {
        0=>("cpu","CPU","%",null,null,null,null,null,null),
        1=>("intel_gpu","Intel GPU","%",null,null,null,null,null,null),
        2=>("gpu","NVIDIA GPU","%",null,null,null,null,null,null),
        3=>("ram_used","RAM used","GiB",null,null,null,null,null,null),
        4=>("vram_used","VRAM used","GiB",null,null,null,null,null,null),
        5=>("cpu_temp","CPU temp","C","chassis_temp","Chassis temp","C","gpu_temp","NVIDIA temp","C"),
        6=>("fan1","Fan 1","RPM","fan2","Fan 2","RPM",null,null,null),
        7=>("rx","RX","Mb/s","tx","TX","Mb/s",null,null,null),
        8=>("volume","Volume used","%",null,"Free","GiB",null,null,null),
        _=>("intel_shared","Intel shared","GiB",null,null,null,null,null,null)
    };
    private static (double? A,double? B,double? C) Point(int metric,TelemetryPoint p)=>metric switch
    {
        0=>(p.Cpu,null,null),1=>(p.IntelGpu,null,null),2=>(p.Gpu,null,null),3=>(p.RamUsed,null,null),4=>(p.VramUsed,null,null),
        5=>(p.CpuTemp,p.ChassisTemp,p.GpuTemp),6=>(p.Fan1,p.Fan2,null),7=>(p.Rx,p.Tx,null),9=>(p.IntelShared,null,null),_=>(null,null,null)
    };
    private string PanelHistoryFrame(PanelOpsViewRequest q)
    {
        var now=DateTimeOffset.UtcNow;var window=PanelWindow(q.Window);var trend=ReadTelemetryTrend(window);var def=MetricDef(q.Metric);
        TrendMetric? a=null,b=null,c=null;VolumeTrend? volume=null;int volumeCount=0;var fixedRows=new List<(DateTimeOffset At,double?[] Values)>();
        if(q.Metric==8)
        {
            var volumes=trend.Volumes.OrderBy(x=>x.Id,StringComparer.OrdinalIgnoreCase).Take(2).ToArray();volumeCount=volumes.Length;
            volume=q.Variant<volumes.Length?volumes[q.Variant]:null;
            if(volume!=null)try{var store=Volatile.Read(ref telemetryHistory);if(store!=null)fixedRows.AddRange(store.ReadFixedVolumeSeries(window,now,volume.Id,48).Select(p=>(p.At,new double?[]{R(p.Used),null,null})));}catch(Microsoft.Data.Sqlite.SqliteException){}
        }
        else
        {
            a=Trend(trend,def.Key);if(def.Key2!=null)b=Trend(trend,def.Key2);if(def.Key3!=null)c=Trend(trend,def.Key3);
            try{var store=Volatile.Read(ref telemetryHistory);if(store!=null)fixedRows.AddRange(store.ReadFixedSeries(window,now,48).Select(p=>{var v=Point(q.Metric,p);return (p.At,new double?[]{R(v.A),R(v.B),R(v.C)});}));}catch(Microsoft.Data.Sqlite.SqliteException){}
        }
        var points=fixedRows.Select(x=>x.Values).ToArray();int bucketCount=points.Length,covered=points.Count(x=>x.Any(v=>v.HasValue));
        int expectedMinutes=(int)window;int sampleRows=q.Metric==8?(volume?.Points??0):(a?.Points??0);
        int coveragePct=expectedMinutes==0?0:(int)Math.Clamp(Math.Round(sampleRows*100d/expectedMinutes),0,100);
        int coverageState=sampleRows==0?0:(sampleRows>=expectedMinutes?2:1);
        (DateTimeOffset? First,DateTimeOffset? Last) bounds=(null,null);
        try{var store=Volatile.Read(ref telemetryHistory);if(store!=null)bounds=q.Metric==8&&volume!=null?store.ReadVolumeBounds(window,now,volume.Id):store.ReadMetricBounds(window,now,def.Key);}catch(Microsoft.Data.Sqlite.SqliteException){}
        string firstLocal=bounds.First?.ToLocalTime().ToString("dd.MM HH:mm")??"";
        string lastLocal=bounds.Last?.ToLocalTime().ToString("dd.MM HH:mm")??"";
        int lastAgeS=bounds.Last.HasValue?(int)Math.Clamp((now-bounds.Last.Value).TotalSeconds,0,int.MaxValue):-1;
        double? min=q.Metric==8?volume?.MinUsed:a?.Min,avg=q.Metric==8?volume?.AvgUsed:a?.Avg,max=q.Metric==8?volume?.MaxUsed:a?.Max;
        double? min2=q.Metric==8?null:b?.Min,avg2=q.Metric==8?volume?.AvgFreeGib:b?.Avg,max2=q.Metric==8?null:b?.Max;
        int rows=q.Metric==8?(volume?.Points??0):(a?.Points??0);bool available=covered>0||rows>0;
        string label=q.Metric==8?(volume?.Id??"Volume"):def.Label;
        var wire=new{
            type="opsdeck.opsview.v1",kind=0,request_id=q.RequestId,window=q.Window,metric=q.Metric,variant=q.Variant,volume_count=volumeCount,
            state=available?1:4,label,unit=def.Unit,label2=def.Label2,unit2=def.Unit2,label3=def.Label3,unit3=def.Unit3,
            min=R(min),avg=R(avg),max=R(max),min2=R(min2),avg2=R(avg2),max2=R(max2),min3=R(c?.Min),avg3=R(c?.Avg),max3=R(c?.Max),
            minute_rows=trend.MinuteRows,sample_rows=sampleRows,expected_minutes=expectedMinutes,bucket_count=bucketCount,covered_buckets=covered,coverage_state=coverageState,coverage_pct=coveragePct,first_local=firstLocal,last_local=lastLocal,last_age_s=lastAgeS,points
        };
        return JsonSerializer.Serialize(wire,Json.Options);
    }
    private string PanelAlertsFrame(PanelOpsViewRequest q)
    {
        var alerts=ReadAlertRollup();var rows=alerts.Items.Take(6).Select(x=>new{
            level=(int)x.Level,code=x.Code,source=Short(x.Source,28),text=Short(x.Text,104)}).ToArray();
        return JsonSerializer.Serialize(new{type="opsdeck.opsview.v1",kind=1,request_id=q.RequestId,
            level=(int)alerts.Level,count=alerts.Items.Length,items=rows},Json.Options);
    }
    private string PanelTimelineFrame(PanelOpsViewRequest q)
    {
        var now=DateTimeOffset.UtcNow;var rows=ReadOperationalEvents(7).Select(x=>new{
            age_s=(int)Math.Clamp((now-x.At).TotalSeconds,0,int.MaxValue),severity=(int)x.Severity,domain=(int)x.Domain,
            code=x.Code,summary=Short(x.Summary,112)}).ToArray();
        return JsonSerializer.Serialize(new{type="opsdeck.opsview.v1",kind=2,request_id=q.RequestId,
            count=rows.Length,events=rows},Json.Options);
    }
    public string PanelOpsViewFrame(PanelOpsViewRequest request)
    {
        request.Validate();string wire=request.Kind switch{0=>PanelHistoryFrame(request),1=>PanelAlertsFrame(request),_=>PanelTimelineFrame(request)};
        if(System.Text.Encoding.UTF8.GetByteCount(wire)>3000)throw new InvalidOperationException("Ops-view frame exceeds protocol budget.");
        return wire;
    }
}
