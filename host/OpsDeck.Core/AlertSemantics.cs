using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public enum AlertLevel { Info=0, Attention=1, Recovering=2, Degraded=3 }
public sealed record AlertItem(AlertLevel Level,string Code,string Source,string Text)
{
    public void Validate()
    {
        if(Level==AlertLevel.Info||!Enum.IsDefined(Level))throw new ArgumentException("Active alert must require attention.");
        if(!Regex.IsMatch(Code,"^[A-Z0-9_]{1,32}$")||string.IsNullOrWhiteSpace(Source)||Source.Length>32||Source.Any(char.IsControl)||
           string.IsNullOrWhiteSpace(Text)||Text.Length>160||Text.Any(char.IsControl))throw new ArgumentException("Invalid alert item.");
    }
}
public sealed record AlertRollup(AlertLevel Level,AlertItem[] Items)
{
    public string Primary=>Items.FirstOrDefault()?.Text??"No active alerts";
    public void Validate()
    {
        if(!Enum.IsDefined(Level)||Items.Length>16)throw new ArgumentException("Invalid alert rollup.");foreach(var i in Items)i.Validate();
        var expected=Items.Length==0?AlertLevel.Info:Items.Max(x=>x.Level);if(Level!=expected)throw new ArgumentException("Alert level does not match items.");
    }
}

public static class AlertSemantics
{
    public const double DiskAttentionPercent=90d,DiskDegradedPercent=98d;
    public static string Label(AlertLevel level)=>level switch{AlertLevel.Attention=>"ATTENTION",AlertLevel.Recovering=>"RECOVERING",AlertLevel.Degraded=>"DEGRADED",_=>"INFO"};
    public static AlertLevel LinkLevel(LinkHealthSnapshot link)=>link.Recovering?AlertLevel.Recovering:link.State switch{
        SourceState.Error or SourceState.Denied=>AlertLevel.Degraded,SourceState.Partial or SourceState.Stale=>AlertLevel.Attention,_=>AlertLevel.Info};
    public static AlertLevel VolumeLevel(VolumeReading v,bool pcFresh,DateTimeOffset now)
    {
        if(!pcFresh||v.State(now)!=SourceState.Ok||!v.UsedPercent.HasValue)return AlertLevel.Info;
        return v.UsedPercent.Value>=DiskDegradedPercent?AlertLevel.Degraded:v.UsedPercent.Value>=DiskAttentionPercent?AlertLevel.Attention:AlertLevel.Info;
    }
    public static AlertRollup Evaluate(OperationalAssessment assessment,LinkHealthSnapshot link,PcSample pc,bool pcFresh,PanelHealthSnapshot panel,DateTimeOffset now)
    {
        var items=new List<AlertItem>();
        foreach(var f in assessment.Findings)
        {
            if(f.Code=="LINK_RECOVERING"&&link.Recovering)continue;
            var level=f.Severity==OperationalSeverity.Error?AlertLevel.Degraded:AlertLevel.Attention;
            items.Add(new(level,f.Code,"OPS",f.Text));
        }
        if(link.Recovering)items.Add(new(AlertLevel.Recovering,"LINK_RECOVERING_ACTIVE","PANEL LINK",
            $"Automatic panel link recovery is active: {LinkHealthSnapshot.ActionLabel(link.LastAction)} / {LinkHealthSnapshot.ReasonLabel(link.LastReason)}"));
        foreach(var v in pc.Volumes??[])
        {
            var level=VolumeLevel(v,pcFresh,now);if(level==AlertLevel.Info)continue;
            double used=v.UsedPercent!.Value;string code="DISK_"+v.Id[0]+(level==AlertLevel.Degraded?"_DEGRADED":"_ATTENTION");
            items.Add(new(level,code,"VOLUME "+v.Id,$"{v.Id} volume is {used:0.0}% used; warning-only storage signal"));
        }
        if(panel.Present&&panel.SdState==3)items.Add(new(AlertLevel.Attention,"SD_CARD_ERROR","DECK SD","Deck SD mount reported ERROR; SD storage is optional and controls are unaffected"));
        items=items.GroupBy(x=>x.Code,StringComparer.Ordinal).Select(g=>g.OrderByDescending(x=>x.Level).First()).
            OrderByDescending(x=>x.Level).ThenBy(x=>x.Code,StringComparer.Ordinal).Take(16).ToList();
        var result=new AlertRollup(items.Count==0?AlertLevel.Info:items.Max(x=>x.Level),items.ToArray());result.Validate();return result;
    }
}
