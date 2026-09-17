using System.Text;
using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;

public static class M66AgentIntegrationTests
{
    public static async Task Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m66-"+n,ok);
        var now=DateTimeOffset.UtcNow;string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m66-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            var payloads=new AgentPayloadRegistry();await using var fake=new FakeCodex();var provider=new ManagedCodexProvider(fake,payloads);
            string reqId="req_m66_submit",target="managed:unit001";payloads.Put(reqId,new("Reply OK only",dir,ManagedCodexSandbox.ReadOnly));
            var command=new AgentCommand(reqId,AgentProviderKind.Codex,AgentCommandAction.SubmitTask,target,"idem:m66:submit",true,now,now.AddMinutes(1),AgentCommandState.Executing,now);
            var outcome=await provider.ExecuteAsync(command,CancellationToken.None);
            C("provider-submit",outcome.Success&&outcome.Code=="CODEX_SUBMITTED"&&fake.Submits==1&&fake.LastTarget==target&&payloads.Count==0);
            C("provider-capabilities",provider.Capabilities==(AgentCapability.SubmitTask|AgentCapability.StopTask|AgentCapability.ResumeTask|AgentCapability.RetryTask|AgentCapability.ContinueTask));
            var missing=await provider.ExecuteAsync(command with{RequestId="req_m66_missing"},CancellationToken.None);C("missing-payload-fails",!missing.Success&&missing.Code=="NO_PAYLOAD");
            await provider.ExecuteAsync(command with{Action=AgentCommandAction.StopTask},CancellationToken.None);await provider.ExecuteAsync(command with{Action=AgentCommandAction.ResumeTask},CancellationToken.None);await provider.ExecuteAsync(command with{Action=AgentCommandAction.RetryTask},CancellationToken.None);
            C("provider-actions",fake.Stops==1&&fake.Resumes==1&&fake.Retries==1);
            var pruning=new AgentPayloadRegistry();pruning.Put("keep",new("keep",dir));pruning.Put("drop",new("drop",dir));C("payload-prune",pruning.Prune(["keep"])==1&&pruning.Count==1&&pruning.TryTake("keep",out _));
            var bounded=new AgentPayloadRegistry();for(int i=0;i<64;i++)bounded.Put("p"+i,new("x",dir));bool boundBlocked=false;try{bounded.Put("overflow",new("x",dir));}catch(InvalidOperationException){boundBlocked=true;}C("payload-bound",boundBlocked&&bounded.Count==64);

            TestRdc(dir,C,now);TestWire(C,now);TestHandoff(dir,C,now);await TestEngine(dir,C);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
    private static void TestRdc(string dir,Action<string,bool> c,DateTimeOffset now)
    {
        string cfg=Path.Combine(dir,"rdc-config.json"),history=Path.Combine(dir,"rdc-history.jsonl");
        File.WriteAllText(cfg,JsonSerializer.Serialize(new{clientId="secret-ignored",usageStats=new{totalCalls=100L,successfulCalls=97L,failedCalls=3L,sessionCount=9L,toolCounts=new{read_file=20,start_process=8}}}));
        var u=DesktopCommanderSampler.ReadUsage(cfg);c("rdc-usage",u.Total==100&&u.Ok==97&&u.Failed==3&&u.Sessions==9&&u.Tools==2);
        File.WriteAllText(history,JsonSerializer.Serialize(new{timestamp=now.ToString("O"),toolName="read_file",arguments=new{secret="do-not-read"},output=new{secret="do-not-read"}})+Environment.NewLine);
        var last=DesktopCommanderSampler.ReadLastTool(history);c("rdc-last-tool",last.Tool=="read_file"&&last.At.HasValue);
        var snap=new DesktopCommanderSnapshot(SourceState.Ok,now,2,100,97,3,9,2,"read_file",now);
        c("rdc-success-permille",snap.SuccessPermille==970);
    }
    private static void TestWire(Action<string,bool> c,DateTimeOffset now)
    {
        var rdc=new DesktopCommanderSnapshot(SourceState.Ok,now,2,10574,10545,29,27,22,"read_file",now.AddSeconds(-4));
        var fleet=FleetState.Empty with{Agents=new AgentSample(1,SourceState.Ok,SourceState.Ok,now,"",null,rdc)};
        using var doc=JsonDocument.Parse(fleet.Wire(now,false));var a=doc.RootElement.GetProperty("agents");
        c("wire-rdc",a.GetProperty("rdc_state").GetInt32()==1&&a.GetProperty("rdc_process_count").GetInt32()==2&&a.GetProperty("rdc_total_calls").GetInt32()==10574&&a.GetProperty("rdc_sessions").GetInt32()==27&&a.GetProperty("rdc_success_permille").GetInt32()==997);
        using var locked=JsonDocument.Parse(fleet.Wire(now,true));c("wire-rdc-lock",locked.RootElement.GetProperty("agents").GetProperty("rdc_total_calls").GetInt32()==-1);
        var managed=new ManagedCodexSnapshot(SourceState.Ok,now,true,2,"managed:test","thread","turn","RUNNING",false);using var managedDoc=JsonDocument.Parse(fleet.Wire(now,false,null,null,managed));var ma=managedDoc.RootElement.GetProperty("agents");
        c("wire-managed",ma.GetProperty("managed_codex_state").GetInt32()==1&&ma.GetProperty("managed_codex_owned").GetInt32()==2&&ma.GetProperty("managed_codex_running").GetBoolean()&&ma.GetProperty("managed_codex_runtime").GetBoolean()&&!ma.GetProperty("managed_codex_reconcile").GetBoolean());
        c("wire-budget",Encoding.UTF8.GetByteCount(fleet.Wire(now,false,null,null,managed))<3000);
    }
    private static void TestHandoff(string dir,Action<string,bool> c,DateTimeOffset now)
    {
        string db=Path.Combine(dir,"handoff.db");using(var store=new AgentHandoffStore(db))
        {
            var h=store.Create("handoff_m66_001",AgentProviderKind.ChatGPT,AgentProviderKind.Codex,"managed:unit001",now);c("handoff-created",h.State==AgentHandoffState.Created);
            h=store.Transition(h.HandoffId,AgentHandoffState.Ready,"READY",now.AddSeconds(1));h=store.Transition(h.HandoffId,AgentHandoffState.Accepted,"ACCEPTED",now.AddSeconds(2));h=store.Transition(h.HandoffId,AgentHandoffState.Completed,"COMPLETED",now.AddSeconds(3));
            c("handoff-lifecycle",h.State==AgentHandoffState.Completed&&store.ReadRecent().Length==1);
        }
        using(var store=new AgentHandoffStore(db))c("handoff-persistent",store.Get("handoff_m66_001")?.State==AgentHandoffState.Completed);
    }
    private static async Task TestEngine(string dir,Action<string,bool> c)
    {
        string hostDir=Path.Combine(dir,"engine");Directory.CreateDirectory(hostDir);const string secretPrompt="OPSDECK_PROMPT_MUST_NOT_PERSIST_66";string db=Path.Combine(hostDir,"agent-control.db");
        var engine=new AppEngine(new HostConfig(),new LocalSettings(hostDir));
        try
        {
            engine.Start(serial:false,wifiTelemetry:false);c("engine-provider-ready",engine.AgentControlAvailable&&engine.ReadUnifiedProviders().Any(x=>x.Kind==UnifiedProviderKind.ManagedCodex&&x.Capabilities.HasFlag(AgentCapability.SubmitTask)));
            var req=engine.RequestManagedCodexTask(secretPrompt,dir,ManagedCodexSandbox.ReadOnly);c("engine-submit-requested",req.State==AgentCommandState.Requested&&req.Target.StartsWith("managed:",StringComparison.Ordinal));
            var approved=engine.ApproveAgentCommand(req.RequestId);c("engine-approval",approved.State==AgentCommandState.Approved);
            c("unified-three",engine.ReadUnifiedProviders().Select(x=>x.Kind).Distinct().Count()==3);
            var handoff=engine.CreateAgentHandoff(AgentProviderKind.ChatGPT,AgentProviderKind.Codex,req.Target);c("engine-handoff",handoff.State==AgentHandoffState.Created&&engine.ReadAgentHandoffs().Length==1);
        }
        finally{await engine.DisposeAsync();}
        byte[] bytes=File.ReadAllBytes(db);c("prompt-not-agent-db",!Encoding.UTF8.GetString(bytes).Contains(secretPrompt,StringComparison.Ordinal));
    }

    public static async Task<int> LiveCodexControl()
    {
        var cfg=new LocalSettings().Load();string target="managed:smoke"+Guid.NewGuid().ToString("N")[..8];string ownership=Path.Combine(Path.GetTempPath(),"opsdeck-m66-live-"+Guid.NewGuid().ToString("N")+".json");
        const string marker="OPSDECK_M6_SMOKE_OK";string thread="",turn="";ManagedCodexSnapshot first=default!,before=default!,after=default!;
        try
        {
            await using(var runtime=new CodexAppServerRuntime(cfg,ownership))
            {
                var ids=await runtime.SubmitAsync(target,new ManagedCodexPayload("Reply only "+marker+". Do not read, write, or modify files.",Path.GetTempPath(),ManagedCodexSandbox.ReadOnly),CancellationToken.None);thread=ids.ThreadId;turn=ids.TurnId;var until=DateTimeOffset.UtcNow.AddSeconds(30);
                while(DateTimeOffset.UtcNow<until&&runtime.Snapshot.Activity=="RUNNING")await Task.Delay(250);first=runtime.Snapshot;if(first.Activity=="RUNNING")await runtime.StopAsync(target,CancellationToken.None);
            }
            bool promptAbsent=File.Exists(ownership)&&!File.ReadAllText(ownership).Contains(marker,StringComparison.Ordinal);
            await using(var resumed=new CodexAppServerRuntime(cfg,ownership)){before=resumed.Snapshot;await resumed.ResumeAsync(target,CancellationToken.None);after=resumed.Snapshot;}
            bool ok=thread.Length>0&&turn.Length>0&&first.Activity is "COMPLETED" or "INTERRUPTED"&&promptAbsent&&before.ReconciliationRequired&&before.OwnedThreads==1&&!after.ReconciliationRequired&&after.Activity=="READY";
            Console.WriteLine(JsonSerializer.Serialize(new{ok,thread,turn,first=first.Activity,promptAbsent,before_activity=before.Activity,before_reconcile=before.ReconciliationRequired,before_owned=before.OwnedThreads,after_activity=after.Activity,after_reconcile=after.ReconciliationRequired}));return ok?0:1;
        }
        catch(Exception e){Console.WriteLine(JsonSerializer.Serialize(new{ok=false,error=e.GetType().Name,message=e.Message,thread,turn,first,before,after}));return 1;}
        finally{try{if(File.Exists(ownership))File.Delete(ownership);}catch{}}
    }
    public static async Task<int> LiveRdc()
    {
        var sample=await new DesktopCommanderSampler().SampleAsync(CancellationToken.None);Console.WriteLine(JsonSerializer.Serialize(sample));return sample.ProcessCount>0?0:1;
    }

    private sealed class FakeCodex : ICodexAppServer
    {
        public int Submits,Stops,Resumes,Retries;public string LastTarget="";public ManagedCodexSnapshot Snapshot=>new(SourceState.Ok,DateTimeOffset.UtcNow,true,1,LastTarget,"thread","turn","READY",false);
        public Task<(string ThreadId,string TurnId)> SubmitAsync(string target,ManagedCodexPayload payload,CancellationToken ct){ct.ThrowIfCancellationRequested();Submits++;LastTarget=target;return Task.FromResult(("thread","turn"));}
        public Task StopAsync(string target,CancellationToken ct){Stops++;return Task.CompletedTask;}public Task ResumeAsync(string target,CancellationToken ct){Resumes++;return Task.CompletedTask;}public Task RetryAsync(string target,CancellationToken ct){Retries++;return Task.CompletedTask;}public Task<string> ContinueAsync(string target,string prompt,CancellationToken ct){LastTarget=target;return Task.FromResult("turn-cont");}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}

