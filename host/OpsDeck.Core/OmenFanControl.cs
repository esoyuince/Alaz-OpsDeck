using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpsDeck.Core;

public enum OmenFanMode { Unknown=0, Auto=1, Max=2, Manual=3 }

public sealed record OmenFanControlSnapshot(
    bool Supported=false,
    bool ManualSupported=false,
    OmenFanMode Mode=OmenFanMode.Unknown,
    bool Busy=false,
    DateTimeOffset? ObservedAt=null,
    string Status="OMEN fan control not sampled",
    int? SpeedPercent=null)
{
    public static OmenFanControlSnapshot Setup=>new();
}

public sealed record PanelFanControlRequest(OmenFanMode Mode,int RequestId,int? SpeedPercent=null)
{
    private static readonly Regex Pattern=new(
        @"(?:\A| )opsdeck\.ui: FAN_CONTROL_REQUEST mode=(auto|max|manual)(?: speed=([0-9]{2,3}))? request=([1-9][0-9]{0,9})\z",
        RegexOptions.CultureInvariant|RegexOptions.Compiled);

    public static bool TryParse(string line,out PanelFanControlRequest? request)
    {
        request=null;
        if(string.IsNullOrEmpty(line)||line.Length>240)return false;
        var m=Pattern.Match(line);
        if(!m.Success||!int.TryParse(m.Groups[3].Value,out int id)||id<=0)return false;
        string modeText=m.Groups[1].Value;int? speed=null;
        if(m.Groups[2].Success)
        {
            if(!int.TryParse(m.Groups[2].Value,out int parsed)||parsed<50||parsed>100||parsed%5!=0)return false;
            speed=parsed;
        }
        OmenFanMode mode=modeText switch{"auto"=>OmenFanMode.Auto,"max"=>OmenFanMode.Max,"manual"=>OmenFanMode.Manual,_=>OmenFanMode.Unknown};
        if(mode==OmenFanMode.Manual&&!speed.HasValue||mode!=OmenFanMode.Manual&&speed.HasValue)return false;
        request=new(mode,id,speed);
        return true;
    }
}

public sealed class OmenFanControl : IDisposable
{
    private readonly string scriptPath;
    private readonly SemaphoreSlim gate=new(1,1);
    private OmenFanControlSnapshot snapshot=OmenFanControlSnapshot.Setup;
    private int disposed;

    public OmenFanControl(string? script=null)=>scriptPath=script??Path.Combine(AppContext.BaseDirectory,"omen_fan_control.ps1");
    public OmenFanControlSnapshot Snapshot=>Volatile.Read(ref snapshot);

    public Task<OmenFanControlSnapshot> RefreshAsync(CancellationToken ct)=>RunAsync("status",null,ct);

    public Task<OmenFanControlSnapshot> SetModeAsync(OmenFanMode mode,CancellationToken ct)
    {
        if(mode is not (OmenFanMode.Auto or OmenFanMode.Max))throw new ArgumentOutOfRangeException(nameof(mode));
        return RunAsync(mode==OmenFanMode.Auto?"auto":"max",null,ct);
    }

    public Task<OmenFanControlSnapshot> SetManualAsync(int speedPercent,CancellationToken ct)
    {
        if(speedPercent<50||speedPercent>100||speedPercent%5!=0)throw new ArgumentOutOfRangeException(nameof(speedPercent));
        return RunAsync("manual",speedPercent,ct);
    }

    private async Task<OmenFanControlSnapshot> RunAsync(string action,int? speedPercent,CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        await gate.WaitAsync(ct);
        try
        {
            var prior=Snapshot;
            Volatile.Write(ref snapshot,prior with{Busy=true,Status=action=="status"?"Reading OMEN fan control":$"Applying {action.ToUpperInvariant()}"});
            if(!File.Exists(scriptPath))return PublishFailure(prior,"OMEN fan helper is not deployed");

            string powershell=Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32","WindowsPowerShell","v1.0","powershell.exe");
            var start=new ProcessStartInfo(powershell)
            {
                UseShellExecute=false,
                CreateNoWindow=true,
                RedirectStandardOutput=true,
                RedirectStandardError=true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(scriptPath);
            start.ArgumentList.Add("-Action");
            start.ArgumentList.Add(action);
            if(speedPercent.HasValue)
            {
                start.ArgumentList.Add("-SpeedPct");
                start.ArgumentList.Add(speedPercent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            start.ArgumentList.Add("-InternalWorker");

            using var process=Process.Start(start)??throw new InvalidOperationException("OMEN fan helper could not start");
            Task<string> stdout=process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr=process.StandardError.ReadToEndAsync(ct);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try{await process.WaitForExitAsync(timeout.Token);}
            catch(OperationCanceledException)when(!ct.IsCancellationRequested)
            {
                TryKill(process);
                return PublishFailure(prior,"OMEN fan helper timed out");
            }

            string output=await stdout,error=await stderr;
            if(process.ExitCode!=0)return PublishFailure(prior,$"OMEN fan helper exit {process.ExitCode}: {Limit(error)}");
            try
            {
                var next=ParseOutput(output,DateTimeOffset.UtcNow);
                Volatile.Write(ref snapshot,next);
                return next;
            }
            catch(Exception e)when(e is JsonException or InvalidDataException)
            {
                return PublishFailure(prior,"OMEN fan helper returned invalid status");
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return PublishFailure(Snapshot,"OMEN fan control unavailable: "+e.GetType().Name);
        }
        finally{gate.Release();}
    }

    private OmenFanControlSnapshot PublishFailure(OmenFanControlSnapshot prior,string status)
    {
        var failed=prior with{Busy=false,Status=Limit(status)};
        Volatile.Write(ref snapshot,failed);
        return failed;
    }

    public static OmenFanControlSnapshot ParseOutput(string output,DateTimeOffset observedAt)
    {
        string? json=output.Replace("\r","")
            .Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
            .LastOrDefault(x=>x.StartsWith('{'));
        if(json==null)throw new InvalidDataException("OMEN fan status JSON missing");
        using var doc=JsonDocument.Parse(json);
        var root=doc.RootElement;
        if(root.GetProperty("schema").GetString()!="opsdeck.omen-fan-control.v1"||
           !root.GetProperty("supported").GetBoolean())
            throw new InvalidDataException("Unsupported OMEN fan status");

        string modeText=root.GetProperty("mode").GetString()??"";
        OmenFanMode mode=modeText switch
        {
            "auto"=>OmenFanMode.Auto,
            "max"=>OmenFanMode.Max,
            "manual"=>OmenFanMode.Manual,
            _=>OmenFanMode.Unknown
        };
        bool manualSupported=root.TryGetProperty("manual_supported",out var manualNode)&&(manualNode.ValueKind is JsonValueKind.True or JsonValueKind.False)&&manualNode.GetBoolean();
        int? speed=null;
        if(root.TryGetProperty("fan_speed_pct",out var speedNode)&&speedNode.TryGetInt32(out int parsedSpeed)&&parsedSpeed is >=50 and <=100&&parsedSpeed%5==0)speed=parsedSpeed;
        if(manualSupported&&!speed.HasValue)throw new InvalidDataException("Manual fan speed unavailable");
        if(mode==OmenFanMode.Manual&&!manualSupported)throw new InvalidDataException("Manual mode reported without capability");
        string status=mode==OmenFanMode.Manual?$"OMEN MANUAL {speed}%":$"OMEN {modeText.ToUpperInvariant()}";
        return new(true,manualSupported,mode,false,observedAt,status,speed);
    }

    private static string Limit(string value)
    {
        string clean=Regex.Replace(value??"",@"[\x00-\x1F\x7F]+"," ").Trim();
        return clean.Length<=140?clean:clean[..140];
    }

    private static void TryKill(Process p)
    {
        try{if(!p.HasExited)p.Kill(true);}catch{}
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        gate.Dispose();
    }
}
