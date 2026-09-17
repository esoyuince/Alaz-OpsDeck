using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpsDeck.Core;

public enum PanelAgentAction { None=0,Submit=1,Stop=2,Resume=3,Retry=4,Handoff=5,Continue=6 }
public enum PanelAgentPhase { Idle=0,Receiving=1,AwaitingConfirm=2,Executing=3,Succeeded=4,Failed=5,Rejected=6 }
public sealed record PanelWorkspace(int Id,string Name,string Path);
public sealed record PanelAgentHistory(PanelAgentAction Action,AgentCommandState State,string Result,int AgeSeconds);
public abstract record PanelAgentInbound(int RequestId);
public sealed record PanelAgentSubmit(int RequestId,int WorkspaceId,ManagedCodexSandbox Sandbox,string Prompt):PanelAgentInbound(RequestId);
public sealed record PanelAgentActionRequest(int RequestId,PanelAgentAction Action):PanelAgentInbound(RequestId);
public sealed record PanelAgentConfirm(int RequestId):PanelAgentInbound(RequestId);
public sealed record PanelAgentCancel(int RequestId):PanelAgentInbound(RequestId);

public static class PanelWorkspaceCatalog
{
    public const int MaxWorkspaces=32;
    public static PanelWorkspace[] Discover(string root=@"C:\Antigravity")
    {
        if(!Directory.Exists(root))return [];
        return new DirectoryInfo(root).EnumerateDirectories()
            .Where(d=>(d.Attributes&(FileAttributes.Hidden|FileAttributes.System|FileAttributes.ReparsePoint))==0&&LooksLikeWorkspace(d))
            .OrderByDescending(d=>d.Name.Equals("OpsDeck",StringComparison.OrdinalIgnoreCase)?DateTime.MaxValue:d.LastWriteTimeUtc)
            .ThenBy(d=>d.Name,StringComparer.OrdinalIgnoreCase).Take(MaxWorkspaces)
            .Select((d,i)=>new PanelWorkspace(i,InventoryPaging.Label(d.Name,24),d.FullName)).ToArray();
    }
    private static bool LooksLikeWorkspace(DirectoryInfo d)
    {
        string n=d.Name.ToLowerInvariant();string[] noise=["backup","archive","worktree","release","staging","evidence","keys","artifact","custody","reconciliation","temp"];
        if(n.StartsWith('_')||noise.Any(n.Contains))return false;
        string p=d.FullName;return Directory.Exists(Path.Combine(p,".git"))||File.Exists(Path.Combine(p,"AGENTS.md"))||File.Exists(Path.Combine(p,"README.md"))||File.Exists(Path.Combine(p,"package.json"))||File.Exists(Path.Combine(p,"pubspec.yaml"))||File.Exists(Path.Combine(p,"Cargo.toml"))||Directory.Exists(Path.Combine(p,"engine-rust"))||Directory.Exists(Path.Combine(p,"src"))||Directory.Exists(Path.Combine(p,"host"))||Directory.Exists(Path.Combine(p,"firmware"))||Directory.Exists(Path.Combine(p,"lib"));
    }
}
public sealed class PanelAgentAssembler
{
    public const int MaxPromptBytes=2048,ChunkBytes=48,MaxChunks=(MaxPromptBytes+ChunkBytes-1)/ChunkBytes;
    private sealed record Draft(int RequestId,int Bytes,int Chunks,int WorkspaceId,ManagedCodexSandbox Sandbox,
        DateTimeOffset StartedAt,byte[][] Data,bool[] Seen);
    private Draft? draft;
    private static readonly UTF8Encoding StrictUtf8=new(false,true);

    public bool TryAccept(string line,DateTimeOffset now,out PanelAgentInbound? inbound)
    {
        inbound=null;if(line.Length>320)return false;
        string body=Body(line);if(body.Length==0)return false;
        if(TryBegin(body,now))return true;
        if(TryChunk(body))return true;
        if(TryCommit(body,now,out var submit)){inbound=submit;return true;}
        if(TrySimple(body,"AGENT_CONFIRM",id=>new PanelAgentConfirm(id),out inbound))return true;
        if(TrySimple(body,"AGENT_CANCEL",id=>new PanelAgentCancel(id),out inbound))return true;
        var m=Regex.Match(body,@"^AGENT_ACTION request=([1-9][0-9]{0,9}) action=(stop|resume|retry|handoff)$",RegexOptions.CultureInvariant);
        if(m.Success&&int.TryParse(m.Groups[1].Value,out int id)){
            var action=m.Groups[2].Value switch{"stop"=>PanelAgentAction.Stop,"resume"=>PanelAgentAction.Resume,"retry"=>PanelAgentAction.Retry,_=>PanelAgentAction.Handoff};
            inbound=new PanelAgentActionRequest(id,action);return true;
        }
        return false;
    }
    private static string Body(string line)
    {
        int i=line.IndexOf("opsdeck.ui: ",StringComparison.Ordinal);return i<0?"":line[(i+12)..].Trim();
    }
    private bool TryBegin(string body,DateTimeOffset now)
    {
        var m=Regex.Match(body,@"^AGENT_PROMPT_BEGIN request=([1-9][0-9]{0,9}) bytes=([1-9][0-9]{0,3}) chunks=([1-9][0-9]?) repo=([0-9]{1,2}) sandbox=([01])$",RegexOptions.CultureInvariant);
        if(!m.Success)return false;
        int id=int.Parse(m.Groups[1].Value),bytes=int.Parse(m.Groups[2].Value),chunks=int.Parse(m.Groups[3].Value),repo=int.Parse(m.Groups[4].Value),sandbox=int.Parse(m.Groups[5].Value);
        int expected=(bytes+ChunkBytes-1)/ChunkBytes;
        if(bytes>MaxPromptBytes||chunks>MaxChunks||chunks!=expected||repo>=PanelWorkspaceCatalog.MaxWorkspaces){draft=null;return true;}
        draft=new Draft(id,bytes,chunks,repo,(ManagedCodexSandbox)sandbox,now,new byte[chunks][],new bool[chunks]);return true;
    }
    private bool TryChunk(string body)
    {
        var m=Regex.Match(body,@"^AGENT_PROMPT_CHUNK request=([1-9][0-9]{0,9}) index=([0-9]{1,2}) data=([0-9A-F]{2,96})$",RegexOptions.CultureInvariant);
        if(!m.Success)return false;var d=draft;if(d==null)return true;
        if(!int.TryParse(m.Groups[1].Value,out int id)||!int.TryParse(m.Groups[2].Value,out int index)||id!=d.RequestId||index<0||index>=d.Chunks)return true;
        string hex=m.Groups[3].Value;if((hex.Length&1)!=0)return true;int expected=index==d.Chunks-1?d.Bytes-index*ChunkBytes:ChunkBytes;if(hex.Length/2!=expected)return true;
        try{d.Data[index]=Convert.FromHexString(hex);d.Seen[index]=true;}catch(FormatException){}return true;
    }
    private bool TryCommit(string body,DateTimeOffset now,out PanelAgentSubmit? submit)
    {
        submit=null;var m=Regex.Match(body,@"^AGENT_PROMPT_COMMIT request=([1-9][0-9]{0,9})$",RegexOptions.CultureInvariant);if(!m.Success)return false;
        var d=draft;draft=null;if(d==null||!int.TryParse(m.Groups[1].Value,out int id)||id!=d.RequestId||d.Seen.Any(x=>!x)||now-d.StartedAt>TimeSpan.FromSeconds(30))return true;
        byte[] bytes=new byte[d.Bytes];int p=0;foreach(var part in d.Data){if(part==null)return true;Buffer.BlockCopy(part,0,bytes,p,part.Length);p+=part.Length;}
        try{string prompt=StrictUtf8.GetString(bytes);if(string.IsNullOrWhiteSpace(prompt)||prompt.IndexOf('\0')>=0)return true;submit=new(d.RequestId,d.WorkspaceId,d.Sandbox,prompt);}catch(DecoderFallbackException){}
        return true;
    }
    private static bool TrySimple(string body,string verb,Func<int,PanelAgentInbound> factory,out PanelAgentInbound? inbound)
    {
        inbound=null;var m=Regex.Match(body,"^"+verb+@" request=([1-9][0-9]{0,9})$",RegexOptions.CultureInvariant);if(!m.Success)return false;
        if(int.TryParse(m.Groups[1].Value,out int id))inbound=factory(id);return true;
    }
}

public sealed record PanelAgentControlSnapshot(PanelAgentPhase Phase=PanelAgentPhase.Idle,int RequestId=0,
    PanelAgentAction Action=PanelAgentAction.None,string Result="IDLE",DateTimeOffset? UpdatedAt=null,
    PanelWorkspace[]? Workspaces=null,PanelAgentHistory[]? History=null)
{
    public PanelWorkspace[] EffectiveWorkspaces=>Workspaces??[];public PanelAgentHistory[] EffectiveHistory=>History??[];
    public string Wire(bool locked,ManagedCodexSnapshot managed)
    {
        var now=DateTimeOffset.UtcNow;
        return JsonSerializer.Serialize(new{type="opsdeck.agent_control.v1",locked,
            phase=(int)(locked?PanelAgentPhase.Idle:Phase),request_id=locked?0:RequestId,action=(int)(locked?PanelAgentAction.None:Action),result=locked?"LOCKED":Result,
            managed_running=!locked&&string.Equals(managed.Activity,"RUNNING",StringComparison.Ordinal),managed_reconcile=!locked&&managed.ReconciliationRequired,
            workspaces=locked?Array.Empty<object>():EffectiveWorkspaces.Select(w=>(object)new{id=w.Id,name=w.Name}).ToArray(),
            history=locked?Array.Empty<object>():EffectiveHistory.Take(4).Select(h=>(object)new{action=(int)h.Action,state=(int)h.State,result=h.Result,age_s=Math.Clamp(h.AgeSeconds,0,604800)}).ToArray()},Json.Options);
    }
}


