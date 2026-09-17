using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace OpsDeck.Core;

public enum ManagedCodexSandbox { ReadOnly=0, WorkspaceWrite=1 }
public sealed record ManagedCodexPayload(string Prompt,string WorkingDirectory,ManagedCodexSandbox Sandbox=ManagedCodexSandbox.ReadOnly)
{
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(Prompt)||Prompt.Length>12000||Prompt.Any(c=>c=='\0'))throw new ArgumentException("Invalid Codex task prompt.");
        if(string.IsNullOrWhiteSpace(WorkingDirectory)||WorkingDirectory.Length>1024||!Path.IsPathFullyQualified(WorkingDirectory)||!Directory.Exists(WorkingDirectory))throw new ArgumentException("Invalid Codex working directory.");
        if(!Enum.IsDefined(Sandbox))throw new ArgumentException("Invalid Codex sandbox.");
    }
}
public sealed record ManagedCodexSnapshot(SourceState State,DateTimeOffset? ObservedAt=null,bool RuntimeRunning=false,
    int OwnedThreads=0,string ActiveTarget="",string ThreadId="",string TurnId="",string Activity="SETUP",bool ReconciliationRequired=false);

public sealed class AgentPayloadRegistry
{
    private readonly ConcurrentDictionary<string,ManagedCodexPayload> values=new(StringComparer.Ordinal);
    public void Put(string requestId,ManagedCodexPayload payload){payload.Validate();if(values.Count>=64)throw new InvalidOperationException("Too many pending managed Codex payloads.");if(!values.TryAdd(requestId,payload))throw new InvalidOperationException("Payload already registered.");}
    public bool TryTake(string requestId,out ManagedCodexPayload payload)=>values.TryRemove(requestId,out payload!);
    public void Remove(string requestId)=>values.TryRemove(requestId,out _);
    public int Prune(IEnumerable<string> keep){var allowed=keep.ToHashSet(StringComparer.Ordinal);int removed=0;foreach(var key in values.Keys)if(!allowed.Contains(key)&&values.TryRemove(key,out _))removed++;return removed;}
    public int Count=>values.Count;
}
public interface ICodexAppServer : IAsyncDisposable
{
    ManagedCodexSnapshot Snapshot{get;}
    Task<(string ThreadId,string TurnId)> SubmitAsync(string target,ManagedCodexPayload payload,CancellationToken ct);
    Task StopAsync(string target,CancellationToken ct);
    Task ResumeAsync(string target,CancellationToken ct);
    Task RetryAsync(string target,CancellationToken ct);
    Task<string> ContinueAsync(string target,string prompt,CancellationToken ct);
}

public sealed class ManagedCodexProvider(ICodexAppServer runtime,AgentPayloadRegistry payloads) : IAgentControlProvider
{
    public AgentProviderKind Kind=>AgentProviderKind.Codex;
    public AgentCapability Capabilities=>AgentCapability.SubmitTask|AgentCapability.StopTask|AgentCapability.ResumeTask|AgentCapability.RetryTask|AgentCapability.ContinueTask;
    public async Task<AgentCommandOutcome> ExecuteAsync(AgentCommand command,CancellationToken ct)
    {
        switch(command.Action)
        {
            case AgentCommandAction.SubmitTask:
                if(!payloads.TryTake(command.RequestId,out var payload))return new(false,"NO_PAYLOAD");
                await runtime.SubmitAsync(command.Target,payload,ct);return new(true,"CODEX_SUBMITTED");
            case AgentCommandAction.StopTask:
                await runtime.StopAsync(command.Target,ct);return new(true,"CODEX_STOPPED");
            case AgentCommandAction.ResumeTask:
                await runtime.ResumeAsync(command.Target,ct);return new(true,"CODEX_RESUMED");
            case AgentCommandAction.RetryTask:
                await runtime.RetryAsync(command.Target,ct);return new(true,"CODEX_RETRIED");
            case AgentCommandAction.ContinueTask:
                if(!payloads.TryTake(command.RequestId,out var continuation))return new(false,"NO_PAYLOAD");
                await runtime.ContinueAsync(command.Target,continuation.Prompt,ct);return new(true,"CODEX_CONTINUED");
            default:return new(false,"UNSUPPORTED_ACTION");
        }
    }
}

internal sealed record ManagedCodexSession(string Target,string ThreadId,string TurnId,string WorkingDirectory,ManagedCodexSandbox Sandbox,string? RetryPrompt,bool Running,DateTimeOffset UpdatedAt);
public sealed partial class CodexAppServerRuntime : ICodexAppServer
{
    private readonly HostConfig config;private readonly ManagedCodexOwnershipStore? ownershipStore;private readonly SemaphoreSlim rpcGate=new(1,1),writeGate=new(1,1);private readonly object gate=new();
    private readonly Dictionary<string,ManagedCodexSession> sessions=new(StringComparer.Ordinal);private readonly ConcurrentDictionary<int,TaskCompletionSource<JsonElement>> pending=new();
    private Process? process;private CancellationTokenSource? processStop;private Task? reader;private Task? stderrDrain;private int requestId;
    private ManagedCodexSnapshot snapshot=new(SourceState.Setup,Activity:"NOT STARTED");public ManagedCodexSnapshot Snapshot=>Volatile.Read(ref snapshot);
    public CodexAppServerRuntime(HostConfig config,string? ownershipPath=null)
    {
        this.config=config;if(!string.IsNullOrWhiteSpace(ownershipPath)){ownershipStore=new ManagedCodexOwnershipStore(ownershipPath);var owned=ownershipStore.Read();foreach(var row in owned)sessions[row.Target]=new(row.Target,row.ThreadId,row.TurnId,row.WorkingDirectory,row.Sandbox,null,false,row.UpdatedAt);if(owned.Length>0){var active=owned[0];snapshot=new(SourceState.Partial,DateTimeOffset.UtcNow,false,sessions.Count,active.Target,active.ThreadId,active.TurnId,"RECONCILE",true);}}
    }

    public async Task<(string ThreadId,string TurnId)> SubmitAsync(string target,ManagedCodexPayload payload,CancellationToken ct)
    {
        payload.Validate();ValidateTarget(target);await EnsureStarted(ct);
        lock(gate)if(sessions.ContainsKey(target))throw new InvalidOperationException("Managed Codex target already exists.");
        var start=await Call("thread/start",new{cwd=payload.WorkingDirectory,approvalPolicy="on-request",sandbox=SandboxName(payload.Sandbox),ephemeral=false},ct);
        string thread=IdFrom(start,"thread","id");if(thread.Length==0)throw new InvalidDataException("Codex thread/start returned no thread id.");
        var turn=await Call("turn/start",new{threadId=thread,input=new[]{new{type="text",text=payload.Prompt,text_elements=Array.Empty<object>()}}},ct);
        string turnId=IdFrom(turn,"turn","id");if(turnId.Length==0)throw new InvalidDataException("Codex turn/start returned no turn id.");
        ManagedCodexSession session=new(target,thread,turnId,payload.WorkingDirectory,payload.Sandbox,payload.Prompt,true,DateTimeOffset.UtcNow);lock(gate)sessions[target]=session;SaveOwnership(session);Publish(SourceState.Ok,target,thread,turnId,"RUNNING",false);
        return(thread,turnId);
    }

    public async Task StopAsync(string target,CancellationToken ct)
    {
        var s=Owned(target);if(!s.Running||s.TurnId.Length==0)return;
        await EnsureStarted(ct);bool interrupted=false;
        for(int attempt=0;attempt<2&&!interrupted;attempt++)
        {
            try{await Call("turn/interrupt",new{threadId=s.ThreadId,turnId=s.TurnId},ct);interrupted=true;}
            catch(InvalidOperationException e)when(IsNoActiveTurn(e)&&attempt==0){await Task.Delay(250,ct);}
            catch(InvalidOperationException e)when(IsNoActiveTurn(e)&&attempt==1){}
        }
        var next=s with{Running=false,UpdatedAt=DateTimeOffset.UtcNow};lock(gate)sessions[target]=next;SaveOwnership(next);Publish(SourceState.Ok,target,s.ThreadId,s.TurnId,interrupted?"INTERRUPTED":"READY",false);
    }
    public async Task ResumeAsync(string target,CancellationToken ct)
    {
        var s=Owned(target);await EnsureStarted(ct);await Call("thread/resume",new{threadId=s.ThreadId,cwd=s.WorkingDirectory,approvalPolicy="on-request",sandbox=SandboxName(s.Sandbox)},ct);var next=s with{Running=false,UpdatedAt=DateTimeOffset.UtcNow};lock(gate)sessions[target]=next;SaveOwnership(next);Publish(SourceState.Ok,target,s.ThreadId,s.TurnId,"READY",false);
    }

    public async Task RetryAsync(string target,CancellationToken ct)
    {
        var s=Owned(target);if(string.IsNullOrWhiteSpace(s.RetryPrompt))throw new InvalidOperationException("Retry payload is not retained across host restart; submit a new task.");await EnsureStarted(ct);await Call("thread/resume",new{threadId=s.ThreadId,cwd=s.WorkingDirectory,approvalPolicy="on-request",sandbox=SandboxName(s.Sandbox)},ct);
        var turn=await Call("turn/start",new{threadId=s.ThreadId,input=new[]{new{type="text",text=s.RetryPrompt,text_elements=Array.Empty<object>()}}},ct);
        string turnId=IdFrom(turn,"turn","id");if(turnId.Length==0)throw new InvalidDataException("Codex retry returned no turn id.");
        var next=s with{TurnId=turnId,Running=true,UpdatedAt=DateTimeOffset.UtcNow};lock(gate)sessions[target]=next;SaveOwnership(next);Publish(SourceState.Ok,target,s.ThreadId,turnId,"RUNNING",false);
    }

    public async Task<string> ContinueAsync(string target,string prompt,CancellationToken ct)
    {
        var s=Owned(target);if(s.Running)throw new InvalidOperationException("Managed Codex session already has a running turn.");
        new ManagedCodexPayload(prompt,s.WorkingDirectory,s.Sandbox).Validate();await EnsureStarted(ct);
        await Call("thread/resume",new{threadId=s.ThreadId,cwd=s.WorkingDirectory,approvalPolicy="on-request",sandbox=SandboxName(s.Sandbox)},ct);
        var turn=await Call("turn/start",new{threadId=s.ThreadId,input=new[]{new{type="text",text=prompt,text_elements=Array.Empty<object>()}}},ct);
        string turnId=IdFrom(turn,"turn","id");if(turnId.Length==0)throw new InvalidDataException("Codex continuation returned no turn id.");
        var next=s with{TurnId=turnId,RetryPrompt=prompt,Running=true,UpdatedAt=DateTimeOffset.UtcNow};lock(gate)sessions[target]=next;SaveOwnership(next);Publish(SourceState.Ok,target,s.ThreadId,turnId,"RUNNING",false);return turnId;
    }

    private ManagedCodexSession Owned(string target)
    {
        ValidateTarget(target);lock(gate)return sessions.TryGetValue(target,out var s)?s:throw new InvalidOperationException("Codex target is not owned by OpsDeck.");
    }
    private void SaveOwnership(ManagedCodexSession s)=>ownershipStore?.Put(new(s.Target,s.ThreadId,s.TurnId,s.WorkingDirectory,s.Sandbox,s.UpdatedAt));
    private static void ValidateTarget(string target){if(string.IsNullOrWhiteSpace(target)||target.Length>96||!target.StartsWith("managed:",StringComparison.Ordinal))throw new InvalidOperationException("Only OpsDeck-managed Codex targets are allowed.");}
    private static string SandboxName(ManagedCodexSandbox value)=>value==ManagedCodexSandbox.WorkspaceWrite?"workspace-write":"read-only";

    private async Task EnsureStarted(CancellationToken ct)
    {
        if(process is {HasExited:false})return;await rpcGate.WaitAsync(ct);
        try{if(process is {HasExited:false})return;await StartProcess(ct);}finally{rpcGate.Release();}
    }
    private async Task StartProcess(CancellationToken ct)
    {
        await StopProcess();string? exe=CodexTelemetrySampler.ResolveExecutable(config.CodexDirectory);if(exe==null){Publish(SourceState.NoData,"","","","CODEX NOT FOUND",true);throw new InvalidOperationException("Codex executable not found.");}
        var psi=CodexTelemetrySampler.AppServerStartInfo(exe);
        process=Process.Start(psi)??throw new IOException("Codex app-server did not start.");processStop=new CancellationTokenSource();reader=Task.Run(()=>ReaderLoop(process,processStop.Token));stderrDrain=Task.Run(()=>DrainStderr(process,processStop.Token));
        try
        {
            await CallCore("initialize",new{clientInfo=new{name="opsdeck-control",version="m6.8"},capabilities=new{experimentalApi=true}},ct);
            await SendNotification("initialized",ct);Publish(SourceState.Ok,"","","","READY",false);
        }
        catch{await StopProcess();Publish(SourceState.Error,"","","","START FAILED",true);throw;}
    }

    private async Task<JsonElement> Call(string method,object? parameters,CancellationToken ct)
    {await EnsureStarted(ct);return await CallCore(method,parameters,ct);}
    private async Task<JsonElement> CallCore(string method,object? parameters,CancellationToken ct)
    {
        var p=process??throw new InvalidOperationException("Codex runtime unavailable.");int id=Interlocked.Increment(ref requestId);
        var tcs=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);if(!pending.TryAdd(id,tcs))throw new InvalidOperationException("Codex RPC id collision.");
        try{await WriteMessage(p,new Dictionary<string,object?>{{"id",id},{"method",method},{"params",parameters}},ct);using var reg=ct.Register(()=>tcs.TrySetCanceled(ct));return await tcs.Task;}
        finally{pending.TryRemove(id,out _);}
    }
    private async Task SendNotification(string method,CancellationToken ct)
    {var p=process??throw new InvalidOperationException("Codex runtime unavailable.");await WriteMessage(p,new Dictionary<string,object?>{{"method",method}},ct);}
    private async Task WriteMessage(Process p,object message,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();string json=JsonSerializer.Serialize(message);if(json.Length>128*1024)throw new InvalidDataException("Codex request exceeds bound.");await writeGate.WaitAsync(ct);
        try{await p.StandardInput.WriteLineAsync(json);await p.StandardInput.FlushAsync(ct);}finally{writeGate.Release();}
    }
    private async Task ReaderLoop(Process p,CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested&&!p.HasExited)
            {
                string? line=await p.StandardOutput.ReadLineAsync(ct);if(line==null)break;if(line.Length>2*1024*1024)throw new InvalidDataException("Codex response exceeds bound.");
                using var doc=JsonDocument.Parse(line);var root=doc.RootElement;
                if(root.ValueKind!=JsonValueKind.Object)continue;
                if(await TryHandleServerRequest(root,ct))continue;
                if(root.TryGetProperty("id",out var rid)&&rid.ValueKind==JsonValueKind.Number&&rid.TryGetInt32(out int id)&&pending.TryGetValue(id,out var waiter))
                {
                    if(root.TryGetProperty("error",out var rpcError))waiter.TrySetException(new InvalidOperationException(CodexRpcError(rpcError)));
                    else if(root.TryGetProperty("result",out var result))waiter.TrySetResult(result.Clone());
                    else waiter.TrySetException(new InvalidDataException("Codex RPC response missing result."));
                    continue;
                }
                if(root.TryGetProperty("method",out var method)&&method.ValueKind==JsonValueKind.String)ObserveNotification(method.GetString()??"",root);
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch(Exception e)when(e is IOException or JsonException or InvalidDataException or InvalidOperationException){Publish(SourceState.Error,"","","","RUNTIME LOST",true);}
        finally
        {
            foreach(var item in pending.Values)item.TrySetException(new IOException("Codex app-server stopped."));
            lock(gate){foreach(var key in sessions.Keys.ToArray())sessions[key]=sessions[key] with{Running=false};}
            if(!ct.IsCancellationRequested)Publish(SourceState.Error,"","","","RECONCILE REQUIRED",true);
        }
    }

    private void ObserveNotification(string method,JsonElement root)
    {
        if(method=="turn/completed")
        {
            string turn=FindString(root,"turnId");if(turn.Length==0)turn=FindNestedId(root,"turn");
            lock(gate){var pair=sessions.FirstOrDefault(x=>x.Value.TurnId==turn);if(pair.Key!=null){var next=pair.Value with{Running=false,UpdatedAt=DateTimeOffset.UtcNow};sessions[pair.Key]=next;SaveOwnership(next);Publish(SourceState.Ok,pair.Key,pair.Value.ThreadId,turn,"COMPLETED",false);}}
        }
        else if(method=="turn/started")
        {
            string turn=FindString(root,"turnId");if(turn.Length>0)Publish(SourceState.Ok,Snapshot.ActiveTarget,Snapshot.ThreadId,turn,"RUNNING",false);
        }
    }
    private void Publish(SourceState state,string target,string thread,string turn,string activity,bool reconcile)
    {int owned;lock(gate)owned=sessions.Count;Volatile.Write(ref snapshot,new(state,DateTimeOffset.UtcNow,process is {HasExited:false},owned,target,thread,turn,activity,reconcile));}
    private static bool IsNoActiveTurn(InvalidOperationException e)
        =>e.Message.Contains("no active turn to interrupt",StringComparison.OrdinalIgnoreCase);
    private static string CodexRpcError(JsonElement error)
    {
        int? code=error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("code",out var c)&&c.TryGetInt32(out int n)?n:null;
        string message=error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("message",out var m)&&m.ValueKind==JsonValueKind.String?(m.GetString()??""):"RPC error";
        if(message.Length>240)message=message[..240];return code.HasValue?$"Codex app-server RPC error {code.Value}: {message}":$"Codex app-server RPC error: {message}";
    }
    private static string IdFrom(JsonElement result,string objectName,string idName)
    {if(result.ValueKind==JsonValueKind.Object&&result.TryGetProperty(objectName,out var o)&&o.ValueKind==JsonValueKind.Object&&o.TryGetProperty(idName,out var id)&&id.ValueKind==JsonValueKind.String)return id.GetString()??"";return FindString(result,idName);}
    private static string FindString(JsonElement root,string name)
    {
        if(root.ValueKind==JsonValueKind.Object){if(root.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String)return v.GetString()??"";foreach(var p in root.EnumerateObject()){var found=FindString(p.Value,name);if(found.Length>0)return found;}}
        else if(root.ValueKind==JsonValueKind.Array)foreach(var v in root.EnumerateArray()){var found=FindString(v,name);if(found.Length>0)return found;}return "";
    }
    private static string FindNestedId(JsonElement root,string objectName)
    {if(root.ValueKind==JsonValueKind.Object){if(root.TryGetProperty(objectName,out var o)&&o.ValueKind==JsonValueKind.Object&&o.TryGetProperty("id",out var id)&&id.ValueKind==JsonValueKind.String)return id.GetString()??"";foreach(var p in root.EnumerateObject()){var found=FindNestedId(p.Value,objectName);if(found.Length>0)return found;}}return "";}
    private static async Task DrainStderr(Process p,CancellationToken ct)
    {try{while(!ct.IsCancellationRequested&&!p.HasExited){string? line=await p.StandardError.ReadLineAsync(ct);if(line==null)break;}}catch(OperationCanceledException)when(ct.IsCancellationRequested){}catch(IOException){}}

    private async Task StopProcess()
    {
        ClearPendingApprovals();var cts=processStop;processStop=null;if(cts!=null){cts.Cancel();cts.Dispose();}var p=process;process=null;
        if(p!=null){try{p.StandardInput.Close();}catch{}try{if(!p.HasExited)p.Kill(entireProcessTree:true);}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){}p.Dispose();}
        if(reader!=null)try{await reader.WaitAsync(TimeSpan.FromSeconds(1));}catch(Exception e)when(e is TimeoutException or OperationCanceledException){}reader=null;
        if(stderrDrain!=null)try{await stderrDrain.WaitAsync(TimeSpan.FromSeconds(1));}catch(Exception e)when(e is TimeoutException or OperationCanceledException){}stderrDrain=null;
    }
    public async ValueTask DisposeAsync(){await StopProcess();rpcGate.Dispose();writeGate.Dispose();}
}
