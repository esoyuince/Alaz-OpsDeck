using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
namespace OpsDeck.Core;

public sealed record PanelHealthSnapshot(bool Present,DateTimeOffset? ObservedAt=null,long UptimeS=-1,
    long InternalFree=-1,long InternalMin=-1,long PsramFree=-1,long Packets=-1,int SdState=-1)
{
    public static PanelHealthSnapshot Empty=>new(false);
    private static readonly Regex Wire=new(@"^HEALTH uptime_s=([0-9]{1,12}) internal_free=([0-9]{1,12}) internal_min=([0-9]{1,12}) psram_free=([0-9]{1,12})(?: sd=([0-3]))? packets=([0-9]{1,12})$",RegexOptions.CultureInvariant);
    public static bool TryParse(string evidence,DateTimeOffset at,out PanelHealthSnapshot value)
    {
        value=Empty;if(string.IsNullOrEmpty(evidence)||evidence.Length>240||evidence.Any(char.IsControl))return false;
        var m=Wire.Match(evidence);if(!m.Success)return false;
        if(!long.TryParse(m.Groups[1].Value,out var uptime)||!long.TryParse(m.Groups[2].Value,out var free)||
           !long.TryParse(m.Groups[3].Value,out var min)||!long.TryParse(m.Groups[4].Value,out var psram)||
           !long.TryParse(m.Groups[6].Value,out var packets))return false;
        if(uptime<0||uptime>315_576_000||free<0||free>134_217_728||min<0||min>134_217_728||psram<0||psram>134_217_728||packets<0)return false;
        int sd=m.Groups[5].Success?int.Parse(m.Groups[5].Value):-1;
        value=new(true,at,uptime,free,min,psram,packets,sd);return true;
    }
    public static bool IsReboot(PanelHealthSnapshot before,PanelHealthSnapshot after)
        =>before.Present&&after.Present&&before.UptimeS>=0&&after.UptimeS>=0&&after.UptimeS+5<before.UptimeS;
}
public sealed record LifecycleSample(DateTimeOffset At,long HostPrivateBytes,long HostWorkingSetBytes,
    int HostHandles,int HostThreads,long HostUptimeS,PanelHealthSnapshot Panel,LinkHealthSnapshot Link)
{
    public static LifecycleSample Capture(PanelHealthSnapshot panel,LinkHealthSnapshot link)
    {
        using var p=Process.GetCurrentProcess();p.Refresh();var now=DateTimeOffset.UtcNow;
        return new(now,p.PrivateMemorySize64,p.WorkingSet64,p.HandleCount,p.Threads.Count,
            Math.Max(0,(long)(now-link.StartedAt).TotalSeconds),panel,link);
    }
}

public sealed record RangeStat(double? Min,double? Avg,double? Max,double? Latest,int Points);
public sealed record LifecycleTrend(TelemetryWindow Window,DateTimeOffset From,DateTimeOffset To,int MinuteRows,
    RangeStat HostPrivateMiB,RangeStat HostWorkingSetMiB,RangeStat Handles,RangeStat Threads,
    RangeStat PanelInternalKiB,RangeStat PanelMinKiB,RangeStat PanelPsramKiB,long PanelPacketsAdvanced,
    int PanelReboots,long PanelLatestUptimeS,int PanelSdState,int HostRestarts,
    int LinkReopens,int LinkRomProbes,int LinkRecoveries,SourceState LatestLinkState);

public sealed partial class LifecycleHistoryStore : IDisposable
{
    public const int RetentionDays=7;
    private readonly SqliteConnection db;private readonly object gate=new();
    public LifecycleHistoryStore(string path)
    {
        db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,DefaultTimeout=1}.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-512; "+
            "CREATE TABLE IF NOT EXISTS lifecycle_minute (stamp INTEGER PRIMARY KEY,host_private INTEGER NOT NULL,host_working INTEGER NOT NULL,host_handles INTEGER NOT NULL,host_threads INTEGER NOT NULL,host_uptime INTEGER NOT NULL,"+
            "panel_present INTEGER NOT NULL,panel_uptime INTEGER,panel_internal_free INTEGER,panel_internal_min INTEGER,panel_psram_free INTEGER,panel_packets INTEGER,panel_sd_state INTEGER,"+
            "link_state INTEGER NOT NULL,link_reopen INTEGER NOT NULL,link_rom INTEGER NOT NULL,link_recovered INTEGER NOT NULL);";cmd.ExecuteNonQuery();
    }
    public void Add(LifecycleSample s)
    {
        if(s.HostPrivateBytes<0||s.HostWorkingSetBytes<0||s.HostHandles<0||s.HostThreads<0||s.HostUptimeS<0)throw new ArgumentException("Invalid lifecycle sample.");
        long minute=s.At.ToUnixTimeSeconds()/60*60,cutoff=s.At.AddDays(-RetentionDays).ToUnixTimeSeconds();
        lock(gate){using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="INSERT OR REPLACE INTO lifecycle_minute VALUES($t,$hp,$hw,$hh,$ht,$hu,$pp,$pu,$pf,$pm,$ps,$pk,$sd,$ls,$lr,$lp,$lc); DELETE FROM lifecycle_minute WHERE stamp < $cutoff;";
            cmd.Parameters.AddWithValue("$t",minute);cmd.Parameters.AddWithValue("$hp",s.HostPrivateBytes);cmd.Parameters.AddWithValue("$hw",s.HostWorkingSetBytes);
            cmd.Parameters.AddWithValue("$hh",s.HostHandles);cmd.Parameters.AddWithValue("$ht",s.HostThreads);cmd.Parameters.AddWithValue("$hu",s.HostUptimeS);
            cmd.Parameters.AddWithValue("$pp",s.Panel.Present?1:0);cmd.Parameters.AddWithValue("$pu",s.Panel.Present?(object)s.Panel.UptimeS:DBNull.Value);
            cmd.Parameters.AddWithValue("$pf",s.Panel.Present?(object)s.Panel.InternalFree:DBNull.Value);cmd.Parameters.AddWithValue("$pm",s.Panel.Present?(object)s.Panel.InternalMin:DBNull.Value);
            cmd.Parameters.AddWithValue("$ps",s.Panel.Present?(object)s.Panel.PsramFree:DBNull.Value);cmd.Parameters.AddWithValue("$pk",s.Panel.Present?(object)s.Panel.Packets:DBNull.Value);
            cmd.Parameters.AddWithValue("$sd",s.Panel.Present&&s.Panel.SdState>=0?(object)s.Panel.SdState:DBNull.Value);
            cmd.Parameters.AddWithValue("$ls",(int)s.Link.State);cmd.Parameters.AddWithValue("$lr",s.Link.ReopenCount);cmd.Parameters.AddWithValue("$lp",s.Link.RomProbeCount);cmd.Parameters.AddWithValue("$lc",s.Link.RecoveredCount);
            cmd.Parameters.AddWithValue("$cutoff",cutoff);cmd.ExecuteNonQuery();tx.Commit();}
    }

    private static double? D(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetDouble(i);
    private static RangeStat Range(SqliteConnection db,string column,long lo,long hi,double scale=1)
    {
        using var cmd=db.CreateCommand();cmd.CommandText=$"SELECT MIN({column}),AVG({column}),MAX({column}),(SELECT {column} FROM lifecycle_minute WHERE stamp >= $lo AND stamp <= $hi AND {column} IS NOT NULL ORDER BY stamp DESC LIMIT 1),COUNT({column}) FROM lifecycle_minute WHERE stamp >= $lo AND stamp <= $hi";
        cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);using var r=cmd.ExecuteReader();r.Read();
        double? min=D(r,0),avg=D(r,1),max=D(r,2),latest=D(r,3);return new(min/scale,avg/scale,max/scale,latest/scale,r.GetInt32(4));
    }
    private sealed record CounterRow(long HostUptime,long? PanelUptime,long? Packets,int Reopen,int Rom,int Recovered);
    public LifecycleTrend ReadSummary(TelemetryWindow window,DateTimeOffset now)
    {
        var from=now-TelemetryWindows.Span(window);long lo=from.ToUnixTimeSeconds(),hi=now.ToUnixTimeSeconds();
        lock(gate){
            var hp=Range(db,"host_private",lo,hi,1048576d);var hw=Range(db,"host_working",lo,hi,1048576d);
            var hh=Range(db,"host_handles",lo,hi);var ht=Range(db,"host_threads",lo,hi);
            var pf=Range(db,"panel_internal_free",lo,hi,1024d);var pm=Range(db,"panel_internal_min",lo,hi,1024d);var ps=Range(db,"panel_psram_free",lo,hi,1024d);
            var rows=new List<CounterRow>();using(var cmd=db.CreateCommand()){
                cmd.CommandText="SELECT host_uptime,panel_uptime,panel_packets,link_reopen,link_rom,link_recovered FROM lifecycle_minute WHERE stamp >= $lo AND stamp <= $hi ORDER BY stamp";
                cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);using var r=cmd.ExecuteReader();while(r.Read())rows.Add(new(r.GetInt64(0),r.IsDBNull(1)?null:r.GetInt64(1),r.IsDBNull(2)?null:r.GetInt64(2),r.GetInt32(3),r.GetInt32(4),r.GetInt32(5)));
            }
            long packetDelta=0;int panelReboots=0,hostRestarts=0,reopens=0,rom=0,recovered=0;long? prevPanelUptime=null,prevPackets=null;
            for(int i=0;i<rows.Count;i++){
                if(i>0&&rows[i].HostUptime+5<rows[i-1].HostUptime)hostRestarts++;
                if(rows[i].PanelUptime.HasValue){if(prevPanelUptime.HasValue&&rows[i].PanelUptime!.Value+5<prevPanelUptime.Value)panelReboots++;prevPanelUptime=rows[i].PanelUptime;}
                if(rows[i].Packets.HasValue){if(prevPackets.HasValue&&rows[i].Packets>prevPackets)packetDelta+=rows[i].Packets!.Value-prevPackets.Value;prevPackets=rows[i].Packets;}
                if(i>0&&rows[i].Reopen>rows[i-1].Reopen)reopens+=rows[i].Reopen-rows[i-1].Reopen;
                if(i>0&&rows[i].Rom>rows[i-1].Rom)rom+=rows[i].Rom-rows[i-1].Rom;
                if(i>0&&rows[i].Recovered>rows[i-1].Recovered)recovered+=rows[i].Recovered-rows[i-1].Recovered;
            }
            long panelLatest=-1;int sd=-1;SourceState link=SourceState.NoData;
            if(rows.Count>0){using(var cmd=db.CreateCommand()){cmd.CommandText="SELECT link_state FROM lifecycle_minute WHERE stamp >= $lo AND stamp <= $hi ORDER BY stamp DESC LIMIT 1";cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);var raw=cmd.ExecuteScalar();if(raw is long n&&Enum.IsDefined((SourceState)(int)n))link=(SourceState)(int)n;}
                using var panelCmd=db.CreateCommand();panelCmd.CommandText="SELECT panel_uptime,panel_sd_state FROM lifecycle_minute WHERE stamp >= $lo AND stamp <= $hi AND panel_present=1 ORDER BY stamp DESC LIMIT 1";panelCmd.Parameters.AddWithValue("$lo",lo);panelCmd.Parameters.AddWithValue("$hi",hi);using var r=panelCmd.ExecuteReader();if(r.Read()){panelLatest=r.IsDBNull(0)?-1:r.GetInt64(0);sd=r.IsDBNull(1)?-1:r.GetInt32(1);}}
            return new(window,from,now,rows.Count,hp,hw,hh,ht,pf,pm,ps,packetDelta,panelReboots,panelLatest,sd,hostRestarts,reopens,rom,recovered,link);
        }
    }
    public long Count(){lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM lifecycle_minute";return (long)(cmd.ExecuteScalar()??0L);}}
    public void Dispose()=>db.Dispose();
}
