using Microsoft.Data.Sqlite;
namespace OpsDeck.Core;

public enum ReliabilityWindow { H1=60,H8=480,H24=1440,D7=10080 }
public static class ReliabilityWindows
{
    public static TimeSpan Span(ReliabilityWindow value)=>TimeSpan.FromMinutes((int)value);
    public static string Label(ReliabilityWindow value)=>value switch{
        ReliabilityWindow.H1=>"1h",ReliabilityWindow.H8=>"8h",ReliabilityWindow.H24=>"24h",ReliabilityWindow.D7=>"7d",_=>throw new ArgumentOutOfRangeException(nameof(value))};
}
public sealed record ReliabilityMetric(string Label,string Unit,double? First,double? Latest,
    double? Min,double? Max,double? Delta,int Points);
public sealed record ReliabilityReport(ReliabilityWindow Window,DateTimeOffset From,DateTimeOffset To,
    int ExpectedMinutes,int ObservedMinutes,double CoveragePercent,DateTimeOffset? FirstObservedAt,
    DateTimeOffset? LastObservedAt,int MaxGapMinutes,ReliabilityMetric HostPrivateMiB,
    ReliabilityMetric HostWorkingSetMiB,ReliabilityMetric Handles,ReliabilityMetric Threads,
    ReliabilityMetric PanelInternalKiB,ReliabilityMetric PanelMinKiB,ReliabilityMetric PanelPsramKiB,
    long PanelPacketsAdvanced,int HostRestarts,int PanelReboots,int LinkReopens,int LinkRomProbes,
    int LinkRecoveries,double RecoveryActionsPerWindowHour,double RecoveriesPerWindowHour,
    SourceState LatestLinkState,int PanelSdState);

public sealed partial class LifecycleHistoryStore
{
    private sealed record ReliabilityRow(long Stamp,long HostPrivate,long HostWorking,int Handles,int Threads,long HostUptime,
        long? PanelUptime,long? PanelInternal,long? PanelMin,long? PanelPsram,long? Packets,int? Sd,
        int LinkState,int Reopen,int Rom,int Recovered);
    private static ReliabilityMetric Metric(string label,string unit,IReadOnlyList<ReliabilityRow> rows,
        Func<ReliabilityRow,double?> read,double scale=1)
    {
        var values=rows.Select(read).Where(x=>x.HasValue&&double.IsFinite(x.Value)).Select(x=>x!.Value/scale).ToArray();
        if(values.Length==0)return new(label,unit,null,null,null,null,null,0);
        double first=values[0],latest=values[^1];return new(label,unit,first,latest,values.Min(),values.Max(),latest-first,values.Length);
    }
    public ReliabilityReport ReadReliability(ReliabilityWindow window,DateTimeOffset to)
    {
        if(!Enum.IsDefined(window))throw new ArgumentOutOfRangeException(nameof(window));
        var span=ReliabilityWindows.Span(window);var from=to-span;long lo=from.ToUnixTimeSeconds(),hi=to.ToUnixTimeSeconds();
        var rows=new List<ReliabilityRow>();
        lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT stamp,host_private,host_working,host_handles,host_threads,host_uptime,panel_uptime,panel_internal_free,panel_internal_min,panel_psram_free,panel_packets,panel_sd_state,link_state,link_reopen,link_rom,link_recovered FROM lifecycle_minute WHERE stamp >= $lo AND stamp < $hi ORDER BY stamp";
            cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);using var r=cmd.ExecuteReader();
            while(r.Read())rows.Add(new(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),r.GetInt32(3),r.GetInt32(4),r.GetInt64(5),
                r.IsDBNull(6)?null:r.GetInt64(6),r.IsDBNull(7)?null:r.GetInt64(7),r.IsDBNull(8)?null:r.GetInt64(8),
                r.IsDBNull(9)?null:r.GetInt64(9),r.IsDBNull(10)?null:r.GetInt64(10),r.IsDBNull(11)?null:r.GetInt32(11),
                r.GetInt32(12),r.GetInt32(13),r.GetInt32(14),r.GetInt32(15)));}
        int expected=(int)span.TotalMinutes,observed=rows.Count;double coverage=expected==0?0:Math.Min(100,observed*100d/expected);
        DateTimeOffset? firstAt=observed>0?DateTimeOffset.FromUnixTimeSeconds(rows[0].Stamp):null,lastAt=observed>0?DateTimeOffset.FromUnixTimeSeconds(rows[^1].Stamp):null;
        int maxGap=0;long cursor=lo;foreach(var row in rows){if(row.Stamp>cursor)maxGap=Math.Max(maxGap,(int)((row.Stamp-cursor)/60));cursor=Math.Max(cursor,row.Stamp+60);}if(hi>cursor)maxGap=Math.Max(maxGap,(int)((hi-cursor)/60));
        long packetDelta=0;int hostRestarts=0,panelReboots=0,reopens=0,rom=0,recovered=0;long? prevPanel=null,prevPackets=null;
        for(int i=0;i<rows.Count;i++){
            if(i>0&&rows[i].HostUptime+5<rows[i-1].HostUptime)hostRestarts++;
            if(rows[i].PanelUptime.HasValue){if(prevPanel.HasValue&&rows[i].PanelUptime!.Value+5<prevPanel.Value)panelReboots++;prevPanel=rows[i].PanelUptime;}
            if(rows[i].Packets.HasValue){if(prevPackets.HasValue&&rows[i].Packets>prevPackets)packetDelta+=rows[i].Packets!.Value-prevPackets.Value;prevPackets=rows[i].Packets;}
            if(i>0&&rows[i].Reopen>rows[i-1].Reopen)reopens+=rows[i].Reopen-rows[i-1].Reopen;
            if(i>0&&rows[i].Rom>rows[i-1].Rom)rom+=rows[i].Rom-rows[i-1].Rom;
            if(i>0&&rows[i].Recovered>rows[i-1].Recovered)recovered+=rows[i].Recovered-rows[i-1].Recovered;
        }
        var hp=Metric("Host private memory","MiB",rows,x=>x.HostPrivate,1048576d);var hw=Metric("Host working set","MiB",rows,x=>x.HostWorking,1048576d);
        var handles=Metric("Host handles","",rows,x=>x.Handles);var threads=Metric("Host threads","",rows,x=>x.Threads);
        var pi=Metric("Panel internal free","KiB",rows,x=>x.PanelInternal,1024d);var pm=Metric("Panel lifetime min free","KiB",rows,x=>x.PanelMin,1024d);var ps=Metric("Panel PSRAM free","KiB",rows,x=>x.PanelPsram,1024d);
        SourceState link=SourceState.NoData;if(rows.Count>0&&Enum.IsDefined((SourceState)rows[^1].LinkState))link=(SourceState)rows[^1].LinkState;
        int sd=rows.LastOrDefault(x=>x.Sd.HasValue)?.Sd??-1;double windowHours=span.TotalHours;
        return new(window,from,to,expected,observed,coverage,firstAt,lastAt,maxGap,hp,hw,handles,threads,pi,pm,ps,packetDelta,
            hostRestarts,panelReboots,reopens,rom,recovered,windowHours>0?(reopens+rom)/windowHours:0,windowHours>0?recovered/windowHours:0,link,sd);
    }
}

public sealed partial class AppEngine
{
    public ReliabilityReport ReadReliabilityReport(ReliabilityWindow window)
    {
        var wall=DateTimeOffset.UtcNow;var to=DateTimeOffset.FromUnixTimeSeconds(wall.ToUnixTimeSeconds()/60*60);var store=Volatile.Read(ref lifecycleHistory);
        if(store!=null)try{return store.ReadReliability(window,to);}catch(Microsoft.Data.Sqlite.SqliteException){log.Event("reliability_history_read_failed");}
        var span=ReliabilityWindows.Span(window);var empty=new ReliabilityMetric("","",null,null,null,null,null,0);
        return new(window,to-span,to,(int)span.TotalMinutes,0,0,null,null,(int)span.TotalMinutes,empty,empty,empty,empty,empty,empty,empty,0,0,0,0,0,0,0,0,SourceState.NoData,-1);
    }
}
