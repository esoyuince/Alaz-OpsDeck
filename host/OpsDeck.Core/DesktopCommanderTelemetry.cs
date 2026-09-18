using System.Diagnostics;
using System.Text.Json;

namespace OpsDeck.Core;

public sealed record DesktopCommanderSnapshot(SourceState State,DateTimeOffset? ObservedAt=null,int ProcessCount=0,
    long? TotalCalls=null,long? SuccessfulCalls=null,long? FailedCalls=null,long? Sessions=null,long? UniqueTools=null,
    string LastTool="",DateTimeOffset? LastActionAt=null,string Detail="",long? RunCalls=null,long? RunSessions=null)
{
    public int? SuccessPermille=>TotalCalls>0&&SuccessfulCalls.HasValue?(int)Math.Clamp(Math.Round(SuccessfulCalls.Value*1000d/TotalCalls.Value),0,1000):null;
    public static DesktopCommanderSnapshot Setup=>new(SourceState.Setup,Detail:"Desktop Commander has not been sampled yet.");
}

public sealed class DesktopCommanderSampler
{
    private DesktopCommanderSnapshot cached=DesktopCommanderSnapshot.Setup;private DateTimeOffset nextSample=DateTimeOffset.MinValue;
    public static readonly string ConfigPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude-server-commander","config.json");
    public static readonly string HistoryPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude-server-commander","tool-history.jsonl");
    public async Task<DesktopCommanderSnapshot> SampleAsync(CancellationToken ct)
    {
        var wall=DateTimeOffset.UtcNow;if(wall<nextSample)return cached;int processes=0;DateTimeOffset? processStart=null;bool probeError=false;try{(processes,processStart)=await RemoteProcessProbe(ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch(Exception e)when(e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception){probeError=true;}
        var now=DateTimeOffset.UtcNow;long? total=null,ok=null,failed=null,sessions=null,tools=null,runCalls=null,runSessions=null;string lastTool="";DateTimeOffset? lastAt=null;bool stats=false;
        try{(total,ok,failed,sessions,tools)=ReadUsage(ConfigPath);stats=total.HasValue;}catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException){}
        try{(lastTool,lastAt)=ReadLastTool(HistoryPath);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException){}
        if(processes>0&&processStart.HasValue)try{(runCalls,runSessions)=ReadRunUsage(HistoryPath,processStart.Value);stats|=runCalls.HasValue;}catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException){}
        var state=probeError?SourceState.Error:processes>0?(stats?SourceState.Ok:SourceState.Partial):SourceState.NoData;
        string detail=probeError?"Remote Desktop Commander process probe failed safely.":processes>0?(stats?"Remote Desktop Commander process, current-run counters and bounded local usage stats observed.":"Remote Desktop Commander remote process observed; local usage stats unavailable."):"Remote Desktop Commander remote process not observed.";
        var result=new DesktopCommanderSnapshot(state,now,processes,total,ok,failed,sessions,tools,lastTool,lastAt,detail,runCalls,runSessions);cached=result;nextSample=now.AddSeconds(15);return result;
    }
    public static (long? Total,long? Ok,long? Failed,long? Sessions,long? Tools) ReadUsage(string path)
    {
        if(!File.Exists(path))return(null,null,null,null,null);var info=new FileInfo(path);if(info.Length>1024*1024)throw new InvalidDataException("Desktop Commander config exceeds bound.");
        using var doc=JsonDocument.Parse(File.ReadAllBytes(path));var root=doc.RootElement;if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("usageStats",out var usage)||usage.ValueKind!=JsonValueKind.Object)return(null,null,null,null,null);
        long? total=Long(usage,"totalToolCalls")??Long(usage,"totalCalls"),ok=Long(usage,"successfulCalls"),failed=Long(usage,"failedCalls"),sessions=Long(usage,"totalSessions")??Long(usage,"sessionCount"),tools=null;
        if(usage.TryGetProperty("toolCounts",out var counts)&&counts.ValueKind==JsonValueKind.Object)tools=counts.EnumerateObject().LongCount();
        if(total.HasValue&&ok.HasValue&&failed.HasValue&&ok.Value+failed.Value>total.Value)throw new InvalidDataException("Invalid Desktop Commander usage totals.");
        return(total,ok,failed,sessions,tools);
    }
    public static (long? Calls,long? Sessions) ReadRunUsage(string path,DateTimeOffset startedAt)
    {
        if(!File.Exists(path))return(null,null);var info=new FileInfo(path);if(info.Length>6*1024*1024)throw new InvalidDataException("Desktop Commander history exceeds bound.");
        var times=new List<DateTimeOffset>();var floor=startedAt.AddSeconds(-5);
        foreach(string line in File.ReadLines(path))
        {
            if(string.IsNullOrWhiteSpace(line))continue;
            try
            {
                using var doc=JsonDocument.Parse(line);var root=doc.RootElement;
                if(!root.TryGetProperty("timestamp",out var stamp)||stamp.ValueKind!=JsonValueKind.String||!DateTimeOffset.TryParse(stamp.GetString(),out var at))continue;
                if(at>=floor)times.Add(at);
            }
            catch(JsonException){throw new InvalidDataException("Invalid Desktop Commander history JSON.");}
        }
        if(times.Count==0)return(0,0);times.Sort();long sessions=1;for(int i=1;i<times.Count;i++)if(times[i]-times[i-1]>TimeSpan.FromMinutes(30))sessions++;
        return(times.Count,sessions);
    }
    public static (string Tool,DateTimeOffset? At) ReadLastTool(string path)
    {
        if(!File.Exists(path))return("",null);var info=new FileInfo(path);if(info.Length>6*1024*1024)throw new InvalidDataException("Desktop Commander history exceeds bound.");
        string? line=File.ReadLines(path).LastOrDefault(x=>!string.IsNullOrWhiteSpace(x));if(line==null)return("",null);using var doc=JsonDocument.Parse(line);var root=doc.RootElement;
        string tool=root.TryGetProperty("toolName",out var t)&&t.ValueKind==JsonValueKind.String?t.GetString()??"":"";if(tool.Length>64||tool.Any(char.IsControl))tool="";
        DateTimeOffset? at=null;if(root.TryGetProperty("timestamp",out var stamp)&&stamp.ValueKind==JsonValueKind.String&&DateTimeOffset.TryParse(stamp.GetString(),out var parsed))at=parsed;
        return(tool,at);
    }
    private static long? Long(JsonElement o,string name)
    {if(!o.TryGetProperty(name,out var v)||v.ValueKind==JsonValueKind.Null)return null;if(v.ValueKind!=JsonValueKind.Number||!v.TryGetInt64(out long n)||n<0||n>1_000_000_000_000L)throw new InvalidDataException("Invalid Desktop Commander counter.");return n;}

    private static async Task<(int Count,DateTimeOffset? StartedAt)> RemoteProcessProbe(CancellationToken ct)
    {
        const string script="$p=Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'desktop-commander' -and $_.CommandLine -match '\\bremote\\b' }; $a=@($p); if($a.Count -eq 0){'0|'} else {$first=$a|Sort-Object CreationDate|Select-Object -First 1; $utc=$first.CreationDate.ToUniversalTime().ToString('O'); \"$($a.Count)|$utc\"}";
        var psi=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};psi.ArgumentList.Add("-NoProfile");psi.ArgumentList.Add("-NonInteractive");psi.ArgumentList.Add("-Command");psi.ArgumentList.Add(script);
        using var p=Process.Start(psi)??throw new IOException("Desktop Commander process probe failed.");using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            string output=await p.StandardOutput.ReadToEndAsync(timeout.Token);await p.WaitForExitAsync(timeout.Token);if(p.ExitCode!=0)return(0,null);
            var parts=output.Trim().Split('|',2);if(parts.Length!=2||!int.TryParse(parts[0],out int count))return(0,null);
            DateTimeOffset? started=DateTimeOffset.TryParse(parts[1],out var at)?at:null;return(Math.Clamp(count,0,16),started);
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){try{if(!p.HasExited)p.Kill(true);}catch{}return(0,null);}
    }
}
