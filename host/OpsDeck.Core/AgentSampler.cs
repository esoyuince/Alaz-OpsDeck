using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace OpsDeck.Core;

public sealed class AgentSampler : IDisposable
{
    private readonly HostConfig config;private readonly BridgeTaskReader taskReader;private readonly DesktopCommanderSampler desktopCommander=new();
    private readonly HttpClient http=new(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false}){Timeout=TimeSpan.FromSeconds(2),MaxResponseContentBufferSize=16384};
    public AgentSampler(HostConfig config){this.config=config;taskReader=new BridgeTaskReader(config.BridgeTaskDatabasePath);}
    public async Task<AgentSample> SampleAsync(CancellationToken ct)
    {
        int count=0;bool partial=false;using var self=Process.GetCurrentProcess();int ownSession=self.SessionId;
        string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.CodexDirectory))+Path.DirectorySeparatorChar;
        try {
            foreach(var p in Process.GetProcessesByName("codex"))using(p){
                try {
                    // Presence in the selected installation, not task/session progress.
                    if(p.SessionId!=ownSession)continue;
                    var exe=p.MainModule?.FileName;
                    if(exe!=null&&exe.StartsWith(root,StringComparison.OrdinalIgnoreCase))
                    {
                        string? parent=ParentProcessName(p);if(parent==null)partial=true;
                        if(!IsAuxiliaryCodexParent(parent))count++;
                    }
                    else if(exe==null)partial=true;
                } catch(Exception e)when(e is Win32Exception or InvalidOperationException or NotSupportedException){partial=true;}
            }
        }catch(Win32Exception){partial=true;}
        SourceState bridge=SourceState.Setup;
        if(config.BridgeHealthUrl.Length>0){
            try {
                using var response=await http.GetAsync(HostConfig.ValidateBridge(config.BridgeHealthUrl),ct);
                if(response.StatusCode==HttpStatusCode.Unauthorized||response.StatusCode==HttpStatusCode.Forbidden)bridge=SourceState.Denied;
                else if(!response.IsSuccessStatusCode)bridge=SourceState.Error;
                else {using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));bridge=IsBridgeHealth(json.RootElement)?SourceState.Ok:SourceState.Error;}
            }catch(Exception e)when(e is HttpRequestException or TaskCanceledException or JsonException){if(ct.IsCancellationRequested)throw;bridge=SourceState.Error;}
        }
        var observed=DateTimeOffset.UtcNow;var tasks=taskReader.Read(observed);var remote=await desktopCommander.SampleAsync(ct);
        return new(partial&&count==0?-1:count,partial?SourceState.Partial:SourceState.Ok,bridge,observed,
            "Codex process, ChatGPT MCP bridge and Remote Desktop Commander are independent sources.",tasks,remote);
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessBasicInformation
    {public IntPtr Reserved1,PebBaseAddress,Reserved2A,Reserved2B,UniqueProcessId,InheritedFromUniqueProcessId;}
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr processHandle,int processInformationClass,ref ProcessBasicInformation info,int length,out int returnLength);
    private static string? ParentProcessName(Process process)
    {
        try{var info=new ProcessBasicInformation();int status=NtQueryInformationProcess(process.Handle,0,ref info,Marshal.SizeOf<ProcessBasicInformation>(),out _);if(status!=0)return null;long raw=info.InheritedFromUniqueProcessId.ToInt64();if(raw<=0||raw>int.MaxValue)return null;using var parent=Process.GetProcessById((int)raw);return parent.ProcessName;}
        catch(Exception e)when(e is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException){return null;}
    }
    public static bool IsAuxiliaryCodexParent(string? parent)=>parent!=null&&(parent.Equals("node",StringComparison.OrdinalIgnoreCase)||parent.StartsWith("OpsDeck.",StringComparison.OrdinalIgnoreCase));    public static bool IsBridgeHealth(JsonElement o)=>o.ValueKind==JsonValueKind.Object&&o.TryGetProperty("ok",out var ok)&&ok.ValueKind==JsonValueKind.True&&o.TryGetProperty("name",out var name)&&name.ValueKind==JsonValueKind.String&&name.GetString()=="chatgpt-codex-mcp-bridge";
    public void Dispose()=>http.Dispose();
}
