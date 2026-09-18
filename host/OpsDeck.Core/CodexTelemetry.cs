using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace OpsDeck.Core;

public sealed record CodexQuotaWindow(int UsedPercent,long? WindowMinutes,DateTimeOffset? ResetsAt)
{
    public int RemainingPercent=>Math.Clamp(100-UsedPercent,0,100);
}
public sealed record CodexQuota(string Id,string Name,string PlanType,CodexQuotaWindow? Primary,CodexQuotaWindow? Secondary);
public sealed record CodexUsageSnapshot(SourceState State,DateTimeOffset? CollectedAt=null,string Version="",
    string PlanType="",long? LifetimeTokens=null,long? TodayTokens=null,long? PeakDailyTokens=null,
    long? LongestTurnSeconds=null,long? CurrentStreakDays=null,long? LongestStreakDays=null,
    bool? OrdinaryUsageAllowed=null,CodexQuota[]? Quotas=null,string Detail="")
{
    public static CodexUsageSnapshot Setup=>new(SourceState.Setup,Detail:"Codex usage has not been sampled yet.");
    public CodexQuota[] EffectiveQuotas=>Quotas??[];
}

public static class CodexTelemetryParser
{
    public static CodexUsageSnapshot Parse(JsonElement rateResponse,JsonElement usageResponse,DateTimeOffset now,string version="")
    {
        var rate=RequireResult(rateResponse,"rate limits");var usage=RequireResult(usageResponse,"usage");
        var quotas=ParseQuotas(rate);var summary=usage.GetProperty("summary");
        long? lifetime=Long(summary,"lifetimeTokens"),peak=Long(summary,"peakDailyTokens");
        long? longest=Long(summary,"longestRunningTurnSec"),streak=Long(summary,"currentStreakDays"),longestStreak=Long(summary,"longestStreakDays");
        long? today=TodayTokens(usage,now);string plan=quotas.Select(q=>q.PlanType).FirstOrDefault(x=>x.Length>0)??"";
        bool? ordinary=rate.TryGetProperty("ordinaryUsageAllowed",out var allowed)&&allowed.ValueKind is JsonValueKind.True or JsonValueKind.False?allowed.GetBoolean():null;
        return new(SourceState.Ok,now,version,plan,lifetime,today,peak,longest,streak,longestStreak,ordinary,quotas,
            "Direct read from the local Codex app-server account usage and rate-limit APIs; no prompt or response content is read.");
    }
    private static JsonElement RequireResult(JsonElement root,string label)
    {
        if(root.ValueKind!=JsonValueKind.Object||root.TryGetProperty("error",out _)||!root.TryGetProperty("result",out var result)||result.ValueKind!=JsonValueKind.Object)
            throw new InvalidDataException("Invalid Codex "+label+" response.");
        return result;
    }
    private static long? Long(JsonElement o,string name)
    {
        if(!o.TryGetProperty(name,out var v)||v.ValueKind==JsonValueKind.Null)return null;
        if(v.ValueKind!=JsonValueKind.Number||!v.TryGetInt64(out long n)||n<0)throw new InvalidDataException("Invalid Codex usage counter.");
        return n;
    }
    private static long? TodayTokens(JsonElement usage,DateTimeOffset now)
    {
        if(!usage.TryGetProperty("dailyUsageBuckets",out var buckets)||buckets.ValueKind==JsonValueKind.Null)return null;
        if(buckets.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Invalid Codex daily usage buckets.");
        string today=now.ToLocalTime().ToString("yyyy-MM-dd");
        foreach(var b in buckets.EnumerateArray())if(b.TryGetProperty("startDate",out var d)&&d.GetString()==today)return Long(b,"tokens");
        return 0;
    }
    private static CodexQuota[] ParseQuotas(JsonElement rate)
    {
        var rows=new List<CodexQuota>();
        if(rate.TryGetProperty("rateLimitsByLimitId",out var map)&&map.ValueKind==JsonValueKind.Object)
            foreach(var p in map.EnumerateObject())rows.Add(ParseQuota(p.Name,p.Value));
        else if(rate.TryGetProperty("rateLimits",out var single)&&single.ValueKind==JsonValueKind.Object)
            rows.Add(ParseQuota(single.TryGetProperty("limitId",out var id)&&id.ValueKind==JsonValueKind.String?id.GetString()??"codex":"codex",single));
        if(rows.Count>8)throw new InvalidDataException("Too many Codex quota buckets.");
        return rows.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray();
    }
    private static CodexQuota ParseQuota(string key,JsonElement q)
    {
        string id=Text(q,"limitId")??key,name=Text(q,"limitName")??id,plan=Text(q,"planType")??"";
        if(id.Length is <1 or >80||name.Length>120||plan.Length>40)throw new InvalidDataException("Invalid Codex quota metadata.");
        return new(id,name,plan,Window(q,"primary"),Window(q,"secondary"));
    }
    private static CodexQuotaWindow? Window(JsonElement q,string name)
    {
        if(!q.TryGetProperty(name,out var w)||w.ValueKind==JsonValueKind.Null)return null;
        if(w.ValueKind!=JsonValueKind.Object||!w.TryGetProperty("usedPercent",out var used)||!used.TryGetInt32(out int pct)||pct<0||pct>100)
            throw new InvalidDataException("Invalid Codex quota window.");
        long? mins=Long(w,"windowDurationMins"),reset=Long(w,"resetsAt");
        DateTimeOffset? at=reset.HasValue?DateTimeOffset.FromUnixTimeSeconds(reset.Value):null;
        return new(pct,mins,at);
    }
    private static string? Text(JsonElement o,string name)=>o.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
}
public sealed class CodexTelemetrySampler(HostConfig config)
{
    public const int SampleSeconds=300;public const int RetrySeconds=30;
    private static readonly UTF8Encoding ProtocolUtf8=new(false,true);
    internal static ProcessStartInfo AppServerStartInfo(string exe)
    {
        var psi=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=ProtocolUtf8,StandardOutputEncoding=ProtocolUtf8,StandardErrorEncoding=ProtocolUtf8};
        if(psi.Environment.TryGetValue("CODEX_HOME",out var codexHome)&&IsBridgeIsolatedCodexHome(codexHome))psi.Environment.Remove("CODEX_HOME");
        psi.ArgumentList.Add("app-server");psi.ArgumentList.Add("--stdio");return psi;
    }
    internal static bool IsBridgeIsolatedCodexHome(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return false;
        try
        {
            string full=Path.GetFullPath(value).Replace(Path.AltDirectorySeparatorChar,Path.DirectorySeparatorChar);
            string sep=Path.DirectorySeparatorChar.ToString();
            string marker=sep+"chatgpt-codex-mcp-bridge"+sep+"data"+sep+"native"+sep;
            return full.Contains(marker,StringComparison.OrdinalIgnoreCase);
        }
        catch(Exception e)when(e is ArgumentException or NotSupportedException or PathTooLongException){return false;}
    }
    public async Task<CodexUsageSnapshot> SampleAsync(CancellationToken external)
    {
        string? exe=ResolveExecutable(config.CodexDirectory);
        if(exe==null)return CodexUsageSnapshot.Setup with{State=SourceState.NoData,CollectedAt=DateTimeOffset.UtcNow,Detail="Codex executable was not found under the configured Codex directory."};
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(external);timeout.CancelAfter(TimeSpan.FromSeconds(15));var ct=timeout.Token;
        Process? process=null;Task<string>? stderrTask=null;
        try
        {
            var psi=AppServerStartInfo(exe);
            process=Process.Start(psi)??throw new IOException("Codex app-server did not start.");
            stderrTask=process.StandardError.ReadToEndAsync();
            var init=await Call(process,1,"initialize",new{clientInfo=new{name="opsdeck",version="m6.2"},capabilities=new{experimentalApi=true}},ct);
            string version=ReadVersion(init);await Send(process,new{method="initialized"},ct);
            var rates=await Call(process,2,"account/rateLimits/read",null,ct);
            var usage=await Call(process,3,"account/usage/read",null,ct);
            return CodexTelemetryParser.Parse(rates,usage,DateTimeOffset.UtcNow,version);
        }
        catch(OperationCanceledException)when(external.IsCancellationRequested){throw;}
        catch(Exception e)when(e is IOException or InvalidDataException or JsonException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(SourceState.Error,DateTimeOffset.UtcNow,Detail:"Codex app-server usage read failed: "+e.GetType().Name);
        }
        finally
        {
            if(process!=null)
            {
                try{process.StandardInput.Close();}catch{}
                try{if(!process.HasExited){using var grace=new CancellationTokenSource(TimeSpan.FromSeconds(2));await process.WaitForExitAsync(grace.Token);}}catch(OperationCanceledException){}
                try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){}
                process.Dispose();
            }
            if(stderrTask!=null)try{await stderrTask.WaitAsync(TimeSpan.FromSeconds(1));}catch(Exception e)when(e is TimeoutException or ObjectDisposedException){}
        }
    }
    public static string? ResolveExecutable(string root)
    {
        if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||!Directory.Exists(root))return null;
        string full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),prefix=full+Path.DirectorySeparatorChar;
        try{return Directory.EnumerateFiles(full,"codex.exe",SearchOption.AllDirectories).Where(p=>Path.GetFullPath(p).StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return null;}
    }
    private static async Task<JsonElement> Call(Process process,int id,string method,object? parameters,CancellationToken ct)
    {
        var message=new Dictionary<string,object?>{{"id",id},{"method",method}};if(parameters!=null)message["params"]=parameters;
        await Send(process,message,ct);
        while(true)
        {
            string? line=await process.StandardOutput.ReadLineAsync(ct);if(line==null)throw new IOException("Codex app-server closed its output.");
            if(line.Length>2*1024*1024)throw new InvalidDataException("Codex app-server response exceeded the safety bound.");
            using var doc=JsonDocument.Parse(line);var root=doc.RootElement;
            if(root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("id",out var responseId)&&responseId.ValueKind==JsonValueKind.Number&&responseId.TryGetInt32(out int value)&&value==id)
                return root.Clone();
        }
    }
    private static async Task Send(Process process,object message,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();string json=JsonSerializer.Serialize(message);await process.StandardInput.WriteLineAsync(json);await process.StandardInput.FlushAsync(ct);
    }
    private static string ReadVersion(JsonElement init)
    {
        try
        {
            string ua=init.GetProperty("result").GetProperty("userAgent").GetString()??"";int slash=ua.IndexOf('/');if(slash<0)return "";
            int end=ua.IndexOf(' ',slash+1);string version=(end>slash?ua[(slash+1)..end]:ua[(slash+1)..]);return version.Length<=64?version:"";
        }
        catch(Exception e)when(e is KeyNotFoundException or InvalidOperationException){return "";}
    }
}
