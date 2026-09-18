using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;

public static class M62CodexTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m62-"+n,ok);
        void Reject(string n,Action a){try{a();C(n,false);}catch(Exception e)when(e is InvalidDataException or ArgumentException){C(n,true);}}
        var now=new DateTimeOffset(2026,9,16,0,15,0,TimeSpan.FromHours(3));
        long reset=now.AddHours(5).ToUnixTimeSeconds();
        using var rateDoc=JsonDocument.Parse(Rate(reset));using var usageDoc=JsonDocument.Parse(Usage(now));
        var sample=CodexTelemetryParser.Parse(rateDoc.RootElement,usageDoc.RootElement,now,"0.154-test");
        C("state",sample.State==SourceState.Ok&&sample.PlanType=="pro"&&sample.Version=="0.154-test");
        C("tokens",sample.LifetimeTokens==123456789&&sample.TodayTokens==987654&&sample.PeakDailyTokens==2000000);
        C("streak",sample.LongestTurnSeconds==3600&&sample.CurrentStreakDays==7&&sample.LongestStreakDays==12);
        C("ordinary-usage",sample.OrdinaryUsageAllowed==true);
        C("quota-count",sample.EffectiveQuotas.Length==2);
        var main=sample.EffectiveQuotas.Single(x=>x.Id=="codex");
        C("quota-main",main.Primary?.UsedPercent==70&&main.Primary.RemainingPercent==30&&main.Primary.WindowMinutes==10080);
        C("quota-reset",main.Primary?.ResetsAt==DateTimeOffset.FromUnixTimeSeconds(reset));
        var spark=sample.EffectiveQuotas.Single(x=>x.Id=="codex_bengalfox");
        C("quota-secondary",spark.Primary?.UsedPercent==5&&spark.Secondary?.UsedPercent==12&&spark.Name=="GPT-5.3-Codex-Spark");
        string panelWire=FleetState.Empty.Wire(now,false,null,sample);
        using(var panelDoc=JsonDocument.Parse(panelWire))
        {
            var a=panelDoc.RootElement.GetProperty("agents");
            C("panel-wire-quota",a.GetProperty("codex_usage_state").GetInt32()==1&&a.GetProperty("codex_quota_used").GetInt32()==70&&a.GetProperty("codex_quota_remaining").GetInt32()==30&&a.GetProperty("codex_quota_window_m").GetInt32()==10080);
            C("panel-wire-spark",a.GetProperty("codex_spark_used").GetInt32()==5&&a.GetProperty("codex_spark_remaining").GetInt32()==95&&a.GetProperty("codex_spark_window_m").GetInt32()==300);
        }
        C("panel-wire-budget",System.Text.Encoding.UTF8.GetByteCount(panelWire)<3000);

        var cacheNow=DateTimeOffset.UtcNow;
        var cacheReset=cacheNow.AddHours(3);
        var cacheSample=sample with{
            CollectedAt=cacheNow,
            Quotas=[new CodexQuota("codex","Codex","pro",new CodexQuotaWindow(71,10080,cacheReset),null)]
        };
        string cacheDir=Path.Combine(Path.GetTempPath(),"opsdeck-m62-cache-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);
        try
        {
            var cache=new CodexUsageCache(cacheDir);cache.Save(cacheSample);
            var restored=cache.Load(cacheNow.AddMinutes(5));
            C("cache-restores-stale",restored?.State==SourceState.Stale&&restored.CollectedAt==cacheNow&&restored.EffectiveQuotas.Single().Primary?.UsedPercent==71);
            var failed=new CodexUsageSnapshot(SourceState.Error,cacheNow.AddMinutes(5),Detail:"transient");
            var fallback=CodexUsageCache.Fallback(restored,failed,cacheNow.AddMinutes(5));
            C("failure-retains-quota",fallback?.State==SourceState.Stale&&fallback.EffectiveQuotas.Single().Primary?.RemainingPercent==29);
            using var staleWire=JsonDocument.Parse(FleetState.Empty.Wire(cacheNow.AddMinutes(5),false,null,fallback));
            var staleAgents=staleWire.RootElement.GetProperty("agents");
            C("stale-wire-keeps-quota",staleAgents.GetProperty("codex_usage_state").GetInt32()==3&&staleAgents.GetProperty("codex_quota_used").GetInt32()==71);
            C("cache-expires",cache.Load(cacheNow.Add(CodexUsageCache.MaxAge).AddSeconds(1))==null);
            var expiredReset=cacheSample with{Quotas=[new CodexQuota("codex","Codex","pro",new CodexQuotaWindow(71,10080,cacheNow.AddSeconds(-1)),null)]};
            using var expiredWire=JsonDocument.Parse(FleetState.Empty.Wire(cacheNow,false,null,expiredReset));
            C("expired-reset-hidden",!expiredWire.RootElement.GetProperty("agents").TryGetProperty("codex_quota_reset_local",out _));
            C("retry-shorter-than-sample",CodexTelemetrySampler.RetrySeconds<CodexTelemetrySampler.SampleSeconds&&CodexTelemetrySampler.RetrySeconds==30);
        }
        finally{Directory.Delete(cacheDir,true);}

        using(var lockedDoc=JsonDocument.Parse(FleetState.Empty.Wire(now,true,null,sample)))
        {
            var a=lockedDoc.RootElement.GetProperty("agents");C("panel-wire-lock-hides-quota",a.GetProperty("codex_quota_used").GetInt32()==-1&&a.GetProperty("codex_spark_used").GetInt32()==-1&&a.GetProperty("codex_usage_state").GetInt32()==4);
        }
        C("account-id-not-retained",!sample.ToString().Contains("secret-account",StringComparison.OrdinalIgnoreCase));
        var detect=typeof(CodexTelemetrySampler).GetMethod("IsBridgeIsolatedCodexHome",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
        bool DetectHome(string value)=>detect!=null&&(bool)detect.Invoke(null,new object?[]{value})!;
        C("bridge-home-detected",DetectHome(@"C:\Antigravity\chatgpt-codex-mcp-bridge\data\native\00000000-0000-0000-0000-000000000000"));
        C("normal-home-preserved",!DetectHome(@"C:\Users\ender\.codex"));
        var startInfo=typeof(CodexTelemetrySampler).GetMethod("AppServerStartInfo",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
        string? oldCodexHome=Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME",@"C:\Antigravity\chatgpt-codex-mcp-bridge\data\native\00000000-0000-0000-0000-000000000000");
            var isolatedPsi=startInfo?.Invoke(null,new object[]{"codex.exe"}) as System.Diagnostics.ProcessStartInfo;
            C("bridge-home-stripped",isolatedPsi!=null&&!isolatedPsi.Environment.ContainsKey("CODEX_HOME"));
            Environment.SetEnvironmentVariable("CODEX_HOME",@"C:\Users\ender\custom-codex-home");
            var normalPsi=startInfo?.Invoke(null,new object[]{"codex.exe"}) as System.Diagnostics.ProcessStartInfo;
            C("normal-home-kept",normalPsi!=null&&normalPsi.Environment.TryGetValue("CODEX_HOME",out var kept)&&kept==@"C:\Users\ender\custom-codex-home");
        }
        finally{Environment.SetEnvironmentVariable("CODEX_HOME",oldCodexHome);}
        C("no-content-detail",sample.Detail.Contains("no prompt or response content",StringComparison.OrdinalIgnoreCase));
        C("aux-node",AgentSampler.IsAuxiliaryCodexParent("node"));
        C("aux-opsdeck",AgentSampler.IsAuxiliaryCodexParent("OpsDeck.Host"));
        C("desktop-parent-kept",!AgentSampler.IsAuxiliaryCodexParent("ChatGPT"));
        C("cli-parent-kept",!AgentSampler.IsAuxiliaryCodexParent("powershell"));
        C("unknown-parent-kept",!AgentSampler.IsAuxiliaryCodexParent(null));
        using(var noToday=JsonDocument.Parse(Usage(now.AddDays(-1))))
            C("no-today-zero",CodexTelemetryParser.Parse(rateDoc.RootElement,noToday.RootElement,now).TodayTokens==0);
        using(var bad=JsonDocument.Parse(Rate(reset).Replace("\"usedPercent\":70","\"usedPercent\":101")))
            Reject("percent-bound",()=>CodexTelemetryParser.Parse(bad.RootElement,usageDoc.RootElement,now));
        using(var bad=JsonDocument.Parse(Usage(now).Replace("123456789","-1")))
            Reject("negative-token",()=>CodexTelemetryParser.Parse(rateDoc.RootElement,bad.RootElement,now));
        using(var bad=JsonDocument.Parse("{\"id\":2,\"error\":{\"message\":\"x\"}}"))
            Reject("error-response",()=>CodexTelemetryParser.Parse(bad.RootElement,usageDoc.RootElement,now));

        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m62-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            string oldDir=Path.Combine(dir,"old"),newDir=Path.Combine(dir,"new");Directory.CreateDirectory(oldDir);Directory.CreateDirectory(newDir);
            string old=Path.Combine(oldDir,"codex.exe"),newer=Path.Combine(newDir,"codex.exe");File.WriteAllBytes(old,[]);File.WriteAllBytes(newer,[]);
            File.SetLastWriteTimeUtc(old,DateTime.UtcNow.AddDays(-1));File.SetLastWriteTimeUtc(newer,DateTime.UtcNow);
            C("resolver-newest",string.Equals(CodexTelemetrySampler.ResolveExecutable(dir),newer,StringComparison.OrdinalIgnoreCase));
            C("resolver-missing",CodexTelemetrySampler.ResolveExecutable(Path.Combine(dir,"missing"))==null);
        }
        finally{Directory.Delete(dir,true);}
    }

    public static async Task<int> Live()
    {
        var cfg=new LocalSettings().Load();var sample=await new CodexTelemetrySampler(cfg).SampleAsync(CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new{sample.State,sample.CollectedAt,sample.Version,sample.PlanType,sample.LifetimeTokens,sample.TodayTokens,sample.PeakDailyTokens,sample.LongestTurnSeconds,sample.CurrentStreakDays,sample.LongestStreakDays,sample.OrdinaryUsageAllowed,quotas=sample.EffectiveQuotas.Select(q=>new{q.Id,q.Name,q.PlanType,q.Primary,q.Secondary}),sample.Detail},Json.Options));
        return sample.State==SourceState.Ok?0:1;
    }
    private static string Rate(long reset)
    {
        object Window(int used,long mins)=>new{usedPercent=used,windowDurationMins=mins,resetsAt=reset};
        var main=new{limitId="codex",planType="pro",primary=Window(70,10080)};
        var spark=new{limitId="codex_bengalfox",limitName="GPT-5.3-Codex-Spark",planType="pro",primary=Window(5,300),secondary=Window(12,10080)};
        return JsonSerializer.Serialize(new{id=2,result=new{ordinaryUsageAllowed=true,accountId="secret-account",rateLimits=main,rateLimitsByLimitId=new Dictionary<string,object>{{"codex",main},{"codex_bengalfox",spark}}}});
    }
    private static string Usage(DateTimeOffset date)=>JsonSerializer.Serialize(new{id=3,result=new{summary=new{lifetimeTokens=123456789L,peakDailyTokens=2000000L,longestRunningTurnSec=3600L,currentStreakDays=7L,longestStreakDays=12L},dailyUsageBuckets=new[]{new{startDate=date.ToString("yyyy-MM-dd"),tokens=987654L}},threadUsage=(object?)null}});
}