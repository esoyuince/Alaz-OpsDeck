using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace OpsDeck.Core;

public enum CodexChatRole { User=1, Assistant=2 }
public sealed record CodexOwnedSession(string Target,string Key,string ThreadId,string Repo,
    ManagedCodexSandbox Sandbox,bool Running,string Activity,DateTimeOffset UpdatedAt);
public sealed record CodexChatMessage(CodexChatRole Role,string Text,string TurnId,int Order);
public sealed record CodexConversation(string Target,string Key,string Repo,bool Running,string Activity,
    CodexChatMessage[] Messages,DateTimeOffset ObservedAt);
public sealed record CodexApprovalSnapshot(int Id,string Key,string Kind,string Summary,DateTimeOffset RequestedAt);
internal sealed record CodexServerApproval(int Id,JsonElement RpcId,string ThreadId,string Key,string Kind,string Summary,DateTimeOffset RequestedAt);

public sealed partial class CodexAppServerRuntime
{
    public const int MaxSessionRows=32,MaxConversationMessages=256,MaxConversationTextChars=4000,MaxPendingApprovals=8;
    private readonly object approvalGate=new();private readonly Dictionary<int,CodexServerApproval> approvals=new();private int approvalSequence;
    public CodexOwnedSession[] ReadOwnedSessions(int limit=16)
    {
        if(limit<1||limit>MaxSessionRows)throw new ArgumentOutOfRangeException(nameof(limit));
        ManagedCodexSession[] rows;lock(gate)rows=sessions.Values.OrderByDescending(x=>x.UpdatedAt).Take(limit).ToArray();
        var snap=Snapshot;return rows.Select(s=>new CodexOwnedSession(s.Target,SessionKey(s.Target),s.ThreadId,
            RepoName(s.WorkingDirectory),s.Sandbox,s.Running,SessionActivity(s,snap),s.UpdatedAt)).ToArray();
    }
    public string ResolveOwnedTarget(string key)
    {
        if(key.Length!=16||key.Any(c=>!Uri.IsHexDigit(c)))throw new ArgumentException("Invalid managed Codex session key.");
        lock(gate){foreach(var s in sessions.Values)if(SessionKey(s.Target).Equals(key,StringComparison.OrdinalIgnoreCase))return s.Target;}
        throw new KeyNotFoundException("Unknown managed Codex session key.");
    }
    public CodexApprovalSnapshot? PendingApproval
    {
        get{lock(approvalGate){var a=approvals.Values.OrderBy(x=>x.RequestedAt).FirstOrDefault();return a==null?null:new(a.Id,a.Key,a.Kind,a.Summary,a.RequestedAt);}}
    }
    public ManagedCodexPayload ContinuationPayload(string target,string prompt)
    {
        var s=Owned(target);var payload=new ManagedCodexPayload(prompt,s.WorkingDirectory,s.Sandbox);payload.Validate();return payload;
    }
    public async Task<CodexConversation> ReadConversationAsync(string target,CancellationToken ct)
    {
        var owned=Owned(target);await EnsureStarted(ct);
        var result=await Call("thread/read",new{threadId=owned.ThreadId,includeTurns=true},ct);
        if(result.ValueKind!=JsonValueKind.Object||!result.TryGetProperty("thread",out var thread)||thread.ValueKind!=JsonValueKind.Object)
            throw new InvalidDataException("Codex thread/read returned no thread.");
        var messages=ParseMessages(thread);var current=Owned(target);return new(target,SessionKey(target),RepoName(current.WorkingDirectory),
            current.Running,SessionActivity(current,Snapshot),messages,DateTimeOffset.UtcNow);
    }
    private static CodexChatMessage[] ParseMessages(JsonElement thread)
    {
        if(!thread.TryGetProperty("turns",out var turns)||turns.ValueKind!=JsonValueKind.Array)return [];
        var rows=new List<CodexChatMessage>();int order=0;
        foreach(var turn in turns.EnumerateArray())
        {
            if(rows.Count>=MaxConversationMessages)break;string turnId=Text(turn,"id");
            if(!turn.TryGetProperty("items",out var items)||items.ValueKind!=JsonValueKind.Array)continue;
            foreach(var item in items.EnumerateArray())
            {
                if(rows.Count>=MaxConversationMessages)break;string type=Text(item,"type");string value="";CodexChatRole role;
                if(type=="userMessage"){role=CodexChatRole.User;value=UserText(item);}
                else if(type=="agentMessage"){role=CodexChatRole.Assistant;value=Text(item,"text");}
                else continue;
                value=BoundText(value,MaxConversationTextChars);if(value.Length>0)rows.Add(new(role,value,turnId,order++));
            }
        }
        return rows.ToArray();
    }
    private static string UserText(JsonElement item)
    {
        if(!item.TryGetProperty("content",out var content)||content.ValueKind!=JsonValueKind.Array)return "";
        var parts=new List<string>();foreach(var input in content.EnumerateArray())if(Text(input,"type")=="text")
        {string t=Text(input,"text");if(t.Length>0)parts.Add(t);}return string.Join("\n",parts);
    }
    private static string Text(JsonElement o,string name)
        =>o.ValueKind==JsonValueKind.Object&&o.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
    private static string BoundText(string value,int max)
    {
        if(string.IsNullOrWhiteSpace(value))return "";var b=new StringBuilder(Math.Min(value.Length,max));
        foreach(char c in value){if(b.Length>=max)break;if(c=='\r')continue;if(c=='\n'||c=='\t'||!char.IsControl(c))b.Append(c);}
        string s=b.ToString().Trim();return value.Length>max?s+"...":s;
    }
    private async Task<bool> TryHandleServerRequest(JsonElement root,CancellationToken ct)
    {
        if(!root.TryGetProperty("id",out var rpcId)||!root.TryGetProperty("method",out var methodNode)||methodNode.ValueKind!=JsonValueKind.String)return false;
        string method=methodNode.GetString()??"";if(method is not ("item/commandExecution/requestApproval" or "item/fileChange/requestApproval"))return false;
        string threadId=FindString(root,"threadId"),target="";lock(gate){var pair=sessions.FirstOrDefault(x=>x.Value.ThreadId==threadId);if(pair.Key!=null)target=pair.Key;}
        if(target.Length==0){await ReplyApproval(rpcId.Clone(),false,ct);return true;}
        string kind=method.Contains("commandExecution",StringComparison.Ordinal)?"COMMAND":"FILE";
        string summary=kind=="COMMAND"?FindString(root,"command"):FindString(root,"reason");if(string.IsNullOrWhiteSpace(summary))summary=kind=="COMMAND"?"Command approval requested":"File change approval requested";
        var at=DateTimeOffset.UtcNow;int local=Interlocked.Increment(ref approvalSequence);bool stored=false;
        lock(approvalGate){if(approvals.Count<MaxPendingApprovals){approvals[local]=new(local,rpcId.Clone(),threadId,SessionKey(target),kind,BoundText(summary,160),at);stored=true;}}
        if(!stored)await ReplyApproval(rpcId.Clone(),false,ct);return true;
    }
    public async Task RespondApprovalAsync(int id,bool allow,CancellationToken ct)
    {
        CodexServerApproval item;lock(approvalGate){if(!approvals.Remove(id,out item!))throw new KeyNotFoundException("Unknown Codex approval request.");}
        await ReplyApproval(item.RpcId,allow,ct);
    }
    private async Task ReplyApproval(JsonElement rpcId,bool allow,CancellationToken ct)
    {
        await EnsureStarted(ct);var p=process??throw new InvalidOperationException("Codex runtime unavailable.");
        await WriteMessage(p,new Dictionary<string,object?>{{"id",rpcId},{"result",new{decision=allow?"accept":"decline"}}},ct);
    }
    private void ClearPendingApprovals(){lock(approvalGate)approvals.Clear();}
    private static string SessionKey(string target)
        =>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target)))[..16].ToLowerInvariant();
    private static string RepoName(string path)=>InventoryPaging.Label(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)),24);
    private static string SessionActivity(ManagedCodexSession s,ManagedCodexSnapshot snap)
        =>s.Running?"RUNNING":snap.ReconciliationRequired?"RECONCILE":string.Equals(s.Target,snap.ActiveTarget,StringComparison.Ordinal)?snap.Activity:"READY";
}
