using OpsDeck.Core;
namespace OpsDeck.Tests;

public static class M61AgentControlTests
{
    public static async Task Run(Action<string,bool> check)
    {
        void C(string name,bool ok)=>check("m61-"+name,ok);
        void Throws(string name,Action action){try{action();C(name,false);}catch(Exception e)when(e is ArgumentException or InvalidOperationException or KeyNotFoundException){C(name,true);}}
        var now=DateTimeOffset.UtcNow;
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m61-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            using var store=new AgentCommandStore(Path.Combine(dir,"agent-control.db"));
            var fake=new FakeProvider();var control=new AgentControlPlane(store,[fake]);
            var req=R("req_m61_0001",AgentProviderKind.Codex,AgentCommandAction.RetryTask,"task:abc","idem:m61:0001",now);
            var created=control.Request(req,now);C("request-awaits-approval",created.State==AgentCommandState.Requested);
            var duplicate=control.Request(req with{RequestId="req_m61_0002"},now);C("idempotent-request",duplicate.RequestId==created.RequestId);
            Throws("idempotency-collision",()=>control.Request(req with{RequestId="req_m61_0003",Action=AgentCommandAction.StopTask},now));
            await ThrowsAsync("execute-before-approval",async()=>await control.ExecuteAsync(created.RequestId),C);
            var approved=control.Approve(created.RequestId,now.AddSeconds(1));C("approval-transition",approved.State==AgentCommandState.Approved);
            C("approval-idempotent",control.Approve(created.RequestId,now.AddSeconds(2)).State==AgentCommandState.Approved);
            var done=await control.ExecuteAsync(created.RequestId);C("provider-success",done.State==AgentCommandState.Succeeded&&done.ResultCode=="FAKE_OK");
            C("provider-once",fake.Calls==1);
            var events=store.ReadEvents(created.RequestId);C("audit-chain",events.Select(x=>x.Code).SequenceEqual(["REQUESTED","APPROVED","EXECUTING","SUCCEEDED"]));
            C("terminal-idempotent",(await control.ExecuteAsync(created.RequestId)).State==AgentCommandState.Succeeded&&fake.Calls==1);
            Throws("unsafe-target",()=>control.Request(R("req_m61_bad1",AgentProviderKind.Codex,AgentCommandAction.StopTask,"task abc","idem:m61:bad1",now),now));
            Throws("hermes-disabled",()=>control.Request(R("req_m61_herm",AgentProviderKind.Hermes,AgentCommandAction.StopTask,"task:abc","idem:m61:herm",now),now));
            Throws("unattended-disabled",()=>control.Request(R("req_m61_auto",AgentProviderKind.Codex,AgentCommandAction.StopTask,"task:abc","idem:m61:auto",now) with{RequiresApproval=false},now));

            var unsupported=control.Request(R("req_m61_0004",AgentProviderKind.Codex,AgentCommandAction.SubmitTask,"task:def","idem:m61:0004",now),now);
            control.Approve(unsupported.RequestId,now.AddSeconds(1));
            await ThrowsAsync("provider-capability-gate",async()=>await control.ExecuteAsync(unsupported.RequestId),C);
            C("capability-no-transition",store.Get(unsupported.RequestId)?.State==AgentCommandState.Approved);
            Throws("request-id-collision",()=>control.Request(req with{IdempotencyKey="idem:m61:new1"},now));

            var noProviderControl=new AgentControlPlane(store);
            var noProvider=noProviderControl.Request(R("req_m61_none",AgentProviderKind.Codex,AgentCommandAction.StopTask,"task:none","idem:m61:none",now),now);
            noProviderControl.Approve(noProvider.RequestId,now.AddSeconds(1));
            await ThrowsAsync("unregistered-provider-blocked",async()=>await noProviderControl.ExecuteAsync(noProvider.RequestId),C);
            C("unregistered-provider-no-transition",store.Get(noProvider.RequestId)?.State==AgentCommandState.Approved);

            var timeoutPolicy=AgentControlPolicy.SafeDefault with{ExecutionTimeout=TimeSpan.FromMilliseconds(50)};
            var slowControl=new AgentControlPlane(store,[new SlowProvider()],timeoutPolicy);
            var slow=slowControl.Request(R("req_m61_slow",AgentProviderKind.Codex,AgentCommandAction.StopTask,"task:slow","idem:m61:slow",now),now);
            slowControl.Approve(slow.RequestId,now.AddSeconds(1));var timed=await slowControl.ExecuteAsync(slow.RequestId);
            C("execution-timeout",timed.State==AgentCommandState.Failed&&timed.ResultCode=="TIMEOUT");
            Throws("already-expired-request",()=>control.Request(R("req_m61_old1",AgentProviderKind.Codex,AgentCommandAction.StopTask,"task:old","idem:m61:old1",now,TimeSpan.Zero),now));

            var exp=control.Request(R("req_m61_exp1",AgentProviderKind.Codex,AgentCommandAction.StopTask,"task:exp","idem:m61:exp1",now,TimeSpan.FromSeconds(2)),now);
            C("expiry-sweep",store.ExpireDue(now.AddSeconds(3))==1&&store.Get(exp.RequestId)?.State==AgentCommandState.Expired);
            Throws("expired-cannot-approve",()=>control.Approve(exp.RequestId,now.AddSeconds(4)));

            var reject=control.Request(R("req_m61_rej1",AgentProviderKind.ChatGPT,AgentCommandAction.StopTask,"chat:1","idem:m61:rej1",now),now);
            C("reject-transition",control.Reject(reject.RequestId,now.AddSeconds(1)).State==AgentCommandState.Rejected);
            C("reject-idempotent",control.Reject(reject.RequestId,now.AddSeconds(2)).State==AgentCommandState.Rejected);
            Throws("rejected-cannot-approve",()=>control.Approve(reject.RequestId,now.AddSeconds(3)));

            string persistPath=Path.Combine(dir,"persist.db");
            using(var first=new AgentCommandStore(persistPath))
            {
                var c1=new AgentControlPlane(first,[fake]);c1.Request(R("req_m61_pers",AgentProviderKind.Codex,AgentCommandAction.RetryTask,"task:persist","idem:m61:pers",now),now);
            }
            using(var second=new AgentCommandStore(persistPath))
                C("durable-request",second.Get("req_m61_pers")?.State==AgentCommandState.Requested);

            string hostDir=Path.Combine(dir,"host-wiring");Directory.CreateDirectory(hostDir);
            await using(var engine=new AppEngine(new HostConfig(),new LocalSettings(hostDir)))
            {
                engine.Start(serial:false,wifiTelemetry:false);C("host-control-ready",engine.AgentControlAvailable);
                var hostReq=engine.RequestAgentCommand(R("req_m61_host",AgentProviderKind.Codex,AgentCommandAction.StopTask,"task:host","idem:m61:host",DateTimeOffset.UtcNow));
                C("host-request-persisted",engine.ReadAgentCommand(hostReq.RequestId)?.State==AgentCommandState.Requested);
                C("host-approve",engine.ApproveAgentCommand(hostReq.RequestId).State==AgentCommandState.Approved);
                var blocked=await engine.ExecuteAgentCommandAsync(hostReq.RequestId);C("host-provider-ownership-gate",blocked.State==AgentCommandState.Failed&&blocked.ResultCode=="PROVIDER_ERROR");
                C("host-audit-events",engine.ReadAgentCommandEvents(hostReq.RequestId).Length>=4);
            }
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
    private static AgentCommandRequest R(string id,AgentProviderKind provider,AgentCommandAction action,string target,string key,DateTimeOffset now,TimeSpan? lifetime=null)
        =>new(id,provider,action,target,key,now,now+(lifetime??TimeSpan.FromMinutes(1)));

    private static async Task ThrowsAsync(string name,Func<Task> action,Action<string,bool> check)
    {
        try{await action();check(name,false);}
        catch(Exception e)when(e is ArgumentException or InvalidOperationException or KeyNotFoundException){check(name,true);}
    }

    private sealed class FakeProvider : IAgentControlProvider
    {
        public int Calls;
        public AgentProviderKind Kind=>AgentProviderKind.Codex;
        public AgentCapability Capabilities=>AgentCapability.StopTask|AgentCapability.RetryTask|AgentCapability.ResumeTask;
        public Task<AgentCommandOutcome> ExecuteAsync(AgentCommand command,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Calls++;return Task.FromResult(new AgentCommandOutcome(true,"FAKE_OK"));
        }
    }
    private sealed class SlowProvider : IAgentControlProvider
    {
        public AgentProviderKind Kind=>AgentProviderKind.Codex;
        public AgentCapability Capabilities=>AgentCapability.StopTask;
        public async Task<AgentCommandOutcome> ExecuteAsync(AgentCommand command,CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(10),ct);return new(true,"SLOW_OK");
        }
    }
}
