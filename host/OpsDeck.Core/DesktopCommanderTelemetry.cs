using System.Diagnostics;
using System.Text.Json;

namespace OpsDeck.Core;

public sealed record DesktopCommanderSnapshot(SourceState State,DateTimeOffset? ObservedAt=null,int ProcessCount=0,
    long? TotalCalls=null,long? SuccessfulCalls=null,long? FailedCalls=null,long? Sessions=null,long? UniqueTools=null,
    string LastTool="",DateTimeOffset? LastActionAt=null,string Detail="")
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
        var wall=DateTimeOffset.UtcNow;if(wall<nextSample)return cached;int processes=0;bool probeError=false;try{processes=await RemoteProcessCount(ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch(Exception e)when(e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception){probeError=true;}
        var now=DateTimeOffset.UtcNow;long? total=null,ok=null,failed=null,sessions=null,tools=null;string lastTool="";DateTimeOffset? lastAt=null;bool stats=false;
        try{(total,ok,failed,sessions,tools)=ReadUsage(ConfigPath);stats=total.HasValue;}catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException){}
        try{(lastTool,lastAt)=ReadLastTool(HistoryPath);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException){}
        var state=probeError?SourceState.Error:processes>0?(stats?SourceState.Ok:SourceState.Partial):SourceState.NoData;
        string detail=probeError?"Remote Desktop Commander process probe failed safely.":processes>0?(stats?"Remote Desktop Commander process and bounded local usage stats observed.":"Remote Desktop Commander remote process observed; local usage stats unavailable."):"Remote Desktop Commander remote process not observed.";
        var result=new DesktopCommanderSnapshot(state,now,processes,total,ok,failed,sessions,tools,lastTool,lastAt,detail);cached=result;nextSample=now.AddSeconds(15);return result;
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

    private static async Task<int> RemoteProcessCount(CancellationToken ct)
    {
        const string script="$p=Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'desktop-commander' -and $_.CommandLine -match '\\bremote\\b' }; @($p).Count";
        var psi=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};psi.ArgumentList.Add("-NoProfile");psi.ArgumentList.Add("-NonInteractive");psi.ArgumentList.Add("-Command");psi.ArgumentList.Add(script);
        using var p=Process.Start(psi)??throw new IOException("Desktop Commander process probe failed.");using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try{string output=await p.StandardOutput.ReadToEndAsync(timeout.Token);await p.WaitForExitAsync(timeout.Token);return p.ExitCode==0&&int.TryParse(output.Trim(),out int count)?Math.Clamp(count,0,16):0;}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){try{if(!p.HasExited)p.Kill(true);}catch{}return 0;}
    }
}
