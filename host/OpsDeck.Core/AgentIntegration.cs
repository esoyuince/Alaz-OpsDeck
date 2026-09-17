using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace OpsDeck.Core;

public enum UnifiedProviderKind { ChatGptMcp=1, ManagedCodex=2, RemoteDesktopCommander=3 }
public sealed record UnifiedProviderSnapshot(UnifiedProviderKind Kind,string Name,SourceState State,string Status,string Detail,AgentCapability Capabilities,DateTimeOffset? ObservedAt=null);
public enum AgentHandoffState { Created=0,Ready=1,Accepted=2,Completed=3,Failed=4,Cancelled=5 }
public sealed record AgentHandoffRecord(string HandoffId,AgentProviderKind From,AgentProviderKind To,string Target,AgentHandoffState State,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt,string Code="");

public sealed class AgentHandoffStore : IDisposable
{
    private readonly SqliteConnection db;private readonly object gate=new();
    public AgentHandoffStore(string path)
    {
        db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,Cache=SqliteCacheMode.Private,Pooling=false}.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; CREATE TABLE IF NOT EXISTS agent_handoff(handoff_id TEXT PRIMARY KEY,from_provider INTEGER NOT NULL,to_provider INTEGER NOT NULL,target TEXT NOT NULL,state INTEGER NOT NULL,created_ms INTEGER NOT NULL,updated_ms INTEGER NOT NULL,code TEXT NOT NULL); CREATE INDEX IF NOT EXISTS idx_agent_handoff_updated ON agent_handoff(updated_ms DESC);";cmd.ExecuteNonQuery();
    }
    public AgentHandoffRecord Create(string id,AgentProviderKind from,AgentProviderKind to,string target,DateTimeOffset now)
    {
        Validate(id,from,to,target);lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO agent_handoff VALUES($i,$f,$t,$x,$s,$c,$u,'CREATED')";cmd.Parameters.AddWithValue("$i",id);cmd.Parameters.AddWithValue("$f",(int)from);cmd.Parameters.AddWithValue("$t",(int)to);cmd.Parameters.AddWithValue("$x",target);cmd.Parameters.AddWithValue("$s",(int)AgentHandoffState.Created);cmd.Parameters.AddWithValue("$c",now.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$u",now.ToUnixTimeMilliseconds());cmd.ExecuteNonQuery();return new(id,from,to,target,AgentHandoffState.Created,now,now,"CREATED");}
    }
    public AgentHandoffRecord Transition(string id,AgentHandoffState next,string code,DateTimeOffset now)
    {
        if(!Enum.IsDefined(next)||!Regex.IsMatch(code,"^[A-Z0-9_]{2,32}$"))throw new ArgumentException("Invalid handoff transition.");lock(gate){var current=Get(id)??throw new KeyNotFoundException("Unknown handoff.");if(!Allowed(current.State,next))throw new InvalidOperationException("Invalid handoff state transition.");using var cmd=db.CreateCommand();cmd.CommandText="UPDATE agent_handoff SET state=$s,updated_ms=$u,code=$c WHERE handoff_id=$i";cmd.Parameters.AddWithValue("$s",(int)next);cmd.Parameters.AddWithValue("$u",now.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$c",code);cmd.Parameters.AddWithValue("$i",id);cmd.ExecuteNonQuery();return current with{State=next,UpdatedAt=now,Code=code};}
    }
    public AgentHandoffRecord? Get(string id)
    {lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT handoff_id,from_provider,to_provider,target,state,created_ms,updated_ms,code FROM agent_handoff WHERE handoff_id=$i";cmd.Parameters.AddWithValue("$i",id);using var r=cmd.ExecuteReader();return r.Read()?Read(r):null;}}
    public AgentHandoffRecord[] ReadRecent(int limit=100)
    {if(limit<1||limit>500)throw new ArgumentOutOfRangeException(nameof(limit));lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT handoff_id,from_provider,to_provider,target,state,created_ms,updated_ms,code FROM agent_handoff ORDER BY updated_ms DESC LIMIT $n";cmd.Parameters.AddWithValue("$n",limit);using var r=cmd.ExecuteReader();var rows=new List<AgentHandoffRecord>();while(r.Read())rows.Add(Read(r));return rows.ToArray();}}
    private static AgentHandoffRecord Read(SqliteDataReader r)=>new(r.GetString(0),(AgentProviderKind)r.GetInt32(1),(AgentProviderKind)r.GetInt32(2),r.GetString(3),(AgentHandoffState)r.GetInt32(4),DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)),DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),r.GetString(7));
    private static void Validate(string id,AgentProviderKind from,AgentProviderKind to,string target)
    {if(!Regex.IsMatch(id,"^[A-Za-z0-9_-]{8,80}$")||!Enum.IsDefined(from)||!Enum.IsDefined(to)||from==to||!Regex.IsMatch(target,"^[A-Za-z0-9._:-]{1,96}$"))throw new ArgumentException("Invalid handoff metadata.");}
    private static bool Allowed(AgentHandoffState from,AgentHandoffState to)=>from switch{AgentHandoffState.Created=>to is AgentHandoffState.Ready or AgentHandoffState.Cancelled,AgentHandoffState.Ready=>to is AgentHandoffState.Accepted or AgentHandoffState.Cancelled,AgentHandoffState.Accepted=>to is AgentHandoffState.Completed or AgentHandoffState.Failed or AgentHandoffState.Cancelled,_=>false};
    public void Dispose()=>db.Dispose();
}
