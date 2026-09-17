using Microsoft.Data.Sqlite;
namespace OpsDeck.Core;

public enum TelemetryWindow { M15=15,H1=60,H24=1440,D7=10080 }
public sealed record TrendMetric(string Key,string Label,string Unit,double? Min,double? Avg,double? Max,int Points);
public sealed record VolumeTrend(string Id,double? MinUsed,double? AvgUsed,double? MaxUsed,double? AvgFreeGib,int Points);
public sealed record LinkTrend(int Reopens,int RomProbes,int Recoveries,SourceState LatestState,int Points);
public sealed record TelemetryTrend(TelemetryWindow Window,DateTimeOffset From,DateTimeOffset To,
    TrendMetric[] Metrics,VolumeTrend[] Volumes,LinkTrend Link,int MinuteRows);
public sealed record TelemetryPoint(DateTimeOffset At,double? Cpu,double? IntelGpu,double? Gpu,
    double? CpuTemp,double? ChassisTemp,double? GpuTemp,double? RamUsed,double? VramUsed,
    double? Fan1,double? Fan2,double? Rx,double? Tx,double? IntelShared);
public sealed record VolumePoint(DateTimeOffset At,string Id,double? Used,double? FreeGib,double? TotalGib);

public static class TelemetryWindows
{
    public static TimeSpan Span(TelemetryWindow value)=>TimeSpan.FromMinutes((int)value);
    public static string Label(TelemetryWindow value)=>value switch{
        TelemetryWindow.M15=>"15m",TelemetryWindow.H1=>"1h",TelemetryWindow.H24=>"24h",_=>"7d"};
}
public sealed class TelemetryHistoryStore : IDisposable
{
    public const int RetentionDays=7,MaxSeriesRows=10080;
    private readonly SqliteConnection db;private readonly object gate=new();
    private sealed record Def(string Key,string Label,string Unit,string Avg,string Min,string Max);
    private static readonly Def[] Defs=[
        new("cpu","CPU","%","cpu_avg","cpu_min","cpu_max"),
        new("intel_gpu","Intel GPU","%","intel_avg","intel_min","intel_max"),
        new("gpu","NVIDIA GPU","%","gpu_avg","gpu_min","gpu_max"),
        new("cpu_temp","CPU temp","C","cpu_temp_avg","cpu_temp_min","cpu_temp_max"),
        new("chassis_temp","Chassis temp","C","chassis_temp_avg","chassis_temp_min","chassis_temp_max"),
        new("gpu_temp","NVIDIA temp","C","gpu_temp_avg","gpu_temp_min","gpu_temp_max"),
        new("ram_used","RAM used","GiB","ram_avg","ram_min","ram_max"),
        new("vram_used","VRAM used","GiB","vram_avg","vram_min","vram_max"),
        new("fan1","Fan 1","RPM","fan1_avg","fan1_min","fan1_max"),
        new("fan2","Fan 2","RPM","fan2_avg","fan2_min","fan2_max"),
        new("rx","RX","Mb/s","rx_avg","rx_min","rx_max"),
        new("tx","TX","Mb/s","tx_avg","tx_min","tx_max"),
        new("intel_shared","Intel shared","GiB","intel_shared_avg","intel_shared_min","intel_shared_max")];
    public static TelemetryMetricDescriptor[] MetricDescriptors=>Defs.Select(x=>new TelemetryMetricDescriptor(x.Key,x.Label,x.Unit)).ToArray();

    public TelemetryHistoryStore(string path)
    {
        db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,DefaultTimeout=1}.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-1024; "+
            "CREATE TABLE IF NOT EXISTS telemetry_minute (stamp INTEGER PRIMARY KEY,samples INTEGER NOT NULL,"+
            "cpu_avg REAL,cpu_min REAL,cpu_max REAL,intel_avg REAL,intel_min REAL,intel_max REAL,gpu_avg REAL,gpu_min REAL,gpu_max REAL,"+
            "cpu_temp_avg REAL,cpu_temp_min REAL,cpu_temp_max REAL,chassis_temp_avg REAL,chassis_temp_min REAL,chassis_temp_max REAL,"+
            "gpu_temp_avg REAL,gpu_temp_min REAL,gpu_temp_max REAL,ram_avg REAL,ram_min REAL,ram_max REAL,vram_avg REAL,vram_min REAL,vram_max REAL,"+
            "fan1_avg REAL,fan1_min REAL,fan1_max REAL,fan2_avg REAL,fan2_min REAL,fan2_max REAL,rx_avg REAL,rx_min REAL,rx_max REAL,tx_avg REAL,tx_min REAL,tx_max REAL,"+
            "intel_shared_avg REAL,intel_shared_min REAL,intel_shared_max REAL,"+
            "link_state INTEGER NOT NULL,link_reopen INTEGER NOT NULL,link_rom INTEGER NOT NULL,link_recovered INTEGER NOT NULL);";
        cmd.ExecuteNonQuery();
        EnsureColumn("intel_shared_avg");EnsureColumn("intel_shared_min");EnsureColumn("intel_shared_max");
        using var volume=db.CreateCommand();volume.CommandText="CREATE TABLE IF NOT EXISTS volume_minute (stamp INTEGER NOT NULL,volume_id TEXT NOT NULL,used_avg REAL,used_min REAL,used_max REAL,free_avg REAL,total_avg REAL,PRIMARY KEY(stamp,volume_id)); CREATE INDEX IF NOT EXISTS idx_volume_minute_stamp ON volume_minute(stamp);";volume.ExecuteNonQuery();
    }
    private void EnsureColumn(string name)
    {
        using var check=db.CreateCommand();check.CommandText="PRAGMA table_info(telemetry_minute)";using var r=check.ExecuteReader();while(r.Read())if(string.Equals(r.GetString(1),name,StringComparison.Ordinal))return;
        using var alter=db.CreateCommand();alter.CommandText=$"ALTER TABLE telemetry_minute ADD COLUMN {name} REAL";alter.ExecuteNonQuery();
    }
    private static (double? Avg,double? Min,double? Max) Agg(IReadOnlyList<PcSample> rows,Func<PcSample,double?> get)
    {
        var v=rows.Select(get).Where(x=>x.HasValue&&double.IsFinite(x.Value)&&x.Value>=0).Select(x=>x!.Value).ToArray();
        return v.Length==0?(null,null,null):(v.Average(),v.Min(),v.Max());
    }
    private static void Param(SqliteCommand cmd,string name,double? value)=>cmd.Parameters.AddWithValue(name,value.HasValue?(object)value.Value:DBNull.Value);
    private static void Triple(SqliteCommand cmd,string stem,(double? Avg,double? Min,double? Max) v)
    {Param(cmd,"$"+stem+"a",v.Avg);Param(cmd,"$"+stem+"n",v.Min);Param(cmd,"$"+stem+"x",v.Max);}

    public void Add(DateTimeOffset stamp,IReadOnlyList<PcSample> samples,LinkHealthSnapshot link)
    {
        if(samples.Count is <1 or >3600)throw new ArgumentException("Invalid telemetry sample count.");
        long minute=stamp.ToUnixTimeSeconds()/60*60,cutoff=stamp.AddDays(-RetentionDays).ToUnixTimeSeconds();
        var cpu=Agg(samples,x=>x.Cpu);var intel=Agg(samples,x=>x.IntelGpu);var gpu=Agg(samples,x=>x.Gpu);
        var ct=Agg(samples,x=>x.CpuTemp);var ch=Agg(samples,x=>x.ChassisTemp);var gt=Agg(samples,x=>x.GpuTemp);
        var ram=Agg(samples,x=>x.RamUsedGib);var vram=Agg(samples,x=>x.VramUsedGib);var shared=Agg(samples,x=>x.IntelSharedGib);
        var f1=Agg(samples,x=>x.Fan1Rpm);var f2=Agg(samples,x=>x.Fan2Rpm);var rx=Agg(samples,x=>x.RxMbps);var txv=Agg(samples,x=>x.TxMbps);
        lock(gate){using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            string columns=string.Join(',',Defs.SelectMany(d=>new[]{d.Avg,d.Min,d.Max}));
            string parameters=string.Join(',',Enumerable.Range(0,Defs.Length).SelectMany((_,i)=>new[]{"$m"+i+"a","$m"+i+"n","$m"+i+"x"}));
            cmd.CommandText=$"INSERT OR REPLACE INTO telemetry_minute (stamp,samples,{columns},link_state,link_reopen,link_rom,link_recovered) VALUES ($t,$s,{parameters},$ls,$lr,$lp,$lc);";
            cmd.Parameters.AddWithValue("$t",minute);cmd.Parameters.AddWithValue("$s",samples.Count);
            var values=new[]{cpu,intel,gpu,ct,ch,gt,ram,vram,f1,f2,rx,txv,shared};
            for(int i=0;i<values.Length;i++){Param(cmd,"$m"+i+"a",values[i].Avg);Param(cmd,"$m"+i+"n",values[i].Min);Param(cmd,"$m"+i+"x",values[i].Max);}
            cmd.Parameters.AddWithValue("$ls",(int)link.State);cmd.Parameters.AddWithValue("$lr",link.ReopenCount);cmd.Parameters.AddWithValue("$lp",link.RomProbeCount);cmd.Parameters.AddWithValue("$lc",link.RecoveredCount);cmd.ExecuteNonQuery();
            AddVolumes(tx,minute,samples);using var prune=db.CreateCommand();prune.Transaction=tx;prune.CommandText="DELETE FROM telemetry_minute WHERE stamp < $c; DELETE FROM volume_minute WHERE stamp < $c;";prune.Parameters.AddWithValue("$c",cutoff);prune.ExecuteNonQuery();tx.Commit();}
    }
    private void AddVolumes(SqliteTransaction tx,long minute,IReadOnlyList<PcSample> samples)
    {
        var ids=samples.SelectMany(s=>s.Volumes??[]).Where(v=>v.Valid).Select(v=>v.Id).Distinct(StringComparer.Ordinal).Take(16).ToArray();
        foreach(string id in ids)
        {
            var rows=samples.SelectMany(s=>s.Volumes??[]).Where(v=>v.Valid&&v.Id==id).ToArray();if(rows.Length==0)continue;
            var used=rows.Select(v=>v.UsedPercent).Where(v=>v.HasValue).Select(v=>v!.Value).ToArray();
            var free=rows.Select(v=>v.FreeGib).Where(v=>v.HasValue).Select(v=>v!.Value).ToArray();
            var total=rows.Select(v=>v.TotalGib).Where(v=>v.HasValue).Select(v=>v!.Value).ToArray();
            using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="INSERT OR REPLACE INTO volume_minute(stamp,volume_id,used_avg,used_min,used_max,free_avg,total_avg) VALUES($t,$id,$a,$n,$x,$f,$z);";
            cmd.Parameters.AddWithValue("$t",minute);cmd.Parameters.AddWithValue("$id",id);
            Param(cmd,"$a",used.Length>0?used.Average():null);Param(cmd,"$n",used.Length>0?used.Min():null);Param(cmd,"$x",used.Length>0?used.Max():null);
            Param(cmd,"$f",free.Length>0?free.Average():null);Param(cmd,"$z",total.Length>0?total.Average():null);cmd.ExecuteNonQuery();
        }
    }

    private static DateTimeOffset From(TelemetryWindow window,DateTimeOffset now)=>now-TelemetryWindows.Span(window);
    private static double? D(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetDouble(i);
    public long Count(){lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM telemetry_minute";return (long)(cmd.ExecuteScalar()??0L);}}
    public TelemetryTrend ReadSummary(TelemetryWindow window,DateTimeOffset now)
    {
        var from=From(window,now);long lo=from.ToUnixTimeSeconds(),hi=now.ToUnixTimeSeconds();var metrics=new List<TrendMetric>();
        lock(gate)
        {
            foreach(var d in Defs){using var cmd=db.CreateCommand();cmd.CommandText=$"SELECT MIN({d.Min}),AVG({d.Avg}),MAX({d.Max}),COUNT({d.Avg}) FROM telemetry_minute WHERE stamp >= $lo AND stamp <= $hi";cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);using var r=cmd.ExecuteReader();r.Read();metrics.Add(new(d.Key,d.Label,d.Unit,D(r,0),D(r,1),D(r,2),r.GetInt32(3)));}
            var volumes=new List<VolumeTrend>();using(var cmd=db.CreateCommand()){
                cmd.CommandText="SELECT volume_id,MIN(used_min),AVG(used_avg),MAX(used_max),AVG(free_avg),COUNT(used_avg) FROM volume_minute WHERE stamp >= $lo AND stamp <= $hi GROUP BY volume_id ORDER BY volume_id";
                cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);using var r=cmd.ExecuteReader();while(r.Read())volumes.Add(new(r.GetString(0),D(r,1),D(r,2),D(r,3),D(r,4),r.GetInt32(5)));
            }
            int reopen=0,rom=0,recovered=0,points=0;SourceState state=SourceState.NoData;
            using(var cmd=db.CreateCommand()){
                cmd.CommandText="SELECT COALESCE(MAX(link_reopen)-MIN(link_reopen),0),COALESCE(MAX(link_rom)-MIN(link_rom),0),COALESCE(MAX(link_recovered)-MIN(link_recovered),0),COUNT(*) FROM telemetry_minute WHERE stamp >= $lo AND stamp <= $hi";
                cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);using var r=cmd.ExecuteReader();r.Read();reopen=r.GetInt32(0);rom=r.GetInt32(1);recovered=r.GetInt32(2);points=r.GetInt32(3);
            }
            if(points>0){using var cmd=db.CreateCommand();cmd.CommandText="SELECT link_state FROM telemetry_minute WHERE stamp >= $lo AND stamp <= $hi ORDER BY stamp DESC LIMIT 1";cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);var v=cmd.ExecuteScalar();if(v is long n&&Enum.IsDefined((SourceState)(int)n))state=(SourceState)(int)n;}
            return new(window,from,now,metrics.ToArray(),volumes.ToArray(),new(reopen,rom,recovered,state,points),points);
        }
    }
    public TelemetryPoint[] ReadSeries(TelemetryWindow window,DateTimeOffset now,int maxPoints=120)
    {
        if(maxPoints is <10 or >1000)throw new ArgumentOutOfRangeException(nameof(maxPoints));
        long lo=From(window,now).ToUnixTimeSeconds(),hi=now.ToUnixTimeSeconds();var rows=new List<TelemetryPoint>();
        lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT stamp,cpu_avg,intel_avg,gpu_avg,cpu_temp_avg,chassis_temp_avg,gpu_temp_avg,ram_avg,vram_avg,fan1_avg,fan2_avg,rx_avg,tx_avg,intel_shared_avg FROM telemetry_minute WHERE stamp >= $lo AND stamp <= $hi ORDER BY stamp";cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);using var r=cmd.ExecuteReader();while(r.Read())rows.Add(new(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)),D(r,1),D(r,2),D(r,3),D(r,4),D(r,5),D(r,6),D(r,7),D(r,8),D(r,9),D(r,10),D(r,11),D(r,12),D(r,13)));}
        if(rows.Count<=maxPoints)return rows.ToArray();
        var sampled=new TelemetryPoint[maxPoints];
        for(int i=0;i<maxPoints;i++){
            int index=(int)Math.Round(i*(rows.Count-1d)/(maxPoints-1d),MidpointRounding.AwayFromZero);
            sampled[i]=rows[Math.Clamp(index,0,rows.Count-1)];
        }
        return sampled;
    }
    public VolumePoint[] ReadVolumeSeries(TelemetryWindow window,DateTimeOffset now,string id,int maxPoints=120)
    {
        if(string.IsNullOrWhiteSpace(id)||id.Length>16)throw new ArgumentException("Invalid volume id.");
        if(maxPoints is <10 or >1000)throw new ArgumentOutOfRangeException(nameof(maxPoints));
        long lo=From(window,now).ToUnixTimeSeconds(),hi=now.ToUnixTimeSeconds();var rows=new List<VolumePoint>();
        lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT stamp,volume_id,used_avg,free_avg,total_avg FROM volume_minute WHERE stamp >= $lo AND stamp <= $hi AND volume_id=$id ORDER BY stamp";cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);cmd.Parameters.AddWithValue("$id",id);using var r=cmd.ExecuteReader();while(r.Read())rows.Add(new(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)),r.GetString(1),D(r,2),D(r,3),D(r,4)));}
        if(rows.Count<=maxPoints)return rows.ToArray();
        var sampled=new VolumePoint[maxPoints];for(int i=0;i<maxPoints;i++){int index=(int)Math.Round(i*(rows.Count-1d)/(maxPoints-1d),MidpointRounding.AwayFromZero);sampled[i]=rows[Math.Clamp(index,0,rows.Count-1)];}return sampled;
    }

    private static double? Mean(IEnumerable<double?> values)
    {
        var rows=values.Where(x=>x.HasValue&&double.IsFinite(x.Value)).Select(x=>x!.Value).ToArray();
        return rows.Length==0?null:rows.Average();
    }
    private static (long Lo,long Hi,long Range,int Count) FixedWindow(TelemetryWindow window,DateTimeOffset now,int maxPoints)
    {
        if(maxPoints is <10 or >120)throw new ArgumentOutOfRangeException(nameof(maxPoints));
        int count=Math.Min(maxPoints,(int)window);long hi=now.ToUnixTimeSeconds()/60*60;
        long range=(long)TelemetryWindows.Span(window).TotalSeconds,lo=hi-range+60;
        return (lo,hi,range,count);
    }
    public TelemetryPoint[] ReadFixedSeries(TelemetryWindow window,DateTimeOffset now,int maxPoints=48)
    {
        var w=FixedWindow(window,now,maxPoints);var rows=new List<TelemetryPoint>();
        lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT stamp,cpu_avg,intel_avg,gpu_avg,cpu_temp_avg,chassis_temp_avg,gpu_temp_avg,ram_avg,vram_avg,fan1_avg,fan2_avg,rx_avg,tx_avg,intel_shared_avg FROM telemetry_minute WHERE stamp >= $lo AND stamp <= $hi ORDER BY stamp";cmd.Parameters.AddWithValue("$lo",w.Lo);cmd.Parameters.AddWithValue("$hi",w.Hi);using var r=cmd.ExecuteReader();while(r.Read())rows.Add(new(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)),D(r,1),D(r,2),D(r,3),D(r,4),D(r,5),D(r,6),D(r,7),D(r,8),D(r,9),D(r,10),D(r,11),D(r,12),D(r,13)));}
        var result=new TelemetryPoint[w.Count];
        for(int i=0;i<w.Count;i++){long stamp=w.Count==1?w.Hi:w.Lo+(long)Math.Round(i*(w.Hi-w.Lo)/(double)(w.Count-1));result[i]=new(DateTimeOffset.FromUnixTimeSeconds(stamp),null,null,null,null,null,null,null,null,null,null,null,null,null);}
        foreach(var g in rows.GroupBy(x=>Math.Clamp((int)(((x.At.ToUnixTimeSeconds()-w.Lo)*w.Count)/w.Range),0,w.Count-1))){var a=g.ToArray();result[g.Key]=new(a.Min(x=>x.At),Mean(a.Select(x=>x.Cpu)),Mean(a.Select(x=>x.IntelGpu)),Mean(a.Select(x=>x.Gpu)),Mean(a.Select(x=>x.CpuTemp)),Mean(a.Select(x=>x.ChassisTemp)),Mean(a.Select(x=>x.GpuTemp)),Mean(a.Select(x=>x.RamUsed)),Mean(a.Select(x=>x.VramUsed)),Mean(a.Select(x=>x.Fan1)),Mean(a.Select(x=>x.Fan2)),Mean(a.Select(x=>x.Rx)),Mean(a.Select(x=>x.Tx)),Mean(a.Select(x=>x.IntelShared)));}
        return result;
    }
    public VolumePoint[] ReadFixedVolumeSeries(TelemetryWindow window,DateTimeOffset now,string id,int maxPoints=48)
    {
        if(string.IsNullOrWhiteSpace(id)||id.Length>16)throw new ArgumentException("Invalid volume id.");
        var w=FixedWindow(window,now,maxPoints);var rows=new List<VolumePoint>();
        lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT stamp,volume_id,used_avg,free_avg,total_avg FROM volume_minute WHERE stamp >= $lo AND stamp <= $hi AND volume_id=$id ORDER BY stamp";cmd.Parameters.AddWithValue("$lo",w.Lo);cmd.Parameters.AddWithValue("$hi",w.Hi);cmd.Parameters.AddWithValue("$id",id);using var r=cmd.ExecuteReader();while(r.Read())rows.Add(new(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)),r.GetString(1),D(r,2),D(r,3),D(r,4)));}
        var result=new VolumePoint[w.Count];
        for(int i=0;i<w.Count;i++){long stamp=w.Count==1?w.Hi:w.Lo+(long)Math.Round(i*(w.Hi-w.Lo)/(double)(w.Count-1));result[i]=new(DateTimeOffset.FromUnixTimeSeconds(stamp),id,null,null,null);}
        foreach(var g in rows.GroupBy(x=>Math.Clamp((int)(((x.At.ToUnixTimeSeconds()-w.Lo)*w.Count)/w.Range),0,w.Count-1))){var a=g.ToArray();result[g.Key]=new(a.Min(x=>x.At),id,Mean(a.Select(x=>x.Used)),Mean(a.Select(x=>x.FreeGib)),Mean(a.Select(x=>x.TotalGib)));}
        return result;
    }

    public (DateTimeOffset? First,DateTimeOffset? Last) ReadMetricBounds(TelemetryWindow window,DateTimeOffset now,string key)
    {
        var d=Defs.FirstOrDefault(x=>string.Equals(x.Key,key,StringComparison.Ordinal))??throw new ArgumentException("Unknown telemetry metric key.");
        long lo=From(window,now).ToUnixTimeSeconds(),hi=now.ToUnixTimeSeconds();
        lock(gate){using var cmd=db.CreateCommand();cmd.CommandText=$"SELECT MIN(stamp),MAX(stamp) FROM telemetry_minute WHERE stamp >= $lo AND stamp <= $hi AND {d.Avg} IS NOT NULL";cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);using var r=cmd.ExecuteReader();r.Read();return (r.IsDBNull(0)?null:DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)),r.IsDBNull(1)?null:DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)));}
    }
    public (DateTimeOffset? First,DateTimeOffset? Last) ReadVolumeBounds(TelemetryWindow window,DateTimeOffset now,string id)
    {
        if(string.IsNullOrWhiteSpace(id)||id.Length>16)throw new ArgumentException("Invalid volume id.");
        long lo=From(window,now).ToUnixTimeSeconds(),hi=now.ToUnixTimeSeconds();
        lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT MIN(stamp),MAX(stamp) FROM volume_minute WHERE stamp >= $lo AND stamp <= $hi AND volume_id=$id AND used_avg IS NOT NULL";cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);cmd.Parameters.AddWithValue("$id",id);using var r=cmd.ExecuteReader();r.Read();return (r.IsDBNull(0)?null:DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)),r.IsDBNull(1)?null:DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)));}
    }

    public MetricAveragePoint[] ReadMetricRange(string key,DateTimeOffset from,DateTimeOffset to,int maxRows=2000)
    {
        if(from==default||to==default||from>to||to-from>TimeSpan.FromDays(RetentionDays+1))throw new ArgumentException("Invalid telemetry metric range.");
        if(maxRows is <1 or > MaxSeriesRows)throw new ArgumentOutOfRangeException(nameof(maxRows));
        var d=Defs.FirstOrDefault(x=>string.Equals(x.Key,key,StringComparison.Ordinal))??throw new ArgumentException("Unknown telemetry metric key.");
        long lo=from.ToUnixTimeSeconds(),hi=to.ToUnixTimeSeconds();var rows=new List<MetricAveragePoint>();
        lock(gate){using var cmd=db.CreateCommand();cmd.CommandText=$"SELECT stamp,{d.Avg} FROM telemetry_minute WHERE stamp >= $lo AND stamp < $hi AND {d.Avg} IS NOT NULL ORDER BY stamp LIMIT $n";cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);cmd.Parameters.AddWithValue("$n",maxRows);using var r=cmd.ExecuteReader();while(r.Read()){double v=r.GetDouble(1);if(double.IsFinite(v))rows.Add(new(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)),v));}}
        return rows.ToArray();
    }

    public void Dispose()=>db.Dispose();
}
