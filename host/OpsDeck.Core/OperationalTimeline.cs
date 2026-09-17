using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public enum OperationalSeverity { Info=0, Warning=1, Error=2 }
public enum OperationalDomain { Host=0, Link=1, Agent=2, Cloud=3, Https=4, Session=5, Power=6, Device=7 }

public sealed record OperationalEvent(DateTimeOffset At,OperationalSeverity Severity,
    OperationalDomain Domain,string Code,string Summary)
{
    public void Validate()
    {
        if(At==default||At<DateTimeOffset.UnixEpoch||At>DateTimeOffset.UtcNow.AddMinutes(5))
            throw new ArgumentException("Invalid operational event time.");
        if(!Enum.IsDefined(Severity)||!Enum.IsDefined(Domain))throw new ArgumentException("Invalid operational event enum.");
        if(!Regex.IsMatch(Code,"^[A-Z0-9_]{1,32}$"))throw new ArgumentException("Invalid operational event code.");
        if(string.IsNullOrWhiteSpace(Summary)||Summary.Length>180||Summary.Any(char.IsControl))
            throw new ArgumentException("Invalid operational event summary.");
    }
}

public sealed record OperationalSnapshot(bool Locked,bool PowerSuspended,
    SourceState LinkState,bool LinkRecovering,int ReopenCount,int RomProbeCount,int RecoveredCount,
    SerialRecoveryAction LastAction,SerialRecoveryReason LastReason,
    SourceState CodexState,SourceState BridgeState,SourceState TaskState,int TaskActive,
    int NeedsApproval,int Blocked,int ExpiredClaims,SourceState Workers,SourceState D1,
    SourceState R2,SourceState Hosting,int HttpsUp,int HttpsTotal);

public static class OperationalDiff
{
    private static OperationalSeverity Severity(SourceState state)=>state switch{
        SourceState.Error or SourceState.Denied=>OperationalSeverity.Error,
        SourceState.Stale or SourceState.NoData or SourceState.Partial=>OperationalSeverity.Warning,
        _=>OperationalSeverity.Info};
    private static string StateText(SourceState state)=>state.ToString().ToUpperInvariant();
    private static OperationalEvent E(DateTimeOffset at,OperationalSeverity severity,OperationalDomain domain,string code,string summary)
        =>new(at,severity,domain,code,summary);
    private static void StateChange(List<OperationalEvent> events,DateTimeOffset at,string code,string label,SourceState before,SourceState after)
    {
        if(before==after)return;
        events.Add(E(at,Severity(after),OperationalDomain.Cloud,code,$"{label}: {StateText(before)} -> {StateText(after)}"));
    }

    public static OperationalEvent[] Generate(OperationalSnapshot before,OperationalSnapshot after,DateTimeOffset at)
    {
        var events=new List<OperationalEvent>();
        if(before.Locked!=after.Locked)events.Add(E(at,OperationalSeverity.Info,OperationalDomain.Session,
            after.Locked?"SESSION_LOCKED":"SESSION_UNLOCKED",after.Locked?"Windows session locked":"Windows session unlocked"));
        if(before.PowerSuspended!=after.PowerSuspended)events.Add(E(at,OperationalSeverity.Info,OperationalDomain.Power,
            after.PowerSuspended?"POWER_SUSPEND":"POWER_RESUME",after.PowerSuspended?"PC observation suspended":"PC observation resumed; baselines reset"));
        if(!before.LinkRecovering&&after.LinkRecovering)events.Add(E(at,OperationalSeverity.Warning,OperationalDomain.Link,"LINK_RECOVERY",
            $"Recovery started: {LinkHealthSnapshot.ActionLabel(after.LastAction)} / {LinkHealthSnapshot.ReasonLabel(after.LastReason)}"));
        if(after.RecoveredCount>before.RecoveredCount)events.Add(E(at,OperationalSeverity.Info,OperationalDomain.Link,"LINK_RECOVERED",
            $"Panel forward link recovered; total recoveries {after.RecoveredCount}"));
        if(before.LinkState!=after.LinkState&&!after.LinkRecovering&&after.RecoveredCount==before.RecoveredCount)
            events.Add(E(at,Severity(after.LinkState),OperationalDomain.Link,"LINK_STATE",
                $"Panel link: {StateText(before.LinkState)} -> {StateText(after.LinkState)}"));
        if(after.NeedsApproval>0&&after.NeedsApproval!=before.NeedsApproval)
            events.Add(E(at,OperationalSeverity.Warning,OperationalDomain.Agent,"TASK_APPROVAL",
                $"Codex tasks needing approval: {after.NeedsApproval}"));
        if(before.NeedsApproval>0&&after.NeedsApproval==0)
            events.Add(E(at,OperationalSeverity.Info,OperationalDomain.Agent,"TASK_APPROVAL_CLEARED","Codex approval queue cleared"));
        if(after.Blocked>0&&after.Blocked!=before.Blocked)
            events.Add(E(at,OperationalSeverity.Error,OperationalDomain.Agent,"TASK_BLOCKED",$"Blocked Codex tasks: {after.Blocked}"));
        if(before.TaskActive==0&&after.TaskActive>0)
            events.Add(E(at,OperationalSeverity.Info,OperationalDomain.Agent,"TASK_ACTIVITY_STARTED",$"Active Codex tasks: {after.TaskActive}"));
        if(before.TaskActive>0&&after.TaskActive==0)
            events.Add(E(at,OperationalSeverity.Info,OperationalDomain.Agent,"TASK_ACTIVITY_IDLE","No active Codex tasks"));
        if(before.TaskState!=after.TaskState&&before.TaskActive==after.TaskActive&&before.NeedsApproval==after.NeedsApproval&&before.Blocked==after.Blocked)
            events.Add(E(at,Severity(after.TaskState),OperationalDomain.Agent,"TASK_STATE",
                $"Codex task metadata: {StateText(before.TaskState)} -> {StateText(after.TaskState)}"));
        StateChange(events,at,"WORKERS_STATE","Workers",before.Workers,after.Workers);
        StateChange(events,at,"D1_STATE","D1",before.D1,after.D1);
        StateChange(events,at,"R2_STATE","R2",before.R2,after.R2);
        StateChange(events,at,"HOSTING_STATE","Hosting",before.Hosting,after.Hosting);
        if(before.HttpsTotal>0&&after.HttpsTotal>0&&(before.HttpsUp!=after.HttpsUp||before.HttpsTotal!=after.HttpsTotal))
        {
            bool restored=after.HttpsUp==after.HttpsTotal;
            events.Add(E(at,restored?OperationalSeverity.Info:OperationalSeverity.Warning,OperationalDomain.Https,
                restored?"HTTPS_RESTORED":"HTTPS_DEGRADED",$"HTTPS reachable: {after.HttpsUp}/{after.HttpsTotal}"));
        }
        foreach(var e in events)e.Validate();
        return events.ToArray();
    }
}

public sealed class OperationalEventStore : IDisposable
{
    public const int RetentionDays=14,MaxRead=500;
    private readonly SqliteConnection db;private readonly object gate=new();
    public OperationalEventStore(string path)
    {
        db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,
            Cache=SqliteCacheMode.Private,DefaultTimeout=1,Pooling=false}.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-512; "+
            "CREATE TABLE IF NOT EXISTS ops_event (id INTEGER PRIMARY KEY AUTOINCREMENT, stamp_ms INTEGER NOT NULL, severity INTEGER NOT NULL, domain INTEGER NOT NULL, code TEXT NOT NULL, summary TEXT NOT NULL); "+
            "CREATE INDEX IF NOT EXISTS idx_ops_event_stamp ON ops_event(stamp_ms DESC);";cmd.ExecuteNonQuery();
    }

    public void Add(OperationalEvent e)
    {
        e.Validate();lock(gate){using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="INSERT INTO ops_event(stamp_ms,severity,domain,code,summary) VALUES($t,$s,$d,$c,$m); DELETE FROM ops_event WHERE stamp_ms < $cutoff;";
            cmd.Parameters.AddWithValue("$t",e.At.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$s",(int)e.Severity);
            cmd.Parameters.AddWithValue("$d",(int)e.Domain);cmd.Parameters.AddWithValue("$c",e.Code);cmd.Parameters.AddWithValue("$m",e.Summary);
            cmd.Parameters.AddWithValue("$cutoff",DateTimeOffset.UtcNow.AddDays(-RetentionDays).ToUnixTimeMilliseconds());cmd.ExecuteNonQuery();tx.Commit();}
    }

    public OperationalEvent[] Read(int limit=200)
    {
        if(limit<1||limit>MaxRead)throw new ArgumentOutOfRangeException(nameof(limit));
        lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT stamp_ms,severity,domain,code,summary FROM ops_event ORDER BY stamp_ms DESC,id DESC LIMIT $n";cmd.Parameters.AddWithValue("$n",limit);
            using var r=cmd.ExecuteReader();var rows=new List<OperationalEvent>();
            while(r.Read())try{
                var e=new OperationalEvent(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)),
                    (OperationalSeverity)r.GetInt32(1),(OperationalDomain)r.GetInt32(2),r.GetString(3),r.GetString(4));e.Validate();rows.Add(e);
            }catch(Exception ex)when(ex is ArgumentException or ArgumentOutOfRangeException){}
            return rows.ToArray();}
    }
    public OperationalEvent[] ReadRange(DateTimeOffset from,DateTimeOffset to,int limit=200)
    {
        if(from==default||to==default||from>to||to-from>TimeSpan.FromDays(RetentionDays+1))throw new ArgumentException("Invalid operational event range.");
        if(limit<1||limit>MaxRead)throw new ArgumentOutOfRangeException(nameof(limit));
        long lo=from.ToUnixTimeMilliseconds(),hi=to.ToUnixTimeMilliseconds();
        lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT stamp_ms,severity,domain,code,summary FROM ops_event WHERE stamp_ms >= $lo AND stamp_ms <= $hi ORDER BY stamp_ms DESC,id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);cmd.Parameters.AddWithValue("$n",limit);
            using var r=cmd.ExecuteReader();var rows=new List<OperationalEvent>();
            while(r.Read())try{var e=new OperationalEvent(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)),(OperationalSeverity)r.GetInt32(1),(OperationalDomain)r.GetInt32(2),r.GetString(3),r.GetString(4));e.Validate();rows.Add(e);}
            catch(Exception ex)when(ex is ArgumentException or ArgumentOutOfRangeException){}
            return rows.ToArray();}
    }
    public OperationalRangeSummary SummarizeRange(DateTimeOffset from,DateTimeOffset to)
    {
        if(from==default||to==default||from>to||to-from>TimeSpan.FromDays(RetentionDays+1))throw new ArgumentException("Invalid operational summary range.");
        long lo=from.ToUnixTimeMilliseconds(),hi=to.ToUnixTimeMilliseconds();
        lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*),"+
            "COALESCE(SUM(CASE WHEN severity=0 THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN severity=1 THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN severity=2 THEN 1 ELSE 0 END),0),COUNT(DISTINCT domain),"+
            "COALESCE(SUM(CASE WHEN code='LINK_RECOVERY' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN code='LINK_RECOVERED' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN code='PANEL_REBOOT' THEN 1 ELSE 0 END),0),"+
            "COALESCE(SUM(CASE WHEN code='HOST_STARTED' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN code='HOST_STOPPED' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN code='POWER_SUSPEND' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN code='POWER_RESUME' THEN 1 ELSE 0 END),0),"+
            "COALESCE(SUM(CASE WHEN code='HTTPS_DEGRADED' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN code='HTTPS_RESTORED' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN code='TASK_APPROVAL' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN code='TASK_BLOCKED' THEN 1 ELSE 0 END),0),"+
            "COALESCE(SUM(CASE WHEN domain=3 AND code LIKE '%_STATE' THEN 1 ELSE 0 END),0) FROM ops_event WHERE stamp_ms >= $lo AND stamp_ms <= $hi";
            cmd.Parameters.AddWithValue("$lo",lo);cmd.Parameters.AddWithValue("$hi",hi);using var r=cmd.ExecuteReader();r.Read();int I(int i)=>checked((int)r.GetInt64(i));
            return new(I(0),I(1),I(2),I(3),I(4),I(5),I(6),I(7),I(8),I(9),I(10),I(11),I(12),I(13),I(14),I(15),I(16));}
    }
    public long Count(){lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM ops_event";return (long)(cmd.ExecuteScalar()??0L);}}
    public void Dispose()=>db.Dispose();
}
