using System.Text.Json;

namespace OpsDeck.Core;

public sealed class CodexUsageCache
{
    public const string FileName="codex-usage-last-good.json";
    public const int MaxBytes=64*1024;
    public static readonly TimeSpan MaxAge=TimeSpan.FromMinutes(30);
    private readonly string path;

    public CodexUsageCache(string directory)
    {
        if(string.IsNullOrWhiteSpace(directory))throw new ArgumentException("Cache directory is required.",nameof(directory));
        path=Path.Combine(directory,FileName);
    }

    public string PathName=>path;

    public void Save(CodexUsageSnapshot snapshot)
    {
        if(snapshot.State!=SourceState.Ok||!Usable(snapshot,DateTimeOffset.UtcNow,allowFutureSkew:true))return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(snapshot,Json.Options);
        if(bytes.Length>MaxBytes)throw new InvalidDataException("Codex usage cache exceeds bound.");
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }

    public CodexUsageSnapshot? Load(DateTimeOffset now)
    {
        if(!File.Exists(path))return null;
        var info=new FileInfo(path);if(info.Length<=0||info.Length>MaxBytes)throw new InvalidDataException("Invalid Codex usage cache size.");
        CodexUsageSnapshot? snapshot;
        try{snapshot=JsonSerializer.Deserialize<CodexUsageSnapshot>(File.ReadAllBytes(path),Json.Options);}
        catch(JsonException e){throw new InvalidDataException("Invalid Codex usage cache JSON.",e);}
        if(snapshot==null||snapshot.State!=SourceState.Ok||!Usable(snapshot,now))return null;
        return AsStale(snapshot,"Restored last-good Codex quota cache; live refresh pending.");
    }

    public static CodexUsageSnapshot? Fallback(CodexUsageSnapshot? lastGood,CodexUsageSnapshot failed,DateTimeOffset now)
    {
        if(failed.State==SourceState.Ok)throw new ArgumentException("Fallback requires a failed sample.",nameof(failed));
        if(lastGood==null||!Usable(lastGood,now))return null;
        return AsStale(lastGood,$"Last-good Codex quota retained after transient {failed.State.ToString().ToUpperInvariant()} read failure.");
    }

    public static bool Usable(CodexUsageSnapshot? snapshot,DateTimeOffset now,bool allowFutureSkew=false)
    {
        if(snapshot?.CollectedAt is not DateTimeOffset at)return false;
        var age=now-at;if(age<TimeSpan.Zero)return allowFutureSkew&&age>=TimeSpan.FromMinutes(-2);
        if(age>MaxAge)return false;
        var quotas=snapshot.EffectiveQuotas;if(quotas.Length is <1 or >8)return false;
        foreach(var q in quotas)
        {
            if(string.IsNullOrWhiteSpace(q.Id)||q.Id.Length>80||q.Name.Length>120||q.PlanType.Length>40)return false;
            if(q.Primary==null&&q.Secondary==null)return false;
            if(!WindowOk(q.Primary)||!WindowOk(q.Secondary))return false;
        }
        return Counter(snapshot.LifetimeTokens)&&Counter(snapshot.TodayTokens)&&Counter(snapshot.PeakDailyTokens)&&
               Counter(snapshot.LongestTurnSeconds)&&Counter(snapshot.CurrentStreakDays)&&Counter(snapshot.LongestStreakDays);
    }

    private static bool Counter(long? value)=>!value.HasValue||value.Value>=0;
    private static bool WindowOk(CodexQuotaWindow? window)=>window==null||
        (window.UsedPercent is >=0 and <=100&&(!window.WindowMinutes.HasValue||window.WindowMinutes.Value is >=0 and <=525600));

    private static CodexUsageSnapshot AsStale(CodexUsageSnapshot snapshot,string detail)=>
        snapshot with{State=SourceState.Stale,Detail=detail};
}
