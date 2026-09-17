using System.Reflection;
using System.Text;
using System.Text.Json;
using OpsDeck.Core;

namespace OpsDeck.Tests;

public static class M67DeckControlTests
{
    public static async Task Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m67-"+n,ok);
        var now=DateTimeOffset.UtcNow;
        TestAssembler(C,now);TestFrame(C,now);TestWorkspaceCatalog(C);
        await TestEngine(C);
    }
    private static void TestAssembler(Action<string,bool> c,DateTimeOffset now)
    {
        const string prompt="OpsDeck icin gorev: kota, sicaklik ve RDC ekranini kontrol et. Turkce: görev şğçıöü";
        var a=new PanelAgentAssembler();PanelAgentInbound? inbound=null;
        foreach(var line in Lines(41,2,ManagedCodexSandbox.WorkspaceWrite,prompt)){
            c("assembler-line",a.TryAccept(line,now,out var next));if(next!=null)inbound=next;
        }
        c("assembler-submit",inbound is PanelAgentSubmit s&&s.RequestId==41&&s.WorkspaceId==2&&s.Sandbox==ManagedCodexSandbox.WorkspaceWrite&&s.Prompt==prompt);
        c("assembler-action",a.TryAccept("I (1) opsdeck.ui: AGENT_ACTION request=42 action=stop",now,out var stop)&&stop is PanelAgentActionRequest{Action:PanelAgentAction.Stop});
        c("assembler-confirm",a.TryAccept("I (1) opsdeck.ui: AGENT_CONFIRM request=42",now,out var confirm)&&confirm is PanelAgentConfirm);
        c("assembler-cancel",a.TryAccept("I (1) opsdeck.ui: AGENT_CANCEL request=42",now,out var cancel)&&cancel is PanelAgentCancel);
        var missing=new PanelAgentAssembler();var ml=Lines(43,0,ManagedCodexSandbox.ReadOnly,new string('x',80)).ToArray();PanelAgentInbound? mi=null;foreach(var line in new[]{ml[0],ml[1],ml[^1]}){missing.TryAccept(line,now,out var x);if(x!=null)mi=x;}c("missing-chunk-no-submit",mi==null);
        var timeout=new PanelAgentAssembler();PanelAgentInbound? ti=null;foreach(var line in Lines(44,0,ManagedCodexSandbox.ReadOnly,"timeout test")){timeout.TryAccept(line,line.Contains("COMMIT",StringComparison.Ordinal)?now.AddSeconds(31):now,out var x);if(x!=null)ti=x;}c("timeout-no-submit",ti==null);
    }
    private static void TestFrame(Action<string,bool> c,DateTimeOffset now)
    {
        var snapshot=new PanelAgentControlSnapshot(PanelAgentPhase.AwaitingConfirm,77,PanelAgentAction.Submit,"CONFIRM",now,
            [new PanelWorkspace(0,"OpsDeck",@"C:\Projects\OpsDeck"),new PanelWorkspace(1,"AlazSmartEngine",@"C:\Projects\AlazSmartEngine")],
            [new PanelAgentHistory(PanelAgentAction.Submit,AgentCommandState.Succeeded,"CODEX_SUBMITTED",3)]);
        string wire=snapshot.Wire(false,new ManagedCodexSnapshot(SourceState.Ok,now,true,1,"managed:x","t","v","RUNNING",false));
        using var doc=JsonDocument.Parse(wire);var root=doc.RootElement;
        c("frame-type",root.GetProperty("type").GetString()=="opsdeck.agent_control.v1");
        c("frame-workspaces",root.GetProperty("workspaces").GetArrayLength()==2&&root.GetProperty("workspaces")[0].GetProperty("name").GetString()=="OpsDeck");
        c("frame-history",root.GetProperty("history").GetArrayLength()==1);
        c("frame-bounded",Encoding.UTF8.GetByteCount(wire)<3000);
        var actual=snapshot with{Workspaces=PanelWorkspaceCatalog.Discover()};c("frame-real-catalog-bounded",Encoding.UTF8.GetByteCount(actual.Wire(false,new ManagedCodexSnapshot(SourceState.Ok)))<3000);
        using var locked=JsonDocument.Parse(snapshot.Wire(true,new ManagedCodexSnapshot(SourceState.Ok)));
        c("frame-lock-hides",locked.RootElement.GetProperty("workspaces").GetArrayLength()==0&&locked.RootElement.GetProperty("request_id").GetInt32()==0);
    }
    private static void TestWorkspaceCatalog(Action<string,bool> c)
    {
        string root=Path.Combine(Path.GetTempPath(),"opsdeck-m67-ws-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{var a=Directory.CreateDirectory(Path.Combine(root,"SürüTakip"));var b=Directory.CreateDirectory(Path.Combine(root,"OpsDeck"));File.WriteAllText(Path.Combine(a.FullName,"README.md"),"test");File.WriteAllText(Path.Combine(b.FullName,"README.md"),"test");var rows=PanelWorkspaceCatalog.Discover(root);c("workspace-count",rows.Length==2);c("workspace-ascii",rows.All(x=>x.Name.All(ch=>ch>=32&&ch<=126)));}
        finally{Directory.Delete(root,true);}
        string own=Path.Combine(Path.GetTempPath(),"opsdeck-m67-own-"+Guid.NewGuid().ToString("N")+".json");var store=new ManagedCodexOwnershipStore(own);var t=DateTimeOffset.UtcNow;store.Put(new("managed:older","thread1","turn1",@"C:\Projects\OpsDeck",ManagedCodexSandbox.ReadOnly,t.AddMinutes(-1)));store.Put(new("managed:newer","thread2","turn2",@"C:\Projects\OpsDeck",ManagedCodexSandbox.ReadOnly,t));var runtime=new CodexAppServerRuntime(new HostConfig(),own);c("reconcile-active-target",runtime.Snapshot.ReconciliationRequired&&runtime.Snapshot.ActiveTarget=="managed:newer");runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();File.Delete(own);
    }
    private static async Task TestEngine(Action<string,bool> c)
    {
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m67-engine-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);const string secret="M67_PANEL_PROMPT_MUST_STAY_TRANSIENT";
        var engine=new AppEngine(new HostConfig(),new LocalSettings(dir));
        try
        {
            engine.Start(serial:false,wifiTelemetry:false);var workspaces=engine.PanelAgentControl.EffectiveWorkspaces;c("engine-workspaces",workspaces.Length>0);if(workspaces.Length==0)return;
            var observe=typeof(AppEngine).GetMethod("ObservePanelAgentLine",BindingFlags.Instance|BindingFlags.NonPublic)!;
            foreach(var line in Lines(901,workspaces[0].Id,ManagedCodexSandbox.ReadOnly,secret))observe.Invoke(engine,[line]);
            await Wait(()=>engine.PanelAgentControl.Phase==PanelAgentPhase.AwaitingConfirm);
            c("engine-await-confirm",engine.PanelAgentControl is {RequestId:901,Action:PanelAgentAction.Submit,Phase:PanelAgentPhase.AwaitingConfirm});
            c("engine-no-command-before-confirm",engine.ReadRecentAgentCommands(10).Length==0);
            await Task.Delay(150);c("engine-staged-still-no-command",engine.PanelAgentControl.Phase==PanelAgentPhase.AwaitingConfirm&&engine.ReadRecentAgentCommands(10).Length==0);
            observe.Invoke(engine,["I (1) opsdeck.ui: AGENT_CANCEL request=901"]);await Wait(()=>engine.PanelAgentControl.Phase==PanelAgentPhase.Rejected);
            c("engine-cancel",engine.PanelAgentControl.Result=="CANCELLED"&&engine.ReadRecentAgentCommands(10).Length==0);
        }
        finally{await engine.DisposeAsync();}
        string db=Path.Combine(dir,"agent-control.db");c("engine-prompt-not-db",File.Exists(db)&&!Encoding.UTF8.GetString(File.ReadAllBytes(db)).Contains(secret,StringComparison.Ordinal));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);
    }
    private static async Task Wait(Func<bool> done)
    {var until=DateTimeOffset.UtcNow.AddSeconds(3);while(DateTimeOffset.UtcNow<until&&!done())await Task.Delay(25);}
    private static IEnumerable<string> Lines(int request,int repo,ManagedCodexSandbox sandbox,string prompt)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(prompt);int chunks=(bytes.Length+PanelAgentAssembler.ChunkBytes-1)/PanelAgentAssembler.ChunkBytes;
        yield return $"I (1) opsdeck.ui: AGENT_PROMPT_BEGIN request={request} bytes={bytes.Length} chunks={chunks} repo={repo} sandbox={(int)sandbox}";
        for(int i=0;i<chunks;i++){
            int start=i*PanelAgentAssembler.ChunkBytes,count=Math.Min(PanelAgentAssembler.ChunkBytes,bytes.Length-start);
            yield return $"I (1) opsdeck.ui: AGENT_PROMPT_CHUNK request={request} index={i} data={Convert.ToHexString(bytes.AsSpan(start,count))}";
        }
        yield return $"I (1) opsdeck.ui: AGENT_PROMPT_COMMIT request={request}";
    }
}
