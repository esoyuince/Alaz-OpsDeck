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
        C("request-manual",
            PanelFanControlRequest.TryParse("opsdeck.ui: FAN_CONTROL_REQUEST mode=manual speed=70 request=44",out var manualRequest)&&
            manualRequest==new PanelFanControlRequest(OmenFanMode.Manual,44,70));
        C("request-manual-min",
            PanelFanControlRequest.TryParse("I (12345) opsdeck.ui: FAN_CONTROL_REQUEST mode=manual speed=50 request=45",out var manualMin)&&manualMin?.SpeedPercent==50);
        C("request-manual-max",
            PanelFanControlRequest.TryParse("opsdeck.ui: FAN_CONTROL_REQUEST mode=manual speed=100 request=46",out var manualMax)&&manualMax?.SpeedPercent==100);
        C("request-manual-missing-speed-rejected",
            !PanelFanControlRequest.TryParse("opsdeck.ui: FAN_CONTROL_REQUEST mode=manual request=47",out _));
        C("request-manual-step-rejected",
            !PanelFanControlRequest.TryParse("opsdeck.ui: FAN_CONTROL_REQUEST mode=manual speed=73 request=48",out _));
        C("request-auto-speed-rejected",
            !PanelFanControlRequest.TryParse("opsdeck.ui: FAN_CONTROL_REQUEST mode=auto speed=70 request=49",out _));
        C("request-zero-rejected",
            !PanelFanControlRequest.TryParse("opsdeck.ui: FAN_CONTROL_REQUEST mode=max request=0",out _));

        const string autoJson="{\"schema\":\"opsdeck.omen-fan-control.v1\",\"supported\":true,\"manual_supported\":true,\"mode\":\"auto\",\"mode_id\":1,\"fan_speed_pct\":70,\"max_fan\":false,\"action\":\"status\",\"vendor_version\":\"1101.2609.3.0\"}";
        var snap=OmenFanControl.ParseOutput(autoJson,DateTimeOffset.UnixEpoch);
        C("parse-auto",snap.Supported&&snap.ManualSupported&&snap.Mode==OmenFanMode.Auto&&snap.SpeedPercent==70&&!snap.Busy);

        var manual=OmenFanControl.ParseOutput(autoJson.Replace("\"auto\"","\"manual\""),DateTimeOffset.UnixEpoch);
        C("parse-manual",manual.Mode==OmenFanMode.Manual&&manual.ManualSupported&&manual.SpeedPercent==70&&manual.Status.Contains("70%"));

        using var doc=JsonDocument.Parse(new PcSample(Fan1Rpm:3200,Fan2Rpm:3300).Wire(snap));
        var root=doc.RootElement;
        C("wire-state",
            root.GetProperty("fan_control_supported").GetInt32()==1&&
            root.GetProperty("fan_manual_supported").GetInt32()==1&&
            root.GetProperty("fan_control_mode").GetInt32()==1&&
            root.GetProperty("fan_control_speed_pct").GetInt32()==70&&
            root.GetProperty("fan_control_busy").GetInt32()==0);

        using var legacy=JsonDocument.Parse(new PcSample(Cpu:1).Wire());
        C("wire-backcompat",!legacy.RootElement.TryGetProperty("fan_control_mode",out _));
    }
}
