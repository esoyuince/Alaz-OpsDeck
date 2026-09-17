using System.Globalization;
using Microsoft.Data.Sqlite;
namespace OpsDeck.Core;

public sealed record BridgeTaskSample(SourceState State,int? Open=null,int? Claimed=null,
    int? InProgress=null,int? NeedsApproval=null,int? Completed=null,int? Blocked=null,
    int? Cancelled=null,int? ExpiredClaims=null,DateTimeOffset? CollectedAt=null,
    DateTimeOffset? LatestTaskAt=null,string LatestEvent="",DateTimeOffset? LatestEventAt=null,
    string Detail="")
{
    public int? Active => Claimed.HasValue&&InProgress.HasValue&&NeedsApproval.HasValue
        ? checked(Claimed.Value+InProgress.Value+NeedsApproval.Value):null;
    public int? Total => new[]{Open,Claimed,InProgress,NeedsApproval,Completed,Blocked,Cancelled}.All(x=>x.HasValue)
        ? checked(Open!.Value+Claimed!.Value+InProgress!.Value+NeedsApproval!.Value+Completed!.Value+Blocked!.Value+Cancelled!.Value):null;
}

/// <summary>Reads only sanitized task metadata from the Local Codex Bridge SQLite store.</summary>
public sealed class BridgeTaskReader
{
    public const int FreshSeconds=900,MaxRows=1000000;
    private static readonly string[] Statuses=["open","claimed","in_progress","needs_approval","completed","blocked","cancelled"];
    private static readonly HashSet<string> EventTypes=new(["created","context","claimed","runner_claimed","status","result","runner_approved","e2e_requeued"],StringComparer.Ordinal);
    private readonly string path;
    public BridgeTaskReader(string path)=>this.path=path;
    public BridgeTaskSample Read(DateTimeOffset now)
    {
        if(string.IsNullOrWhiteSpace(path))return new(SourceState.Setup,Detail:"Bridge task metadata store not configured.");
        if(!File.Exists(path))return new(SourceState.Error,CollectedAt:now,Detail:"Configured bridge task metadata store is unavailable.");
        try{return ReadCore(now);}
        catch(Exception e)when(e is SqliteException or InvalidDataException or OverflowException or ArgumentException)
        {return new(SourceState.Error,CollectedAt:now,Detail:"Bridge task metadata could not be validated; no task content was read.");}
    }
    private BridgeTaskSample ReadCore(DateTimeOffset now)
    {
        var cs=new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Cache=SqliteCacheMode.Private,DefaultTimeout=1,Pooling=false}.ToString();
        using var db=new SqliteConnection(cs);db.Open();
        using(var pragma=db.CreateCommand()){pragma.CommandText="PRAGMA query_only=ON; PRAGMA busy_timeout=1000;";pragma.ExecuteNonQuery();}
        RequireColumns(db,"tasks",["status","claim_expires_at","created_at","updated_at"]);
        RequireColumns(db,"task_events",["event_type","created_at"]);
        var counts=Statuses.ToDictionary(x=>x,_=>0,StringComparer.Ordinal);
        using(var cmd=db.CreateCommand())
        {
            cmd.CommandText="SELECT status, COUNT(*) FROM tasks GROUP BY status LIMIT 8";
            using var r=cmd.ExecuteReader();int groups=0;
            while(r.Read())
            {
                if(++groups>7)throw new InvalidDataException("Unexpected task status group count");
                string status=r.GetString(0);if(!counts.ContainsKey(status))throw new InvalidDataException("Unknown bridge task status");
                counts[status]=BoundedCount(r.GetInt64(1));
            }
        }
        int total=checked(counts.Values.Sum());
        if(total>MaxRows)throw new InvalidDataException("Task row bound exceeded");
        DateTimeOffset? latest=ScalarTime(db,"SELECT updated_at FROM tasks ORDER BY updated_at DESC LIMIT 1");
        if(total==0)return new(SourceState.NoData,0,0,0,0,0,0,0,0,now,Detail:"Bridge task store is valid and currently contains no tasks.");
        if(!latest.HasValue||latest>now.AddMinutes(5))throw new InvalidDataException("Invalid latest task timestamp");
        var (eventType,eventAt)=LatestEvent(db,now);
        int? expired=ExpiredClaims(db,now);
        var state=now-latest.Value>TimeSpan.FromSeconds(FreshSeconds)?SourceState.Stale:SourceState.Ok;
        return new(state,counts["open"],counts["claimed"],counts["in_progress"],counts["needs_approval"],
            counts["completed"],counts["blocked"],counts["cancelled"],expired,now,latest,eventType,eventAt,
            "Yalnız Local Codex Bridge görev metadata'sı okunur; görev metni, sonuç/hata gövdeleri, aktörler ve kimlikler okunmaz. Yapılandırılmış test sayacı bu kaynakta yoktur.");
    }
    private static int BoundedCount(long value)
    {if(value<0||value>MaxRows)throw new InvalidDataException("Task count outside bound");return checked((int)value);}
    private static void RequireColumns(SqliteConnection db,string table,string[] required)
    {
        using var cmd=db.CreateCommand();cmd.CommandText=$"PRAGMA table_info({table})";
        using var r=cmd.ExecuteReader();var found=new HashSet<string>(StringComparer.Ordinal);
        while(r.Read())found.Add(r.GetString(1));
        if(required.Any(x=>!found.Contains(x)))throw new InvalidDataException("Unsupported bridge task schema");
    }
    private static DateTimeOffset? ScalarTime(SqliteConnection db,string sql)
    {
        using var cmd=db.CreateCommand();cmd.CommandText=sql;object? value=cmd.ExecuteScalar();
        return value is null or DBNull?null:ParseTime(Convert.ToString(value,CultureInfo.InvariantCulture)??"");
    }
    private static DateTimeOffset ParseTime(string value)
    {
        if(!DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out var time))
            throw new InvalidDataException("Invalid bridge metadata timestamp");
        return time;
    }
    private static (string Type,DateTimeOffset? At) LatestEvent(SqliteConnection db,DateTimeOffset now)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="SELECT event_type, created_at FROM task_events ORDER BY created_at DESC LIMIT 1";
        using var r=cmd.ExecuteReader();if(!r.Read())return ("",null);
        string type=r.GetString(0);if(!EventTypes.Contains(type))throw new InvalidDataException("Unknown bridge event type");
        var at=ParseTime(r.GetString(1));if(at>now.AddMinutes(5))throw new InvalidDataException("Future bridge event");return(type,at);
    }
    private static int? ExpiredClaims(SqliteConnection db,DateTimeOffset now)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="SELECT claim_expires_at FROM tasks WHERE status='claimed' LIMIT 1001";
        using var r=cmd.ExecuteReader();int rows=0,expired=0;
        while(r.Read())
        {
            if(++rows>1000)return null;
            if(r.IsDBNull(0))continue;
            if(ParseTime(r.GetString(0))<now)expired++;
        }
        return expired;
    }
}
