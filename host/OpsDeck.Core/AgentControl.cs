using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public enum AgentProviderKind { ChatGPT=1, Codex=2, Hermes=3 }
[Flags] public enum AgentCapability { None=0, SubmitTask=1, StopTask=2, ResumeTask=4, RetryTask=8, ContinueTask=16 }
public enum AgentCommandAction { SubmitTask=1, StopTask=2, ResumeTask=3, RetryTask=4, ContinueTask=5 }
public enum AgentCommandState { Requested=0, Approved=1, Executing=2, Succeeded=3, Failed=4, Rejected=5, Expired=6, Cancelled=7 }

public sealed record AgentCommandRequest(string RequestId,AgentProviderKind Provider,AgentCommandAction Action,
    string Target,string IdempotencyKey,DateTimeOffset CreatedAt,DateTimeOffset ExpiresAt,bool RequiresApproval=true)
{
    public void Validate(DateTimeOffset now)
    {
        if(!Regex.IsMatch(RequestId,"^[A-Za-z0-9_-]{8,80}$"))throw new ArgumentException("Invalid request id.");
        if(!Enum.IsDefined(Provider)||!Enum.IsDefined(Action))throw new ArgumentException("Invalid agent command enum.");
        if(!Regex.IsMatch(Target,"^[A-Za-z0-9._:-]{1,96}$"))throw new ArgumentException("Invalid agent target.");
        if(!Regex.IsMatch(IdempotencyKey,"^[A-Za-z0-9._:-]{8,128}$"))throw new ArgumentException("Invalid idempotency key.");
        if(CreatedAt==default||ExpiresAt<=CreatedAt||ExpiresAt-CreatedAt>TimeSpan.FromMinutes(10))throw new ArgumentException("Invalid command lifetime.");
        if(CreatedAt>now.AddMinutes(1)||ExpiresAt<=now)throw new ArgumentException("Invalid command time.");
    }
}

public sealed record AgentCommand(string RequestId,AgentProviderKind Provider,AgentCommandAction Action,string Target,
    string IdempotencyKey,bool RequiresApproval,DateTimeOffset CreatedAt,DateTimeOffset ExpiresAt,
    AgentCommandState State,DateTimeOffset UpdatedAt,string ResultCode="");
public sealed record AgentCommandEvent(DateTimeOffset At,string RequestId,AgentCommandState From,AgentCommandState To,string Code);
public sealed record AgentCommandOutcome(bool Success,string Code)
{
    public void Validate()
    {
        if(!Regex.IsMatch(Code,"^[A-Z0-9_]{2,32}$"))throw new ArgumentException("Invalid provider result code.");
    }
}

public interface IAgentControlProvider
{
    AgentProviderKind Kind{get;}
    AgentCapability Capabilities{get;}
    Task<AgentCommandOutcome> ExecuteAsync(AgentCommand command,CancellationToken ct);
}

public sealed record AgentControlPolicy(TimeSpan RequestLifetime,TimeSpan ExecutionTimeout,bool RequireApproval,
    AgentProviderKind[] AllowedProviders,AgentCommandAction[] AllowedActions)
{
    public static AgentControlPolicy SafeDefault=>new(TimeSpan.FromMinutes(2),TimeSpan.FromSeconds(30),true,
        [AgentProviderKind.ChatGPT,AgentProviderKind.Codex],[AgentCommandAction.SubmitTask,AgentCommandAction.StopTask,AgentCommandAction.ResumeTask,AgentCommandAction.RetryTask,AgentCommandAction.ContinueTask]);
    public void Validate()
    {
        if(RequestLifetime<=TimeSpan.Zero||RequestLifetime>TimeSpan.FromMinutes(10))throw new ArgumentException("Invalid request lifetime.");
        if(ExecutionTimeout<=TimeSpan.Zero||ExecutionTimeout>TimeSpan.FromMinutes(2))throw new ArgumentException("Invalid execution timeout.");
        if(AllowedProviders.Length==0||AllowedActions.Length==0)throw new ArgumentException("Control allowlist cannot be empty.");
        if(AllowedProviders.Any(p=>!Enum.IsDefined(p))||AllowedActions.Any(a=>!Enum.IsDefined(a)))throw new ArgumentException("Invalid control allowlist.");
    }
    public void ValidateRequest(AgentCommandRequest request,DateTimeOffset now)
    {
        Validate();request.Validate(now);
        if(!AllowedProviders.Contains(request.Provider))throw new InvalidOperationException("Agent provider is not allowlisted.");
        if(!AllowedActions.Contains(request.Action))throw new InvalidOperationException("Agent action is not allowlisted.");
        if(RequireApproval&&!request.RequiresApproval)throw new InvalidOperationException("Unattended agent control is disabled.");
        if(request.ExpiresAt-request.CreatedAt>RequestLifetime)throw new InvalidOperationException("Command lifetime exceeds policy.");
    }
}

public sealed class AgentCommandStore : IDisposable
{
    public const int MaxEvents=500;
    private readonly SqliteConnection db;private readonly object gate=new();
    public AgentCommandStore(string path)
    {
        db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,
            Cache=SqliteCacheMode.Private,DefaultTimeout=1,Pooling=false}.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-512; "+
            "CREATE TABLE IF NOT EXISTS agent_command (request_id TEXT PRIMARY KEY, provider INTEGER NOT NULL, action INTEGER NOT NULL, target TEXT NOT NULL, idempotency_key TEXT NOT NULL UNIQUE, requires_approval INTEGER NOT NULL, created_ms INTEGER NOT NULL, expires_ms INTEGER NOT NULL, state INTEGER NOT NULL, updated_ms INTEGER NOT NULL, result_code TEXT NOT NULL); "+
            "CREATE TABLE IF NOT EXISTS agent_command_event (id INTEGER PRIMARY KEY AUTOINCREMENT, request_id TEXT NOT NULL, stamp_ms INTEGER NOT NULL, from_state INTEGER NOT NULL, to_state INTEGER NOT NULL, code TEXT NOT NULL); "+
            "CREATE INDEX IF NOT EXISTS idx_agent_command_event_req ON agent_command_event(request_id,id);";cmd.ExecuteNonQuery();
    }
    public AgentCommand Create(AgentCommandRequest request,AgentCommandState initial,DateTimeOffset now)
    {
        lock(gate)
        {
            using var tx=db.BeginTransaction();var byId=FindByRequestId(request.RequestId,tx); if(byId!=null){if(byId.Provider!=request.Provider||byId.Action!=request.Action||byId.Target!=request.Target||byId.IdempotencyKey!=request.IdempotencyKey)throw new InvalidOperationException("Request id collision.");tx.Commit();return byId;} var prior=FindByIdempotency(request.IdempotencyKey,tx);
            if(prior!=null)
            {
                if(prior.Provider!=request.Provider||prior.Action!=request.Action||prior.Target!=request.Target)
                    throw new InvalidOperationException("Idempotency key collision.");
                tx.Commit();return prior;
            }
            using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO agent_command VALUES($id,$p,$a,$t,$k,$r,$c,$e,$s,$u,'')";
            cmd.Parameters.AddWithValue("$id",request.RequestId);cmd.Parameters.AddWithValue("$p",(int)request.Provider);cmd.Parameters.AddWithValue("$a",(int)request.Action);
            cmd.Parameters.AddWithValue("$t",request.Target);cmd.Parameters.AddWithValue("$k",request.IdempotencyKey);cmd.Parameters.AddWithValue("$r",request.RequiresApproval?1:0);
            cmd.Parameters.AddWithValue("$c",request.CreatedAt.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$e",request.ExpiresAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$s",(int)initial);cmd.Parameters.AddWithValue("$u",now.ToUnixTimeMilliseconds());cmd.ExecuteNonQuery();
            AddEvent(tx,request.RequestId,initial,initial,"REQUESTED",now);tx.Commit();
            return new(request.RequestId,request.Provider,request.Action,request.Target,request.IdempotencyKey,request.RequiresApproval,request.CreatedAt,request.ExpiresAt,initial,now);
        }
    }
    public AgentCommand? Get(string requestId)
    {
        lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT request_id,provider,action,target,idempotency_key,requires_approval,created_ms,expires_ms,state,updated_ms,result_code FROM agent_command WHERE request_id=$id";cmd.Parameters.AddWithValue("$id",requestId);using var r=cmd.ExecuteReader();return r.Read()?ReadCommand(r):null;}
    }
    private AgentCommand? FindByIdempotency(string key,SqliteTransaction tx)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT request_id,provider,action,target,idempotency_key,requires_approval,created_ms,expires_ms,state,updated_ms,result_code FROM agent_command WHERE idempotency_key=$k";cmd.Parameters.AddWithValue("$k",key);using var r=cmd.ExecuteReader();return r.Read()?ReadCommand(r):null;
    }
    private static AgentCommand ReadCommand(SqliteDataReader r)=>new(r.GetString(0),(AgentProviderKind)r.GetInt32(1),(AgentCommandAction)r.GetInt32(2),r.GetString(3),r.GetString(4),r.GetInt32(5)!=0,
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7)),(AgentCommandState)r.GetInt32(8),DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)),r.GetString(10));

    public AgentCommand Transition(string requestId,AgentCommandState expected,AgentCommandState next,string code,DateTimeOffset at,string resultCode="")
    {
        if(!Regex.IsMatch(code,"^[A-Z0-9_]{2,32}$"))throw new ArgumentException("Invalid audit code.");
        if(resultCode.Length>0&&!Regex.IsMatch(resultCode,"^[A-Z0-9_]{2,32}$"))throw new ArgumentException("Invalid result code.");
        lock(gate)
        {
            using var tx=db.BeginTransaction();var current=FindByRequestId(requestId,tx)??throw new KeyNotFoundException("Unknown agent command.");
            if(current.State!=expected)throw new InvalidOperationException($"Invalid agent command transition {current.State} -> {next}.");
            using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE agent_command SET state=$s,updated_ms=$u,result_code=$r WHERE request_id=$id";
            cmd.Parameters.AddWithValue("$s",(int)next);cmd.Parameters.AddWithValue("$u",at.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$r",resultCode);cmd.Parameters.AddWithValue("$id",requestId);cmd.ExecuteNonQuery();
            AddEvent(tx,requestId,expected,next,code,at);tx.Commit();return current with{State=next,UpdatedAt=at,ResultCode=resultCode};
        }
    }
    private AgentCommand? FindByRequestId(string requestId,SqliteTransaction tx)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT request_id,provider,action,target,idempotency_key,requires_approval,created_ms,expires_ms,state,updated_ms,result_code FROM agent_command WHERE request_id=$id";cmd.Parameters.AddWithValue("$id",requestId);using var r=cmd.ExecuteReader();return r.Read()?ReadCommand(r):null;
    }
    public int ExpireDue(DateTimeOffset now)
    {
        lock(gate)
        {
            using var tx=db.BeginTransaction();using var select=db.CreateCommand();select.Transaction=tx;
            select.CommandText="SELECT request_id,state FROM agent_command WHERE expires_ms < $n AND state IN (0,1)";select.Parameters.AddWithValue("$n",now.ToUnixTimeMilliseconds());
            var due=new List<(string Id,AgentCommandState State)>();using(var r=select.ExecuteReader())while(r.Read())due.Add((r.GetString(0),(AgentCommandState)r.GetInt32(1)));
            foreach(var item in due)
            {
                using var update=db.CreateCommand();update.Transaction=tx;update.CommandText="UPDATE agent_command SET state=$s,updated_ms=$u,result_code='EXPIRED' WHERE request_id=$id";
                update.Parameters.AddWithValue("$s",(int)AgentCommandState.Expired);update.Parameters.AddWithValue("$u",now.ToUnixTimeMilliseconds());update.Parameters.AddWithValue("$id",item.Id);update.ExecuteNonQuery();
                AddEvent(tx,item.Id,item.State,AgentCommandState.Expired,"EXPIRED",now);
            }
            tx.Commit();return due.Count;
        }
    }
    public AgentCommand[] ReadRecent(int limit=100)
    {
        if(limit<1||limit>500)throw new ArgumentOutOfRangeException(nameof(limit));lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT request_id,provider,action,target,idempotency_key,requires_approval,created_ms,expires_ms,state,updated_ms,result_code FROM agent_command ORDER BY updated_ms DESC LIMIT $n";cmd.Parameters.AddWithValue("$n",limit);using var r=cmd.ExecuteReader();var rows=new List<AgentCommand>();while(r.Read())rows.Add(ReadCommand(r));return rows.ToArray();}
    }
    public AgentCommandEvent[] ReadEvents(string requestId,int limit=100)
    {
        if(limit<1||limit>MaxEvents)throw new ArgumentOutOfRangeException(nameof(limit));lock(gate){using var cmd=db.CreateCommand();cmd.CommandText="SELECT stamp_ms,request_id,from_state,to_state,code FROM agent_command_event WHERE request_id=$id ORDER BY id LIMIT $n";cmd.Parameters.AddWithValue("$id",requestId);cmd.Parameters.AddWithValue("$n",limit);using var r=cmd.ExecuteReader();var rows=new List<AgentCommandEvent>();while(r.Read())rows.Add(new(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)),r.GetString(1),(AgentCommandState)r.GetInt32(2),(AgentCommandState)r.GetInt32(3),r.GetString(4)));return rows.ToArray();}
    }
    private static void AddEvent(SqliteTransaction tx,string requestId,AgentCommandState from,AgentCommandState to,string code,DateTimeOffset at)
    {
        using var cmd=tx.Connection!.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO agent_command_event(request_id,stamp_ms,from_state,to_state,code) VALUES($id,$t,$f,$n,$c)";
        cmd.Parameters.AddWithValue("$id",requestId);cmd.Parameters.AddWithValue("$t",at.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$f",(int)from);cmd.Parameters.AddWithValue("$n",(int)to);cmd.Parameters.AddWithValue("$c",code);cmd.ExecuteNonQuery();
    }
    public void Dispose()=>db.Dispose();
}

public sealed class AgentControlPlane
{
    private readonly AgentCommandStore store;private readonly AgentControlPolicy policy;private readonly Dictionary<AgentProviderKind,IAgentControlProvider> providers;
    public AgentControlPlane(AgentCommandStore store,IEnumerable<IAgentControlProvider>? providers=null,AgentControlPolicy? policy=null)
    {
        this.store=store;this.policy=policy??AgentControlPolicy.SafeDefault;this.policy.Validate();
        this.providers=(providers??[]).ToDictionary(x=>x.Kind);
    }
    public AgentCommand Request(AgentCommandRequest request,DateTimeOffset? now=null)
    {
        var at=now??DateTimeOffset.UtcNow;policy.ValidateRequest(request,at);
        var initial=request.RequiresApproval?AgentCommandState.Requested:AgentCommandState.Approved;
        return store.Create(request,initial,at);
    }
    public AgentCommand Approve(string requestId,DateTimeOffset? now=null)
    {
        var at=now??DateTimeOffset.UtcNow;var current=RequireCurrent(requestId,at);
        if(current.State is AgentCommandState.Approved or AgentCommandState.Executing or AgentCommandState.Succeeded or AgentCommandState.Failed)return current;
        if(current.State!=AgentCommandState.Requested)throw new InvalidOperationException("Agent command cannot be approved from its current state.");
        return store.Transition(requestId,AgentCommandState.Requested,AgentCommandState.Approved,"APPROVED",at);
    }
    public AgentCommand Reject(string requestId,DateTimeOffset? now=null)
    {
        var at=now??DateTimeOffset.UtcNow;var current=RequireCurrent(requestId,at);
        if(current.State==AgentCommandState.Rejected)return current;
        if(current.State!=AgentCommandState.Requested)throw new InvalidOperationException("Agent command cannot be rejected from its current state.");
        return store.Transition(requestId,AgentCommandState.Requested,AgentCommandState.Rejected,"REJECTED",at,"REJECTED");
    }
    public async Task<AgentCommand> ExecuteAsync(string requestId,CancellationToken ct=default)
    {
        var now=DateTimeOffset.UtcNow;var current=RequireCurrent(requestId,now);
        if(current.State is AgentCommandState.Succeeded or AgentCommandState.Failed)return current;
        if(current.State!=AgentCommandState.Approved)throw new InvalidOperationException("Agent command requires approval before execution.");
        if(!providers.TryGetValue(current.Provider,out var provider))throw new InvalidOperationException("No control provider is registered for this agent.");
        var needed=CapabilityFor(current.Action);if((provider.Capabilities&needed)!=needed)throw new InvalidOperationException("Provider does not allow this action.");
        current=store.Transition(requestId,AgentCommandState.Approved,AgentCommandState.Executing,"EXECUTING",now);
        using var timeout=new CancellationTokenSource(policy.ExecutionTimeout);using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,timeout.Token);
        try
        {
            var outcome=await provider.ExecuteAsync(current,linked.Token);outcome.Validate();var done=DateTimeOffset.UtcNow;
            return store.Transition(requestId,AgentCommandState.Executing,outcome.Success?AgentCommandState.Succeeded:AgentCommandState.Failed,
                outcome.Success?"SUCCEEDED":"FAILED",done,outcome.Code);
        }
        catch(OperationCanceledException)
        {
            var code=timeout.IsCancellationRequested&&!ct.IsCancellationRequested?"TIMEOUT":"CANCELLED";
            return store.Transition(requestId,AgentCommandState.Executing,AgentCommandState.Failed,"FAILED",DateTimeOffset.UtcNow,code);
        }
        catch(Exception e)when(e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return store.Transition(requestId,AgentCommandState.Executing,AgentCommandState.Failed,"FAILED",DateTimeOffset.UtcNow,"PROVIDER_ERROR");
        }
    }
    private AgentCommand RequireCurrent(string requestId,DateTimeOffset now)
    {
        store.ExpireDue(now);var current=store.Get(requestId)??throw new KeyNotFoundException("Unknown agent command.");
        if(current.State==AgentCommandState.Expired)throw new InvalidOperationException("Agent command expired.");
        return current;
    }
    private static AgentCapability CapabilityFor(AgentCommandAction action)=>action switch
    {
        AgentCommandAction.SubmitTask=>AgentCapability.SubmitTask,
        AgentCommandAction.StopTask=>AgentCapability.StopTask,
        AgentCommandAction.ResumeTask=>AgentCapability.ResumeTask,
        AgentCommandAction.RetryTask=>AgentCapability.RetryTask,
        _=>AgentCapability.None
    };
}
