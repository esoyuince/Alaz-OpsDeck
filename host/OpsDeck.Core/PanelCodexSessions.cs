using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public abstract record PanelCodexInbound(int RequestId);
public sealed record PanelCodexChatRequest(int RequestId,string Key,int Page):PanelCodexInbound(RequestId);
public sealed record PanelCodexContinue(int RequestId,string Key,string Prompt):PanelCodexInbound(RequestId);
public sealed record PanelCodexAction(int RequestId,string Key,AgentCommandAction Action):PanelCodexInbound(RequestId);
public sealed record PanelCodexApproval(int RequestId,int ApprovalId,bool Allow):PanelCodexInbound(RequestId);

public sealed record PanelCodexChatPage(int RequestId,string Key,string Repo,int Page,int TotalPages,
    bool Running,string Activity,CodexChatMessage[] Messages,string Result="OK")
{
    public const int PageSize=3,MaxMessageBytes=640;
    public string Wire()=>JsonSerializer.Serialize(new{type="opsdeck.codex_chat.v1",request_id=RequestId,key=Key,repo=Repo,
        page=Page,total_pages=TotalPages,running=Running,activity=Activity,result=Result,
        messages=Messages.Select(m=>new{role=(int)m.Role,text=BoundUtf8(m.Text,MaxMessageBytes)})},Json.Options);
    public static PanelCodexChatPage Build(int requestId,CodexConversation conversation,int requestedPage)
    {
        int count=conversation.Messages.Length,total=Math.Max(1,(count+PageSize-1)/PageSize),page=Math.Clamp(requestedPage,0,total-1);
        int end=Math.Max(0,count-page*PageSize),start=Math.Max(0,end-PageSize);
        return new(requestId,conversation.Key,conversation.Repo,page,total,conversation.Running,conversation.Activity,
            conversation.Messages.Skip(start).Take(end-start).ToArray());
    }
    private static string BoundUtf8(string value,int maxBytes)
    {
        if(Encoding.UTF8.GetByteCount(value)<=maxBytes)return value;var b=new StringBuilder();int used=0;
        foreach(var rune in value.EnumerateRunes()){int n=rune.Utf8SequenceLength;if(used+n>maxBytes-3)break;b.Append(rune);used+=n;}return b+"...";
    }
}

public sealed class PanelCodexPageCache
{
    public const int MaxEntries=32;
    private sealed record Entry(string Key,int RequestedPage,PanelCodexChatPage Page,DateTimeOffset At);
    private readonly object gate=new();private readonly Dictionary<string,Entry> entries=[];
    private static string Id(string key,int page)=>key+":"+page;
    public bool TryGet(string key,int page,out PanelCodexChatPage? value,out DateTimeOffset at)
    {
        lock(gate){if(entries.TryGetValue(Id(key,page),out var e)){value=e.Page;at=e.At;return true;}}
        value=null;at=default;return false;
    }
    public void Put(string key,int requestedPage,PanelCodexChatPage page,DateTimeOffset at)
    {
        lock(gate){entries[Id(key,requestedPage)]=new(key,requestedPage,page,at);while(entries.Count>MaxEntries){var oldest=entries.MinBy(x=>x.Value.At);entries.Remove(oldest.Key);}}
    }
    public void Invalidate(string key){lock(gate){foreach(var id in entries.Where(x=>x.Value.Key==key).Select(x=>x.Key).ToArray())entries.Remove(id);}}
    public void Clear(){lock(gate)entries.Clear();}
    public int Count{get{lock(gate)return entries.Count;}}
}

public sealed class PanelCodexContinueAssembler
{
    public const int MaxPromptBytes=2048,ChunkBytes=48,MaxChunks=(MaxPromptBytes+ChunkBytes-1)/ChunkBytes;
    private sealed record Draft(int RequestId,string Key,int Bytes,int Chunks,DateTimeOffset StartedAt,byte[][] Data,bool[] Seen);
    private Draft? draft;private static readonly UTF8Encoding StrictUtf8=new(false,true);
    public bool TryAccept(string line,DateTimeOffset now,out PanelCodexInbound? inbound)
    {
        inbound=null;if(line.Length>320)return false;string body=Body(line);if(body.Length==0)return false;
        var chat=Regex.Match(body,@"^CODEX_CHAT_REQUEST request=([1-9][0-9]{0,9}) key=([a-f0-9]{16}) page=([0-9]{1,3})$",RegexOptions.CultureInvariant);
        if(chat.Success){inbound=new PanelCodexChatRequest(int.Parse(chat.Groups[1].Value),chat.Groups[2].Value,int.Parse(chat.Groups[3].Value));return true;}
        var action=Regex.Match(body,@"^CODEX_SESSION_ACTION request=([1-9][0-9]{0,9}) key=([a-f0-9]{16}) action=(stop|resume|retry)$",RegexOptions.CultureInvariant);
        if(action.Success){var a=action.Groups[3].Value switch{"stop"=>AgentCommandAction.StopTask,"resume"=>AgentCommandAction.ResumeTask,_=>AgentCommandAction.RetryTask};inbound=new PanelCodexAction(int.Parse(action.Groups[1].Value),action.Groups[2].Value,a);return true;}
        var approval=Regex.Match(body,@"^CODEX_APPROVAL request=([1-9][0-9]{0,9}) approval=([1-9][0-9]{0,9}) decision=(allow|deny)$",RegexOptions.CultureInvariant);
        if(approval.Success){inbound=new PanelCodexApproval(int.Parse(approval.Groups[1].Value),int.Parse(approval.Groups[2].Value),approval.Groups[3].Value=="allow");return true;}
        if(TryBegin(body,now))return true;if(TryChunk(body))return true;if(TryCommit(body,now,out var cont)){inbound=cont;return true;}return false;
    }
    private static string Body(string line){int i=line.IndexOf("opsdeck.ui: ",StringComparison.Ordinal);return i<0?"":line[(i+12)..].Trim();}
    private bool TryBegin(string body,DateTimeOffset now)
    {
        var m=Regex.Match(body,@"^CODEX_CONTINUE_BEGIN request=([1-9][0-9]{0,9}) key=([a-f0-9]{16}) bytes=([1-9][0-9]{0,3}) chunks=([1-9][0-9]?)$",RegexOptions.CultureInvariant);
        if(!m.Success)return false;int id=int.Parse(m.Groups[1].Value),bytes=int.Parse(m.Groups[3].Value),chunks=int.Parse(m.Groups[4].Value),expected=(bytes+ChunkBytes-1)/ChunkBytes;
        if(bytes>MaxPromptBytes||chunks>MaxChunks||chunks!=expected){draft=null;return true;}
        draft=new(id,m.Groups[2].Value,bytes,chunks,now,new byte[chunks][],new bool[chunks]);return true;
    }
    private bool TryChunk(string body)
    {
        var m=Regex.Match(body,@"^CODEX_CONTINUE_CHUNK request=([1-9][0-9]{0,9}) index=([0-9]{1,2}) data=([0-9A-F]{2,96})$",RegexOptions.CultureInvariant);
        if(!m.Success)return false;var d=draft;if(d==null)return true;
        if(!int.TryParse(m.Groups[1].Value,out int id)||!int.TryParse(m.Groups[2].Value,out int index)||id!=d.RequestId||index<0||index>=d.Chunks)return true;
        string hex=m.Groups[3].Value;if((hex.Length&1)!=0)return true;int expected=index==d.Chunks-1?d.Bytes-index*ChunkBytes:ChunkBytes;if(hex.Length/2!=expected)return true;
        if(d.Seen[index]){draft=null;return true;}
        try{d.Data[index]=Convert.FromHexString(hex);d.Seen[index]=true;}catch(FormatException){draft=null;}return true;
    }
    private bool TryCommit(string body,DateTimeOffset now,out PanelCodexContinue? continuation)
    {
        continuation=null;var m=Regex.Match(body,@"^CODEX_CONTINUE_COMMIT request=([1-9][0-9]{0,9})$",RegexOptions.CultureInvariant);if(!m.Success)return false;
        var d=draft;draft=null;if(d==null||!int.TryParse(m.Groups[1].Value,out int id)||id!=d.RequestId||d.Seen.Any(x=>!x)||now-d.StartedAt>TimeSpan.FromSeconds(30))return true;
        byte[] bytes=new byte[d.Bytes];int p=0;foreach(var part in d.Data){if(part==null)return true;Buffer.BlockCopy(part,0,bytes,p,part.Length);p+=part.Length;}
        try{string prompt=StrictUtf8.GetString(bytes);if(string.IsNullOrWhiteSpace(prompt)||prompt.IndexOf('\0')>=0)return true;continuation=new(d.RequestId,d.Key,prompt);}catch(DecoderFallbackException){}return true;
    }
}

public sealed partial class AppEngine
{
    private readonly PanelCodexContinueAssembler panelCodexAssembler=new();
    private readonly ConcurrentQueue<PanelCodexInbound> panelCodexQueue=new();
    private readonly ConcurrentQueue<(string Key,string Target,int Page)> panelCodexRefreshQueue=new();
    private readonly ConcurrentDictionary<string,byte> panelCodexRefreshQueued=new();
    private readonly ConcurrentDictionary<string,PanelCodexChatRequest> panelCodexLatestRequests=new();
    private readonly PanelCodexPageCache panelCodexCache=new();
    private readonly object panelCodexGate=new();
    private PanelCodexChatPage? panelCodexChat;private DateTimeOffset panelCodexChatRequestedAt;
    private static string PanelCodexCacheId(string key,int page)=>key+":"+page;
    private static TimeSpan PanelCodexCacheTtl(PanelCodexChatPage page)=>page.Running?TimeSpan.FromSeconds(2):TimeSpan.FromSeconds(30);
    private void StartPanelCodexSessions(){tasks.Add(Task.Run(PanelCodexSessionLoop));tasks.Add(Task.Run(PanelCodexRefreshLoop));}
    private void ObservePanelCodexLine(string line)
    {
        if(panelCodexAssembler.TryAccept(line,DateTimeOffset.UtcNow,out var inbound)&&inbound!=null)panelCodexQueue.Enqueue(inbound);
    }
    private async Task PanelCodexSessionLoop()
    {
        while(!stop.IsCancellationRequested)
        {
            while(panelCodexQueue.TryDequeue(out var inbound))await HandlePanelCodexInbound(inbound);
            await Task.Delay(50,stop.Token);
        }
    }
    private async Task PanelCodexRefreshLoop()
    {
        DateTimeOffset nextPrefetch=DateTimeOffset.MinValue;
        while(!stop.IsCancellationRequested)
        {
            if(panelCodexRefreshQueue.TryDequeue(out var item))
            {
                string id=PanelCodexCacheId(item.Key,item.Page);try{await RefreshPanelCodexPage(item.Key,item.Target,item.Page);}finally{panelCodexRefreshQueued.TryRemove(id,out _);}continue;
            }
            var now=DateTimeOffset.UtcNow;if(now>=nextPrefetch)
            {
                var runtime=Volatile.Read(ref managedCodexRuntime);if(runtime!=null&&!Locked)
                    foreach(var row in runtime.ReadOwnedSessions(2)){if(!panelCodexCache.TryGet(row.Key,0,out var p,out var at)||p==null||now-at>PanelCodexCacheTtl(p))QueuePanelCodexRefresh(row.Key,row.Target,0);}
                nextPrefetch=now.AddSeconds(10);
            }
            await Task.Delay(100,stop.Token);
        }
    }
    private void QueuePanelCodexRefresh(string key,string target,int page)
    {
        string id=PanelCodexCacheId(key,page);if(panelCodexRefreshQueued.TryAdd(id,0))panelCodexRefreshQueue.Enqueue((key,target,page));
    }
    private async Task RefreshPanelCodexPage(string key,string target,int requestedPage)
    {
        var runtime=Volatile.Read(ref managedCodexRuntime);if(runtime==null)return;using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(20));var watch=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var conversation=await runtime.ReadConversationAsync(target,timeout.Token);var fresh=PanelCodexChatPage.Build(1,conversation,requestedPage);var now=DateTimeOffset.UtcNow;panelCodexCache.Put(key,requestedPage,fresh,now);
            string id=PanelCodexCacheId(key,requestedPage);if(panelCodexLatestRequests.TryGetValue(id,out var latest))PublishPanelCodexChat(fresh with{RequestId=latest.RequestId});
            log.Event("panel_codex_cache_refresh",new{page=requestedPage,messages=fresh.Messages.Length,elapsed_ms=watch.ElapsedMilliseconds,running=fresh.Running});
        }
        catch(OperationCanceledException)when(!stop.IsCancellationRequested){}
        catch(Exception e)when(e is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException or UnauthorizedAccessException or InvalidDataException){}
    }
    private void PublishPanelCodexChat(PanelCodexChatPage page){lock(panelCodexGate){panelCodexChat=page;panelCodexChatRequestedAt=DateTimeOffset.UtcNow;}}
    private async Task HandlePanelCodexInbound(PanelCodexInbound inbound)
    {
        if(Locked)return;try
        {
            switch(inbound){case PanelCodexChatRequest q:await LoadPanelCodexChat(q);break;case PanelCodexContinue c:await ContinuePanelCodex(c);break;case PanelCodexAction a:await ActOnPanelCodex(a);break;case PanelCodexApproval a:await RespondPanelCodexApproval(a);break;}
        }
        catch(Exception e)when(e is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException or UnauthorizedAccessException or InvalidDataException)
        {RecordOperational(new(DateTimeOffset.UtcNow,OperationalSeverity.Warning,OperationalDomain.Agent,"PANEL_CODEX_BLOCKED","Deck Codex session action blocked safely"));}
    }
    private Task LoadPanelCodexChat(PanelCodexChatRequest request)
    {
        var runtime=Volatile.Read(ref managedCodexRuntime)??throw new InvalidOperationException("Managed Codex runtime unavailable.");string target=runtime.ResolveOwnedTarget(request.Key),id=PanelCodexCacheId(request.Key,request.Page);var now=DateTimeOffset.UtcNow;
        panelCodexLatestRequests[id]=request;
        if(panelCodexCache.TryGet(request.Key,request.Page,out var cached,out var at)&&cached!=null)
        {
            PublishPanelCodexChat(cached with{RequestId=request.RequestId});if(now-at>PanelCodexCacheTtl(cached))QueuePanelCodexRefresh(request.Key,target,request.Page);
        }
        else QueuePanelCodexRefresh(request.Key,target,request.Page);
        return Task.CompletedTask;
    }
    private async Task ContinuePanelCodex(PanelCodexContinue continuation)
    {
        var runtime=Volatile.Read(ref managedCodexRuntime)??throw new InvalidOperationException("Managed Codex runtime unavailable.");string target=runtime.ResolveOwnedTarget(continuation.Key);panelCodexCache.Invalidate(continuation.Key);
        var command=RequestManagedCodexContinue(target,continuation.Prompt);ApproveAgentCommand(command.RequestId);var result=await ExecuteAgentCommandAsync(command.RequestId,stop.Token);QueuePanelCodexRefresh(continuation.Key,target,0);
        RecordOperational(new(DateTimeOffset.UtcNow,result.State==AgentCommandState.Succeeded?OperationalSeverity.Info:OperationalSeverity.Warning,
            OperationalDomain.Agent,"PANEL_CODEX_CONTINUE",$"Deck Codex continuation: {result.State} / {result.ResultCode}"));
    }
    private async Task ActOnPanelCodex(PanelCodexAction action)
    {
        var runtime=Volatile.Read(ref managedCodexRuntime)??throw new InvalidOperationException("Managed Codex runtime unavailable.");string target=runtime.ResolveOwnedTarget(action.Key);panelCodexCache.Invalidate(action.Key);
        var command=RequestManagedCodexAction(target,action.Action);ApproveAgentCommand(command.RequestId);var result=await ExecuteAgentCommandAsync(command.RequestId,stop.Token);QueuePanelCodexRefresh(action.Key,target,0);
        RecordOperational(new(DateTimeOffset.UtcNow,result.State==AgentCommandState.Succeeded?OperationalSeverity.Info:OperationalSeverity.Warning,
            OperationalDomain.Agent,"PANEL_CODEX_ACTION",$"Deck Codex session action: {result.State} / {result.ResultCode}"));
    }
    private async Task RespondPanelCodexApproval(PanelCodexApproval approval)
    {
        var runtime=Volatile.Read(ref managedCodexRuntime)??throw new InvalidOperationException("Managed Codex runtime unavailable.");var pending=runtime.PendingApproval;
        await runtime.RespondApprovalAsync(approval.ApprovalId,approval.Allow,stop.Token);
        if(pending!=null){panelCodexCache.Invalidate(pending.Key);try{QueuePanelCodexRefresh(pending.Key,runtime.ResolveOwnedTarget(pending.Key),0);}catch(KeyNotFoundException){}}else panelCodexCache.Clear();
        RecordOperational(new(DateTimeOffset.UtcNow,OperationalSeverity.Info,OperationalDomain.Agent,"PANEL_CODEX_APPROVAL",approval.Allow?"Deck allowed Codex approval request":"Deck declined Codex approval request"));
    }
    private string PanelCodexSessionsFrame()
    {
        var runtime=Volatile.Read(ref managedCodexRuntime);var now=DateTimeOffset.UtcNow;
        var rows=runtime?.ReadOwnedSessions(16)??[];
        var safe=Locked?Array.Empty<object>():rows.Select(s=>(object)new{key=s.Key,repo=s.Repo,activity=s.Activity,
            running=s.Running,write=s.Sandbox==ManagedCodexSandbox.WorkspaceWrite,
            age_s=(int)Math.Clamp((now-s.UpdatedAt).TotalSeconds,0,604800)}).ToArray();
        var a=Locked?null:runtime?.PendingApproval;object? approval=a==null?null:new{id=a.Id,key=a.Key,kind=a.Kind,summary=InventoryPaging.Label(a.Summary,96),age_s=(int)Math.Clamp((now-a.RequestedAt).TotalSeconds,0,3600)};
        return JsonSerializer.Serialize(new{type="opsdeck.codex_sessions.v1",locked=Locked,sessions=safe,approval},Json.Options);
    }
    private string? PanelCodexChatFrame()
    {
        if(Locked)return null;
        lock(panelCodexGate)
        {
            if(panelCodexChat==null||DateTimeOffset.UtcNow-panelCodexChatRequestedAt>TimeSpan.FromSeconds(20))return null;
            return panelCodexChat.Wire();
        }
    }
}
