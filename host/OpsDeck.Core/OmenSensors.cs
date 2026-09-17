using System.Diagnostics;
using System.Text.Json;

namespace OpsDeck.Core;

public sealed record OmenReading(
    double? CpuTemp=null,
    double? ChassisTemp=null,
    double? Fan1Rpm=null,
    double? Fan2Rpm=null,
    DateTimeOffset? ObservedAt=null,
    string Status="OMEN telemetry not sampled");

public sealed class OmenSensors : IDisposable
{
    private static readonly TimeSpan FreshFor=TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RefreshAfterSuccess=TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryAfter=TimeSpan.FromSeconds(5);
    private static readonly string[] CleanupSources=["cpu","gpu","fan_notebook","fan_desktop","ir_temperature","ambient_temperature","vr_temperature"];
    private readonly object gate=new();
    private readonly string scriptPath;
    private readonly CancellationTokenSource stopped=new();
    private OmenReading latest=new();
    private Task? refresh;
    private DateTimeOffset lastAttempt=DateTimeOffset.MinValue;
    private bool disposed;

    public OmenSensors(string? script=null)=>scriptPath=script??Path.Combine(AppContext.BaseDirectory,"omen_probe.ps1");

    public OmenReading Read()
    {
        lock(gate){
            var now=DateTimeOffset.UtcNow;
            if(disposed)return new(Status:"OMEN telemetry stopped");
            if(!File.Exists(scriptPath))return new(Status:"OMEN probe is not deployed");
            if((refresh==null||refresh.IsCompleted)&&RefreshDue(now,lastAttempt,latest.ObservedAt)){
                lastAttempt=now;refresh=Task.Run(Refresh);
            }
            if(latest.ObservedAt is not { } observed||now-observed>FreshFor||observed-now>TimeSpan.FromMinutes(1))
                return new(Status:refresh is {IsCompleted:false}?"OMEN telemetry starting":"OMEN telemetry stale");
            return latest;
        }
    }

    public static bool RefreshDue(DateTimeOffset now,DateTimeOffset lastAttempt,DateTimeOffset? lastSuccess)
        =>lastSuccess is { } observed?now-observed>=RefreshAfterSuccess:now-lastAttempt>=RetryAfter;

    public void ResetBaseline(){lock(gate){if(!disposed){latest=new();lastAttempt=DateTimeOffset.MinValue;}}}

    private async Task Refresh()
    {
        Process? process=null;
        try {
            string? worker=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe");
            if(!File.Exists(worker))throw new FileNotFoundException("Windows PowerShell is unavailable.",worker);
            string scriptDirectory=Path.GetDirectoryName(scriptPath)??AppContext.BaseDirectory;
            string evidenceRoot=Path.Combine(Directory.GetParent(scriptDirectory)?.FullName??scriptDirectory,"logs");
            Directory.CreateDirectory(evidenceRoot);
            string output=Path.Combine(evidenceRoot,"omen-host-latest.json");
            var start=new ProcessStartInfo(worker){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",scriptPath,"-DurationSeconds","3","-OutputPath",output,"-InternalWorker","-Quiet"})start.ArgumentList.Add(arg);
            process=Process.Start(start)??throw new InvalidOperationException("OMEN probe could not start.");
            lock(gate){
                if(disposed){TerminateAndConfirm(process);throw new OperationCanceledException(stopped.Token);}
            }
            Task<string> stdout=process.StandardOutput.ReadToEndAsync(stopped.Token),stderr=process.StandardError.ReadToEndAsync(stopped.Token);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){TerminateAndConfirm(process);if(stopped.IsCancellationRequested)throw;throw new TimeoutException("OMEN probe timed out.");}
            await Task.WhenAll(stdout,stderr);
            if(process.ExitCode!=0)throw new InvalidOperationException($"OMEN probe exit {process.ExitCode}: {Limit(stderr.Result)}");
            OmenReading parsed=Parse(await File.ReadAllTextAsync(output,stopped.Token));
            lock(gate)latest=parsed;
        } catch(OperationCanceledException)when(stopped.IsCancellationRequested) {
        } catch(Exception e) {
            lock(gate){if(!disposed)latest=new(Status:$"OMEN telemetry unavailable: {e.GetType().Name}");}
        } finally {
            if(process!=null){try{TerminateAndConfirm(process);}finally{process.Dispose();}}
        }
    }

    public static OmenReading Parse(string json)
    {
        using var document=JsonDocument.Parse(json);JsonElement root=document.RootElement;
        if(root.GetProperty("schema").GetString()!="opsdeck.omen-probe.v3")throw new InvalidDataException("Unsupported OMEN probe schema.");
        if(root.GetProperty("direct_control_api_invoked").GetBoolean())throw new InvalidDataException("Control-capable probe output was rejected.");
        JsonElement cleanup=root.GetProperty("cleanup");
        if(cleanup.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("OMEN probe cleanup was not confirmed.");
        var cleaned=new HashSet<string>(StringComparer.Ordinal);
        foreach(JsonElement item in cleanup.EnumerateArray()){
            string? source=item.TryGetProperty("source",out var sourceValue)?sourceValue.GetString():null;
            bool attempted=item.TryGetProperty("unregister_attempted",out var attemptedValue)&&attemptedValue.ValueKind==JsonValueKind.True;
            bool succeeded=item.TryGetProperty("helper_return",out var returnValue)&&returnValue.ValueKind==JsonValueKind.True;
            bool noError=item.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.Null;
            if(source==null||!attempted||!succeeded||!noError||!cleaned.Add(source))throw new InvalidDataException("OMEN probe cleanup was not confirmed.");
        }
        if(cleaned.Count!=CleanupSources.Length||CleanupSources.Any(source=>!cleaned.Contains(source)))throw new InvalidDataException("OMEN probe cleanup was incomplete.");
        JsonElement snapshot=root.GetProperty("snapshot");
        double? cpu=Number(snapshot,"cpu_temperature_c",0,150),chassis=OptionalNumber(snapshot,"chassis_temperature_c",0,150),fan1=Number(snapshot,"fan1_rpm",0,30000),fan2=Number(snapshot,"fan2_rpm",0,30000);
        if(cpu==null||fan1==null||fan2==null)throw new InvalidDataException("Required OMEN readings are missing.");
        DateTimeOffset observed=root.GetProperty("collected_utc").GetDateTimeOffset();
        return new(cpu,chassis,fan1,fan2,observed,"OMEN Gaming Hub IPC (up to 15s old)");
    }

    private static double? Number(JsonElement parent,string name,double min,double max)
    {
        if(!parent.TryGetProperty(name,out var value)||value.ValueKind!=JsonValueKind.Number||!value.TryGetDouble(out double number)||!double.IsFinite(number)||number<min||number>max)return null;
        return number;
    }
    private static double? OptionalNumber(JsonElement parent,string name,double min,double max)
    {
        if(!parent.TryGetProperty(name,out var value)||value.ValueKind==JsonValueKind.Null)return null;
        double? number=Number(parent,name,min,max);
        if(number==null)throw new InvalidDataException($"Invalid optional OMEN reading: {name}.");
        return number;
    }
    private static string Limit(string value)=>value.Length<=200?value:value[..200];
    private static void TerminateAndConfirm(Process process)
    {
        if(process.HasExited)return;
        process.Kill(true);
        if(!process.WaitForExit(5000))throw new InvalidOperationException($"OMEN probe process {process.Id} did not exit after termination.");
    }
    public void Dispose()
    {
        Task? pending;
        lock(gate){
            if(disposed)return;
            disposed=true;stopped.Cancel();pending=refresh;
        }
        Exception? failure=null;
        bool completed=pending==null;
        try{if(pending!=null)completed=pending.Wait(TimeSpan.FromSeconds(5));}catch(AggregateException e){failure??=e.Flatten();completed=true;}
        if(completed)stopped.Dispose();
        else{
            _=pending!.ContinueWith(_=>stopped.Dispose(),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            failure??=new TimeoutException("OMEN probe task did not stop within five seconds.");
        }
        if(failure!=null)throw new InvalidOperationException("OMEN probe shutdown was not confirmed.",failure);
    }
}
