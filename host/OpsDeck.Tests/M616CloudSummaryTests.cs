using System.Text;
using System.Text.Json;
using OpsDeck.Core;

namespace OpsDeck.Tests;

public static class M616CloudSummaryTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m616-cloud-"+n,ok);
        var now=new DateTimeOffset(2026,9,18,18,0,0,TimeSpan.FromHours(3));
        var profile=new CloudAccountConfig{ProfileId="m616",Name="Other Projects",AccountId=new string('a',32),Enabled=true};
        var account=AccountState.Empty(profile);
        var summary=new CloudPanelSummary(SourceState.Ok,12,14,4,8,2,1,3,
            SourceState.Partial,8,3,2,
            SourceState.Ok,20,1200,3_000_000,400_000,
            SourceState.Ok,20,900,7,240);
        string wire=account.Wire(0,2,"1234abcd",now,summary);
        using var doc=JsonDocument.Parse(wire);
        var root=doc.RootElement;
        C("summary-present",root.TryGetProperty("summary",out var s));
        C("inventory",s.GetProperty("inventory_state").GetInt32()==1&&s.GetProperty("resource_count").GetInt32()==14&&s.GetProperty("complete_sources").GetInt32()==4);
        C("resource-kinds",s.GetProperty("workers").GetInt32()==8&&s.GetProperty("d1").GetInt32()==2&&s.GetProperty("r2").GetInt32()==1&&s.GetProperty("pages").GetInt32()==3);
        C("queues",s.GetProperty("queue_state").GetInt32()==5&&s.GetProperty("queue_count").GetInt32()==3&&s.GetProperty("queue_observed").GetInt32()==2);
        C("ai",s.GetProperty("ai_requests").GetDouble()==1200&&s.GetProperty("ai_input_tokens").GetDouble()==3_000_000&&s.GetProperty("ai_output_tokens").GetDouble()==400_000);
        C("gateway",s.GetProperty("gateway_requests").GetDouble()==900&&s.GetProperty("gateway_errors").GetDouble()==7&&s.GetProperty("gateway_cached").GetDouble()==240);
        using var legacy=JsonDocument.Parse(account.Wire(0,2,"1234abcd",now));
        C("legacy-omits-summary",!legacy.RootElement.TryGetProperty("summary",out _));
        var unknown=summary with{AiRequests=null,AiInputTokens=null,AiOutputTokens=null,GatewayRequests=null,GatewayErrors=null,GatewayCached=null};
        using var unknownDoc=JsonDocument.Parse(account.Wire(0,2,"1234abcd",now,unknown));
        var u=unknownDoc.RootElement.GetProperty("summary");
        C("unknown-is-sentinel",u.GetProperty("ai_requests").GetDouble()==-1&&u.GetProperty("gateway_requests").GetDouble()==-1);
        C("uart-budget",Encoding.UTF8.GetByteCount(wire)<3000);
    }
}
