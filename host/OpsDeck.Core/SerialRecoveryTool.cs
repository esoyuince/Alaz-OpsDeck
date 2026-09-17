using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public readonly record struct SerialRecoveryProbeResult(bool Found,bool Succeeded,int ExitCode,bool TimedOut);

public static class SerialRecoveryTool
{
    private static readonly string[] PinnedParts=[".espressif","python_env","idf5.5_py3.11_env","Scripts","esptool.exe"];
    public const int TimeoutSeconds=20;

    public static string[] SafeRomProbeArguments(string port)
    {
        if(!Regex.IsMatch(port,@"^COM[1-9][0-9]{0,3}$",RegexOptions.CultureInvariant))throw new ArgumentException("Invalid recovery COM port.");
        return ["--port",port,"--before","default_reset","--after","hard_reset","chip_id"];
    }

    public static string? FindPinnedEsptool(string startDirectory)
    {
        var dir=new DirectoryInfo(Path.GetFullPath(startDirectory));
        for(int i=0;i<8&&dir!=null;i++,dir=dir.Parent)
        {
            string candidate=Path.Combine([dir.FullName,..PinnedParts]);
            if(File.Exists(candidate))return candidate;
        }
        return null;
    }
    public static async Task<SerialRecoveryProbeResult> RunPinnedRomProbe(string startDirectory,string port,CancellationToken external)
    {
        string? tool=FindPinnedEsptool(startDirectory);
        if(tool==null)return new(false,false,-1,false);
        var psi=new ProcessStartInfo(tool){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(tool)!};
        foreach(string arg in SafeRomProbeArguments(port))psi.ArgumentList.Add(arg);
        try
        {
            using var process=Process.Start(psi);
            if(process==null)return new(true,false,-1,false);
            Task<string> stdout=process.StandardOutput.ReadToEndAsync();
            Task<string> stderr=process.StandardError.ReadToEndAsync();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(external);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            try{await process.WaitForExitAsync(timeout.Token);}
            catch(OperationCanceledException)when(!external.IsCancellationRequested)
            {
                try{process.Kill(true);}catch(InvalidOperationException){}
                return new(true,false,-1,true);
            }
            await Task.WhenAll(stdout,stderr);
            return new(true,process.ExitCode==0,process.ExitCode,false);
        }
        catch(Exception e)when(e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {return new(true,false,-1,false);}
    }
}
