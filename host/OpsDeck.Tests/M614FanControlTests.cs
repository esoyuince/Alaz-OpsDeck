using System.Text.Json;
using OpsDeck.Core;

namespace OpsDeck.Tests;

public static class M614FanControlTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m614-fan-"+n,ok);

        C("request-auto",
            PanelFanControlRequest.TryParse("opsdeck.ui: FAN_CONTROL_REQUEST mode=auto request=42",out var autoRequest)&&
            autoRequest==new PanelFanControlRequest(OmenFanMode.Auto,42));
        C("request-max",
            PanelFanControlRequest.TryParse("opsdeck.ui: FAN_CONTROL_REQUEST mode=max request=43",out var maxRequest)&&
            maxRequest==new PanelFanControlRequest(OmenFanMode.Max,43));
        C("request-esp-prefix",
            PanelFanControlRequest.TryParse("I (12345) opsdeck.ui: FAN_CONTROL_REQUEST mode=max request=44",out var prefixed)&&
            prefixed==new PanelFanControlRequest(OmenFanMode.Max,44));
        C("request-manual-rejected",
            !PanelFanControlRequest.TryParse("opsdeck.ui: FAN_CONTROL_REQUEST mode=manual request=44",out _));
        C("request-zero-rejected",
            !PanelFanControlRequest.TryParse("opsdeck.ui: FAN_CONTROL_REQUEST mode=max request=0",out _));

        const string autoJson="{\"schema\":\"opsdeck.omen-fan-control.v1\",\"supported\":true,\"mode\":\"auto\",\"mode_id\":1,\"fan_speed_pct\":100,\"max_fan\":false,\"action\":\"status\",\"vendor_version\":\"1101.2609.3.0\"}";
        var snap=OmenFanControl.ParseOutput(autoJson,DateTimeOffset.UnixEpoch);
        C("parse-auto",snap.Supported&&!snap.ManualSupported&&snap.Mode==OmenFanMode.Auto&&!snap.Busy);

        var manual=OmenFanControl.ParseOutput(autoJson.Replace("\"auto\"","\"manual\""),DateTimeOffset.UnixEpoch);
        C("external-manual-observable-disabled",manual.Mode==OmenFanMode.Manual&&!manual.ManualSupported);

        using var doc=JsonDocument.Parse(new PcSample(Fan1Rpm:3200,Fan2Rpm:3300).Wire(snap));
        var root=doc.RootElement;
        C("wire-state",
            root.GetProperty("fan_control_supported").GetInt32()==1&&
            root.GetProperty("fan_manual_supported").GetInt32()==0&&
            root.GetProperty("fan_control_mode").GetInt32()==1&&
            root.GetProperty("fan_control_busy").GetInt32()==0);

        using var legacy=JsonDocument.Parse(new PcSample(Cpu:1).Wire());
        C("wire-backcompat",!legacy.RootElement.TryGetProperty("fan_control_mode",out _));
    }
}
