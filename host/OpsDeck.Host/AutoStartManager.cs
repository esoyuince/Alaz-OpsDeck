using Microsoft.Win32;
using System.Security;
namespace OpsDeck.Host;

public readonly record struct AutoStartState(bool Enabled,bool Current,string Detail,string? Launcher,string? Command);

public static class AutoStartManager
{
    public const string ValueName="ALAZ_OPSDECK";
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string? FindLauncher(string? startDirectory=null)
    {
        var dir=new DirectoryInfo(Path.GetFullPath(startDirectory??AppContext.BaseDirectory));
        for(int i=0;i<8&&dir!=null;i++,dir=dir.Parent)
        {
            string candidate=Path.Combine(dir.FullName,"OPSDECK_BASLAT.cmd");
            if(File.Exists(candidate))return Path.GetFullPath(candidate);
        }
        return null;
    }

    public static string BuildCommand(string launcher,string? commandProcessor=null)
    {
        if(string.IsNullOrWhiteSpace(launcher)||!Path.IsPathFullyQualified(launcher)||!string.Equals(Path.GetExtension(launcher),".cmd",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Autostart launcher must be an absolute .cmd path.");
        string full=Path.GetFullPath(launcher);
        string cmd=commandProcessor??Environment.GetEnvironmentVariable("ComSpec")??Path.Combine(Environment.SystemDirectory,"cmd.exe");
        if(string.IsNullOrWhiteSpace(cmd)||!Path.IsPathFullyQualified(cmd))throw new InvalidOperationException("Windows command processor path is unavailable.");
        return $"\"{Path.GetFullPath(cmd)}\" /d /c \"\"{full}\" --tray\"";
    }

    public static AutoStartState Read(string? startDirectory=null)
    {
        try
        {
            using var key=Registry.CurrentUser.OpenSubKey(RunKey,false);
            string? saved=key?.GetValue(ValueName) as string;
            string? launcher=FindLauncher(startDirectory);
            if(string.IsNullOrWhiteSpace(saved))return new(false,false,"Kapalı — Windows açılış kaydı yok.",launcher,null);
            if(launcher==null)return new(true,false,"Etkin ama OPSDECK_BASLAT.cmd bulunamadı.",null,saved);
            string expected=BuildCommand(launcher);
            bool current=string.Equals(saved,expected,StringComparison.OrdinalIgnoreCase);
            return new(true,current,current?"Etkin — sabit OpsDeck launcher kaydı güncel.":"Etkin ama kayıt yolu eski/farklı; Kaydet ile güncellenecek.",launcher,saved);
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new(false,false,"Başlangıç kaydı okunamadı: "+e.GetType().Name,FindLauncher(startDirectory),null);
        }
    }

    public static void SetEnabled(bool enabled,string? startDirectory=null)
    {
        using var key=Registry.CurrentUser.CreateSubKey(RunKey,true)??throw new InvalidOperationException("Windows Run registry key cannot be opened.");
        if(!enabled){key.DeleteValue(ValueName,false);return;}
        string launcher=FindLauncher(startDirectory)??throw new InvalidOperationException("OPSDECK_BASLAT.cmd bulunamadı.");
        key.SetValue(ValueName,BuildCommand(launcher),RegistryValueKind.String);
    }
}
