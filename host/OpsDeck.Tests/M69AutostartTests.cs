using OpsDeck.Host;
namespace OpsDeck.Tests;

public static class M69AutostartTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string name,bool ok)=>check("m69-"+name,ok);
        string? launcher=AutoStartManager.FindLauncher(AppContext.BaseDirectory);
        C("finds-stable-launcher",launcher!=null&&Path.GetFileName(launcher).Equals("OPSDECK_BASLAT.cmd",StringComparison.OrdinalIgnoreCase));
        string command=AutoStartManager.BuildCommand(@"C:\Projects\OpsDeck\OPSDECK_BASLAT.cmd",@"C:\Windows\System32\cmd.exe");
        C("command-uses-cmd",command.StartsWith("\"C:\\Windows\\System32\\cmd.exe\" /d /c ",StringComparison.Ordinal));
        C("command-stable-launcher",command.Contains("OPSDECK_BASLAT.cmd",StringComparison.Ordinal)&&!command.Contains("host-m68a",StringComparison.OrdinalIgnoreCase)&&!command.Contains("host-m69a",StringComparison.OrdinalIgnoreCase));
        C("command-tray-mode",command.Contains(" --tray\"",StringComparison.Ordinal));
        C("command-quotes-launcher",command.Contains("\"C:\\Projects\\OpsDeck\\OPSDECK_BASLAT.cmd\" --tray",StringComparison.Ordinal));
        C("launcher-forwards-args",launcher!=null&&File.ReadAllText(launcher).Contains("%*",StringComparison.Ordinal));
        C("launcher-stable-runtime",launcher!=null&&File.ReadAllText(launcher).Contains(@"runtime\OpsDeck.Host.exe",StringComparison.OrdinalIgnoreCase)&&!File.ReadAllText(launcher).Contains(@"artifacts\host-",StringComparison.OrdinalIgnoreCase));
        bool relative=false;try{AutoStartManager.BuildCommand("OPSDECK_BASLAT.cmd",@"C:\Windows\System32\cmd.exe");}catch(ArgumentException){relative=true;}C("relative-rejected",relative);
        bool exe=false;try{AutoStartManager.BuildCommand(@"C:\Projects\OpsDeck\OpsDeck.Host.exe",@"C:\Windows\System32\cmd.exe");}catch(ArgumentException){exe=true;}C("versioned-exe-rejected",exe);
        var state=AutoStartManager.Read(AppContext.BaseDirectory);C("registry-read-safe",!string.IsNullOrWhiteSpace(state.Detail));
        C("stable-value-name",AutoStartManager.ValueName=="ALAZ_OPSDECK");
    }
}
