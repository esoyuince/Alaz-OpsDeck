using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;
public enum SourceState { Setup=0, Ok=1, Error=2, Stale=3, NoData=4, Partial=5, Denied=6 }
public sealed record Metric(SourceState State,double? Value=null,double? Secondary=null,DateTimeOffset? CollectedAt=null,DateTimeOffset? SourceEnd=null,string Unit="",string Detail="",DateTimeOffset? PeriodStart=null,DateTimeOffset? PeriodEnd=null,int Covered=1,int Expected=1,string Note="",bool UsageCharges=false,bool LastRead=false)
{
    public static Metric Setup(string detail="Not configured")=>new(SourceState.Setup,Detail:detail,Covered:0);
    public Metric Failed(SourceState state,string detail)=>this with{State=state,Detail=detail,Covered=0};
    public object Wire(DateTimeOffset now,int ttl)=>new {
        state=(int)(Freshness.MetricState(this,now,ttl)),
        value=Value,secondary=Secondary,age_s=Freshness.AgeSeconds(CollectedAt,now),unit=Unit,covered=Covered,expected=Expected,note=MetricPresentation.Note(this,now,ttl),period_start=MetricPresentation.Date(PeriodStart),period_end=MetricPresentation.Date(PeriodEnd),source_end=MetricPresentation.Date(SourceEnd)
    };
}
public sealed record PcSample(double? Cpu=null,double? Gpu=null,double? GpuTemp=null,double? RamUsedGib=null,double? RamTotalGib=null,double? VramUsedGib=null,double? VramTotalGib=null,double? RxMbps=null,double? TxMbps=null,double? IntelGpu=null,double? IntelTemp=null,double? IntelSharedGib=null,double? IntelSharedLimitGib=null,double? Fan1Rpm=null,double? Fan2Rpm=null,string IntelSensorStatus="unavailable",string FanSensorStatus="unavailable",string NvidiaSensorStatus="unavailable",GpuReading[]? Gpus=null,double? CpuTemp=null,string CpuSensorStatus="No verified CPU sensor provider; no elevation or driver installation",VolumeReading[]? Volumes=null,double? ChassisTemp=null,string ChassisSensorStatus="No verified chassis sensor provider")
{
    public string Wire(OmenFanControlSnapshot? fanControl=null)=>JsonSerializer.Serialize(new {type="opsdeck.pc.v1",cpu=Cpu,cpu_temp=CpuTemp,cpu_sensor=CpuTemp.HasValue?1:4,chassis_temp=ChassisTemp,chassis_sensor=ChassisTemp.HasValue?1:4,fan_sensor=Fan1Rpm.HasValue||Fan2Rpm.HasValue?1:4,fan_control_supported=fanControl is null?null:(int?)(fanControl.Supported?1:0),fan_manual_supported=fanControl is null?null:(int?)(fanControl.ManualSupported?1:0),fan_control_mode=fanControl is null?null:(int?)fanControl.Mode,fan_control_busy=fanControl is null?null:(int?)(fanControl.Busy?1:0),volumes=(Volumes??[]).Take(DiskTelemetry.PanelLimit).Select(v=>v.Wire(DateTimeOffset.UtcNow)),volume_count=(Volumes??[]).Length,gpu=Gpu,gpu_temp=GpuTemp,intel_gpu=IntelGpu,intel_temp=IntelTemp,intel_shared_gib=IntelSharedGib,intel_shared_limit_gib=IntelSharedLimitGib,fan1_rpm=Fan1Rpm,fan2_rpm=Fan2Rpm,ram_used_gib=RamUsedGib,ram_total_gib=RamTotalGib,vram_used_gib=VramUsedGib,vram_total_gib=VramTotalGib,rx_mbps=RxMbps,tx_mbps=TxMbps},Json.Options);
}
public sealed record AgentSample(int CodexCount=-1,SourceState CodexState=SourceState.NoData,SourceState BridgeState=SourceState.Setup,DateTimeOffset? ObservedAt=null,string Detail="Process presence only",BridgeTaskSample? Tasks=null,DesktopCommanderSnapshot? RemoteDesktop=null);
public sealed record FleetState(AgentSample Agents,Metric Workers,Metric D1,Metric R2,Metric Hosting,Metric Cost)
{
    public static FleetState Empty=>new(new(),Metric.Setup(),Metric.Setup(),Metric.Setup(),Metric.Setup(),Metric.Setup());
    public string Wire(DateTimeOffset now,bool locked,LinkHealthSnapshot? link=null,CodexUsageSnapshot? codexUsage=null,ManagedCodexSnapshot? managedCodex=null)
    {
        var t=Agents.Tasks;bool sampleOld=Agents.ObservedAt.HasValue&&now-Agents.ObservedAt>TimeSpan.FromSeconds(15);
        var usage=codexUsage??CodexUsageSnapshot.Setup;var managed=managedCodex??new ManagedCodexSnapshot(SourceState.Setup,Activity:"NOT READY");bool usageOld=usage.CollectedAt.HasValue&&now-usage.CollectedAt.Value>TimeSpan.FromMinutes(10);
        var quotaState=locked?SourceState.NoData:usageOld?SourceState.Stale:usage.State;var quotas=usage.EffectiveQuotas;
        var main=quotas.FirstOrDefault(q=>string.Equals(q.Id,"codex",StringComparison.OrdinalIgnoreCase))??quotas.FirstOrDefault();
        var spark=quotas.FirstOrDefault(q=>q.Id.Contains("bengalfox",StringComparison.OrdinalIgnoreCase)||q.Name.Contains("Spark",StringComparison.OrdinalIgnoreCase));
        int Used(CodexQuotaWindow? w)=>w?.UsedPercent??-1;int Left(CodexQuotaWindow? w)=>w?.RemainingPercent??-1;
        int Window(CodexQuotaWindow? w)=>w?.WindowMinutes is long m?(int)Math.Clamp(m,0,525600):0;int Counter(long? v)=>v.HasValue?(int)Math.Clamp(v.Value,0,2_000_000_000):-1;
        string? Reset(CodexQuota? q){DateTimeOffset? p=q?.Primary?.ResetsAt,sec=q?.Secondary?.ResetsAt;DateTimeOffset? at=p.HasValue&&sec.HasValue?(p.Value<=sec.Value?p:sec):(p??sec);return at?.ToLocalTime().ToString("dd.MM HH:mm");}
        return JsonSerializer.Serialize(new {type="opsdeck.status.v1",locked,agents=new{
            codex_count=locked?-1:Agents.CodexCount,
            codex_state=(int)(locked?SourceState.NoData:sampleOld?SourceState.Stale:Agents.CodexState),
            bridge_state=(int)(locked?SourceState.NoData:sampleOld?SourceState.Stale:Agents.BridgeState),
            rdc_state=(int)(locked?SourceState.NoData:sampleOld?SourceState.Stale:Agents.RemoteDesktop?.State??SourceState.Setup),
            rdc_process_count=locked?-1:Agents.RemoteDesktop?.ProcessCount??-1,rdc_total_calls=locked?-1:Counter(Agents.RemoteDesktop?.RunCalls??Agents.RemoteDesktop?.TotalCalls),
            rdc_sessions=locked?-1:Counter(Agents.RemoteDesktop?.RunSessions??Agents.RemoteDesktop?.Sessions),rdc_success_permille=locked?-1:Agents.RemoteDesktop?.SuccessPermille??-1,
            rdc_last_action_age_s=locked?-1:Freshness.AgeSeconds(Agents.RemoteDesktop?.LastActionAt,now),
            managed_codex_state=(int)(locked?SourceState.NoData:managed.State),managed_codex_owned=locked?-1:Math.Clamp(managed.OwnedThreads,0,1024),
            managed_codex_running=!locked&&string.Equals(managed.Activity,"RUNNING",StringComparison.Ordinal),managed_codex_runtime=!locked&&managed.RuntimeRunning,
            managed_codex_reconcile=!locked&&managed.ReconciliationRequired,
            codex_usage_state=(int)quotaState,codex_usage_age_s=locked?-1:Freshness.AgeSeconds(usage.CollectedAt,now),
            codex_quota_used=locked?-1:Used(main?.Primary),codex_quota_remaining=locked?-1:Left(main?.Primary),codex_quota_window_m=locked?0:Window(main?.Primary),codex_quota_reset_local=locked?null:Reset(main),
            codex_spark_used=locked?-1:Used(spark?.Primary),codex_spark_remaining=locked?-1:Left(spark?.Primary),codex_spark_window_m=locked?0:Window(spark?.Primary),codex_spark_reset_local=locked?null:Reset(spark),
            task_state=(int)(locked?SourceState.NoData:sampleOld?SourceState.Stale:t?.State??SourceState.Setup),
            task_open=locked?-1:t?.Open??-1,task_claimed=locked?-1:t?.Claimed??-1,
            task_in_progress=locked?-1:t?.InProgress??-1,task_needs_approval=locked?-1:t?.NeedsApproval??-1,
            task_blocked=locked?-1:t?.Blocked??-1,task_expired_claims=locked?-1:t?.ExpiredClaims??-1,
            task_latest_event=locked?null:(t?.LatestEvent.Length>0?t.LatestEvent:null),
            task_latest_age_s=locked?-1:Freshness.AgeSeconds(t?.LatestTaskAt,now)
        },link=link?.Wire(now),workers=Workers.Wire(now,180),d1=D1.Wire(now,180),r2=R2.Wire(now,900),hosting=Hosting.Wire(now,180),cost=Cost.Wire(now,7200)},Json.Options);
    }
}
public static class Json
{
    public static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,WriteIndented=false,DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull};
}
public sealed record HostConfig
{
    public int SchemaVersion{get;init;}=3;
    public string Port{get;init;}="COM9";
    public string NetworkInterface{get;init;}="Wi-Fi";
    public bool EdgeNodeEnabled{get;init;}=false;
    public string EdgeNodeStatusUrl{get;init;}="";
    public uint GpuIndex{get;init;}=0;
    public bool DetailedNvidiaSensors{get;init;}=false;
    public string CodexDirectory{get;init;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenAI","Codex","bin");
    public string BridgeHealthUrl{get;init;}="";
    public string BridgeTaskDatabasePath{get;init;}="";
    public string CloudflareAccountId{get;init;}="";
    public bool CloudflareEnabled{get;init;}=false;
    public bool BillingEnabled{get;init;}=false;
    public CloudAccountConfig[] Accounts{get;init;}=[];
    public string[] Sites{get;init;}=[];
    public CloudAccountConfig[] EffectiveAccounts()=>Accounts.Length>0?Accounts:CloudflareAccountId.Length>0?[new(){ProfileId="legacy",Name="Mevcut Hesap",AccountId=CloudflareAccountId,Enabled=CloudflareEnabled,BillingEnabled=BillingEnabled},CloudAccountConfig.Defaults[1]]:CloudAccountConfig.Defaults;
    public void Validate()
    {
        if(SchemaVersion>3||SchemaVersion<1)throw new ArgumentException("Desteklenmeyen ayar sÃ¼rÃ¼mÃ¼.");
        if(!Regex.IsMatch(Port,@"^COM[1-9][0-9]{0,3}$"))throw new ArgumentException("COM port is invalid.");
        if(!Path.IsPathFullyQualified(CodexDirectory))throw new ArgumentException("Codex directory must be absolute.");
        if(CloudflareEnabled&&!Regex.IsMatch(CloudflareAccountId,@"^[a-fA-F0-9]{32}$"))throw new ArgumentException("Cloudflare account ID must contain 32 hexadecimal characters.");
        var accounts=EffectiveAccounts();if(accounts.Length>2)throw new ArgumentException("M3 sÃ¼rÃ¼mÃ¼ en fazla iki hesap profili destekler.");
        foreach(var account in accounts)account.Validate();
        if(accounts.Select(a=>a.ProfileId).Distinct().Count()!=accounts.Length)throw new ArgumentException("Profil kimlikleri tekrarlanamaz.");
        var ids=accounts.Where(a=>a.AccountId.Length>0).Select(a=>a.AccountId);
        if(ids.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=ids.Count())throw new ArgumentException("AynÄ± Cloudflare hesabÄ± iki kez eklenemez.");
        if(BridgeHealthUrl.Length>0)ValidateBridge(BridgeHealthUrl);
        if(BridgeTaskDatabasePath.Length>0)ValidateBridgeTaskDatabase(BridgeTaskDatabasePath);
        if(EdgeNodeEnabled&&EdgeNodeStatusUrl.Length==0)throw new ArgumentException("EdgeNode status URL is required when EdgeNode is enabled.");
        if(EdgeNodeStatusUrl.Length>0)EdgeNodeEndpoint.Validate(EdgeNodeStatusUrl);
        if(Sites.Length>12)throw new ArgumentException("At most 12 public HTTPS health URLs are allowed.");foreach(var site in Sites)ValidateSite(site);
    }
    public static Uri ValidateBridge(string url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var u)||u.Scheme!="http"||u.Host!="127.0.0.1"||u.UserInfo!=""||u.Query!=""||u.Fragment!=""||u.AbsolutePath!="/health")throw new ArgumentException("Bridge must be http://127.0.0.1:<port>/health; no credentials.");return u;
    }
    public static string ValidateBridgeTaskDatabase(string path)
    {
        if(path.Length>1024||path.StartsWith(@"\\",StringComparison.Ordinal)||path.Any(char.IsControl)||!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Bridge task database must be an absolute local path.");
        string ext=Path.GetExtension(path);if(!ext.Equals(".sqlite",StringComparison.OrdinalIgnoreCase)&&!ext.Equals(".db",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Bridge task database must be a .sqlite or .db file.");
        return Path.GetFullPath(path);
    }
    public static Uri ValidateSite(string url)
    {
        if(url.Length>512||!Uri.TryCreate(url,UriKind.Absolute,out var u)||u.Scheme!="https"||!u.IsDefaultPort||u.UserInfo!=""||u.Query!=""||u.Fragment!=""||u.IsLoopback)throw new ArgumentException("Sites must be public HTTPS URLs on port 443 without credentials or query strings.");return u;
    }
}
