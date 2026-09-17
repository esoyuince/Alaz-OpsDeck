using System.Reflection;
using System.Text;
using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;

public static class M68SessionCenterTests
{
    public static async Task Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m68-"+n,ok);var now=DateTimeOffset.UtcNow;
        TestChatPage(C,now);TestAssembler(C,now);TestEdgeCases(C,now);TestCacheAndEncoding(C,now);await TestOwnedCatalog(C,now);await TestEngineFrame(C,now);
    }
    private static void TestChatPage(Action<string,bool> c,DateTimeOffset now)
    {
        var msgs=new[]{new CodexChatMessage(CodexChatRole.User,"Türkçe: ç ğ ı ö ş ü İ", "t1",0),
            new CodexChatMessage(CodexChatRole.Assistant,"Yanıt bir", "t1",1),new CodexChatMessage(CodexChatRole.User,"devam", "t2",2),
            new CodexChatMessage(CodexChatRole.Assistant,"en yeni yanıt", "t2",3)};
        var conv=new CodexConversation("managed:test","0123456789abcdef","OpsDeck",false,"COMPLETED",msgs,now);
        var p0=PanelCodexChatPage.Build(7,conv,0);var p1=PanelCodexChatPage.Build(8,conv,1);
        c("chat-newest-page",p0.TotalPages==2&&p0.Messages.Length==3&&p0.Messages[^1].Text=="en yeni yanıt");
        c("chat-older-page",p1.Messages.Length==1&&p1.Messages[0].Role==CodexChatRole.User);
        string wire=p0.Wire(),oldWire=p1.Wire();using(var decoded=JsonDocument.Parse(oldWire))
        {string text=decoded.RootElement.GetProperty("messages")[0].GetProperty("text").GetString()!;c("chat-ascii-wire-roundtrip",oldWire.All(ch=>ch<=0x7f)&&text=="Türkçe: ç ğ ı ö ş ü İ");}
        c("chat-budget",Encoding.UTF8.GetByteCount(wire)<3000&&Encoding.UTF8.GetByteCount(oldWire)<3000);
    }
    private static void TestAssembler(Action<string,bool> c,DateTimeOffset now)
    {
        var a=new PanelCodexContinueAssembler();string key="0123456789abcdef",prompt="Devam et: Türkçe çğışöü";PanelCodexInbound? inbound=null;
        foreach(var line in ContinueLines(21,key,prompt)){c("continue-line",a.TryAccept(line,now,out var x));if(x!=null)inbound=x;}
        c("continue-utf8",inbound is PanelCodexContinue p&&p.Key==key&&p.Prompt==prompt);
        c("chat-request",a.TryAccept("I (1) opsdeck.ui: CODEX_CHAT_REQUEST request=22 key=0123456789abcdef page=3",now,out var q)&&q is PanelCodexChatRequest{Page:3});
        c("session-action",a.TryAccept("I (1) opsdeck.ui: CODEX_SESSION_ACTION request=23 key=0123456789abcdef action=stop",now,out var s)&&s is PanelCodexAction{Action:AgentCommandAction.StopTask});
        c("approval-allow",a.TryAccept("I (1) opsdeck.ui: CODEX_APPROVAL request=24 approval=9 decision=allow",now,out var ap)&&ap is PanelCodexApproval{ApprovalId:9,Allow:true});
        c("approval-deny",a.TryAccept("I (1) opsdeck.ui: CODEX_APPROVAL request=25 approval=10 decision=deny",now,out var dn)&&dn is PanelCodexApproval{ApprovalId:10,Allow:false});
        var missing=new PanelCodexContinueAssembler();var lines=ContinueLines(26,key,new string('x',80)).ToArray();PanelCodexInbound? bad=null;
        foreach(var line in new[]{lines[0],lines[1],lines[^1]}){missing.TryAccept(line,now,out var x);if(x!=null)bad=x;}c("missing-chunk",bad==null);
    }
    private static void TestEdgeCases(Action<string,bool> c,DateTimeOffset now)
    {
        string key="0123456789abcdef";
        var longMsg=new CodexConversation("managed:test",key,"OpsDeck",false,"READY",
            [new CodexChatMessage(CodexChatRole.Assistant,new string('ş',400),"t",0)],now);
        string wire=PanelCodexChatPage.Build(31,longMsg,0).Wire();using(var doc=JsonDocument.Parse(wire))
        {string text=doc.RootElement.GetProperty("messages")[0].GetProperty("text").GetString()!;c("utf8-boundary",Encoding.UTF8.GetByteCount(text)<=PanelCodexChatPage.MaxMessageBytes&&text.EndsWith("...",StringComparison.Ordinal));}
        var malformed=new PanelCodexContinueAssembler();
        c("reject-uppercase-key",!malformed.TryAccept("I (1) opsdeck.ui: CODEX_CHAT_REQUEST request=1 key=ABCDEF0123456789 page=0",now,out _));
        c("reject-unknown-action",!malformed.TryAccept($"I (1) opsdeck.ui: CODEX_SESSION_ACTION request=2 key={key} action=erase",now,out _));
        c("reject-zero-approval",!malformed.TryAccept("I (1) opsdeck.ui: CODEX_APPROVAL request=3 approval=0 decision=allow",now,out _));
        var tooLong=new PanelCodexContinueAssembler();PanelCodexInbound? inbound=null;
        c("overlong-begin-consumed",tooLong.TryAccept($"I (1) opsdeck.ui: CODEX_CONTINUE_BEGIN request=4 key={key} bytes=2049 chunks=43",now,out _));
        c("overlong-commit-no-message",tooLong.TryAccept("I (1) opsdeck.ui: CODEX_CONTINUE_COMMIT request=4",now,out inbound)&&inbound==null);
        var invalidUtf8=new PanelCodexContinueAssembler();invalidUtf8.TryAccept($"I (1) opsdeck.ui: CODEX_CONTINUE_BEGIN request=5 key={key} bytes=2 chunks=1",now,out _);invalidUtf8.TryAccept("I (1) opsdeck.ui: CODEX_CONTINUE_CHUNK request=5 index=0 data=C328",now,out _);invalidUtf8.TryAccept("I (1) opsdeck.ui: CODEX_CONTINUE_COMMIT request=5",now,out inbound);c("invalid-utf8-no-message",inbound==null);
        var duplicate=new PanelCodexContinueAssembler();var lines=ContinueLines(6,key,"duplicate-check").ToArray();duplicate.TryAccept(lines[0],now,out _);duplicate.TryAccept(lines[1],now,out _);duplicate.TryAccept(lines[1],now,out _);duplicate.TryAccept(lines[^1],now,out inbound);c("duplicate-chunk-invalidates",inbound==null);
    }
    private static void TestCacheAndEncoding(Action<string,bool> c,DateTimeOffset now)
    {
        var field=typeof(AppEngine).GetField("PanelSerialEncoding",BindingFlags.Static|BindingFlags.NonPublic);var enc=field?.GetValue(null) as Encoding;
        c("serial-ascii-wire",enc!=null&&enc.CodePage==Encoding.ASCII.CodePage&&enc.GetPreamble().Length==0&&enc.GetBytes("ASCII").AsSpan().SequenceEqual(new byte[]{65,83,67,73,73}));
        var start=typeof(CodexTelemetrySampler).GetMethod("AppServerStartInfo",BindingFlags.Static|BindingFlags.NonPublic);var psi=start?.Invoke(null,new object[]{"codex.exe"}) as System.Diagnostics.ProcessStartInfo;
        c("codex-process-utf8",psi!=null&&psi.RedirectStandardInput&&psi.RedirectStandardOutput&&psi.RedirectStandardError&&psi.StandardInputEncoding?.CodePage==65001&&psi.StandardOutputEncoding?.CodePage==65001&&psi.StandardErrorEncoding?.CodePage==65001&&psi.StandardInputEncoding.GetPreamble().Length==0&&psi.StandardInputEncoding.EncoderFallback is EncoderExceptionFallback&&psi.StandardOutputEncoding.DecoderFallback is DecoderExceptionFallback&&psi.ArgumentList.SequenceEqual(new[]{"app-server","--stdio"}));
        var rpc=typeof(CodexAppServerRuntime).GetMethod("CodexRpcError",BindingFlags.Static|BindingFlags.NonPublic);using var errDoc=JsonDocument.Parse("{\"code\":-32600,\"message\":\"no active turn to interrupt\"}");string detail=(string)(rpc?.Invoke(null,new object[]{errDoc.RootElement})??"");c("rpc-error-detail",detail.Contains("-32600",StringComparison.Ordinal)&&detail.Contains("no active turn",StringComparison.Ordinal));
        var race=typeof(CodexAppServerRuntime).GetMethod("IsNoActiveTurn",BindingFlags.Static|BindingFlags.NonPublic);c("stop-race-detect",race!=null&&(bool)race.Invoke(null,new object[]{new InvalidOperationException(detail)})!);
        var cache=new PanelCodexPageCache();PanelCodexChatPage Page(int i)=>new(i,"0123456789abcdef","OpsDeck",0,1,false,"READY",[new(CodexChatRole.Assistant,"m"+i,"t",i)]);
        for(int i=0;i<33;i++)cache.Put("k"+i,0,Page(i),now.AddSeconds(i));
        c("cache-bounded",cache.Count==PanelCodexPageCache.MaxEntries&&!cache.TryGet("k0",0,out _,out _)&&cache.TryGet("k32",0,out var latest,out _)&&latest?.Messages[0].Text=="m32");
        cache.Put("same",0,Page(40),now);cache.Put("same",1,Page(41),now);cache.Invalidate("same");c("cache-invalidate",!cache.TryGet("same",0,out _,out _)&&!cache.TryGet("same",1,out _,out _));
    }
    private static async Task TestOwnedCatalog(Action<string,bool> c,DateTimeOffset now)
    {
        string path=Path.Combine(Path.GetTempPath(),"opsdeck-m68-own-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            var store=new ManagedCodexOwnershipStore(path);store.Put(new("managed:m68one","thread-one","turn-one",@"C:\Projects\OpsDeck",ManagedCodexSandbox.ReadOnly,now));
            await using var runtime=new CodexAppServerRuntime(new HostConfig(),path);var rows=runtime.ReadOwnedSessions();
            c("owned-only",rows.Length==1&&rows[0].Target=="managed:m68one"&&rows[0].Repo=="OpsDeck");
            c("stable-key",rows[0].Key.Length==16&&rows[0].Key.All(Uri.IsHexDigit)&&runtime.ResolveOwnedTarget(rows[0].Key)=="managed:m68one");
            bool blocked=false;try{runtime.ResolveOwnedTarget("0000000000000000");}catch(KeyNotFoundException){blocked=true;}c("unknown-key-blocked",blocked);
        }
        finally{try{File.Delete(path);}catch{}}
    }
    private static async Task TestEngineFrame(Action<string,bool> c,DateTimeOffset now)
    {
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m68-engine-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            var settings=new LocalSettings(dir);var own=new ManagedCodexOwnershipStore(Path.Combine(dir,"managed-codex-ownership.json"));
            own.Put(new("managed:m68frame","thread-frame","turn-frame",@"C:\Projects\OpsDeck",ManagedCodexSandbox.ReadOnly,now));
            await using var engine=new AppEngine(new HostConfig(),settings);engine.Start(serial:false,wifiTelemetry:false);
            var method=typeof(AppEngine).GetMethod("PanelCodexSessionsFrame",BindingFlags.Instance|BindingFlags.NonPublic)!;
            string wire=(string)method.Invoke(engine,null)!;using var doc=JsonDocument.Parse(wire);var root=doc.RootElement;
            c("sessions-type",root.GetProperty("type").GetString()=="opsdeck.codex_sessions.v1");
            c("sessions-one",root.GetProperty("sessions").GetArrayLength()==1&&root.GetProperty("sessions")[0].GetProperty("repo").GetString()=="OpsDeck");
            c("sessions-no-thread-id",!wire.Contains("thread-frame",StringComparison.Ordinal)&&!wire.Contains("managed:m68frame",StringComparison.Ordinal));
            c("sessions-budget",Encoding.UTF8.GetByteCount(wire)<3000);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{Directory.Delete(dir,true);}catch{}}
    }
    public static async Task<int> LiveSessionCenter()
    {
        var cfg=new LocalSettings().Load();string own=Path.Combine(Path.GetTempPath(),"opsdeck-m68-live-"+Guid.NewGuid().ToString("N")+".json"),target="managed:m68"+Guid.NewGuid().ToString("N")[..10];
        try
        {
            await using var runtime=new CodexAppServerRuntime(cfg,own);
            await runtime.SubmitAsync(target,new ManagedCodexPayload("Reply only M68_FIRST_OK. Do not read, write, or modify files.",@"C:\\Antigravity\\OpsDeck",ManagedCodexSandbox.ReadOnly),CancellationToken.None);
            await WaitDone(runtime,TimeSpan.FromSeconds(45));var first=await runtime.ReadConversationAsync(target,CancellationToken.None);
            await runtime.ContinueAsync(target,"Reply only M68_SECOND_OK. Do not read, write, or modify files.",CancellationToken.None);
            await WaitDone(runtime,TimeSpan.FromSeconds(45));var second=await runtime.ReadConversationAsync(target,CancellationToken.None);
            bool ok=first.Messages.Any(x=>x.Role==CodexChatRole.Assistant&&x.Text.Contains("M68_FIRST_OK",StringComparison.Ordinal))&&second.Messages.Any(x=>x.Role==CodexChatRole.Assistant&&x.Text.Contains("M68_SECOND_OK",StringComparison.Ordinal))&&second.Messages.Count(x=>x.Role==CodexChatRole.User)>=2;
            Console.WriteLine(JsonSerializer.Serialize(new{ok,first_messages=first.Messages.Length,second_messages=second.Messages.Length,activity=runtime.Snapshot.Activity}));return ok?0:1;
        }
        catch(Exception e){Console.WriteLine(JsonSerializer.Serialize(new{ok=false,error=e.GetType().Name,message=e.Message}));return 1;}
        finally{try{if(File.Exists(own))File.Delete(own);}catch{}}
    }
    public static async Task<int> LiveActions()
    {
        var cfg=new LocalSettings().Load();string own=Path.Combine(Path.GetTempPath(),"opsdeck-m68-actions-"+Guid.NewGuid().ToString("N")+".json"),target="managed:m68act"+Guid.NewGuid().ToString("N")[..8];
        string step="start";
        try
        {
            await using var runtime=new CodexAppServerRuntime(cfg,own);
            var payload=new ManagedCodexPayload("Run a shell command that waits 20 seconds without writing files, then reply M68_ACTION_DONE. Do not read, write, or modify files.",@"C:\\Antigravity\\OpsDeck",ManagedCodexSandbox.ReadOnly);
            step="submit";await runtime.SubmitAsync(target,payload,CancellationToken.None);bool submitted=runtime.Snapshot.Activity=="RUNNING";await Task.Delay(1500);
            step="stop1";await runtime.StopAsync(target,CancellationToken.None);bool stopped=runtime.Snapshot.Activity=="INTERRUPTED";
            step="resume";await runtime.ResumeAsync(target,CancellationToken.None);bool resumed=runtime.Snapshot.Activity=="READY";
            step="retry";await runtime.RetryAsync(target,CancellationToken.None);bool retried=runtime.Snapshot.Activity=="RUNNING";await Task.Delay(1500);
            step="stop2";await runtime.StopAsync(target,CancellationToken.None);bool restopped=runtime.Snapshot.Activity=="INTERRUPTED";
            bool ok=submitted&&stopped&&resumed&&retried&&restopped;
            Console.WriteLine(JsonSerializer.Serialize(new{ok,submitted,stopped,resumed,retried,restopped,activity=runtime.Snapshot.Activity}));return ok?0:1;
        }
        catch(Exception e){Console.WriteLine(JsonSerializer.Serialize(new{ok=false,step,error=e.GetType().Name,message=e.Message}));return 1;}
        finally{try{if(File.Exists(own))File.Delete(own);}catch{}}
    }

    private static async Task WaitDone(CodexAppServerRuntime runtime,TimeSpan timeout)
    {var until=DateTimeOffset.UtcNow+timeout;while(DateTimeOffset.UtcNow<until&&runtime.Snapshot.Activity=="RUNNING")await Task.Delay(250);if(runtime.Snapshot.Activity=="RUNNING")throw new TimeoutException("Managed Codex turn did not complete.");}

    private static IEnumerable<string> ContinueLines(int request,string key,string prompt)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(prompt);int chunks=(bytes.Length+PanelCodexContinueAssembler.ChunkBytes-1)/PanelCodexContinueAssembler.ChunkBytes;
        yield return $"I (1) opsdeck.ui: CODEX_CONTINUE_BEGIN request={request} key={key} bytes={bytes.Length} chunks={chunks}";
        for(int i=0;i<chunks;i++)
        {
            int start=i*PanelCodexContinueAssembler.ChunkBytes,count=Math.Min(PanelCodexContinueAssembler.ChunkBytes,bytes.Length-start);
            yield return $"I (1) opsdeck.ui: CODEX_CONTINUE_CHUNK request={request} index={i} data={Convert.ToHexString(bytes.AsSpan(start,count))}";
        }
        yield return $"I (1) opsdeck.ui: CODEX_CONTINUE_COMMIT request={request}";
    }
}
