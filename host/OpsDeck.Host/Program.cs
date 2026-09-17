using System.Security.Principal;
using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Host;

internal static class Program
{
    [STAThread]static int Main(string[] args)
    {
        try {
            string? Arg(string key){int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:null;}
            var settings=new LocalSettings(Arg("--data"));
            string name=@"Local\OpsDeck.Host."+WindowsIdentity.GetCurrent().User?.Value;
            using var mutex=new Mutex(true,name,out bool created);
            if(!created){if(args.Contains("--headless")){Console.Error.WriteLine("OpsDeck instance already owns the host mutex.");return 2;}MessageBox.Show("ALAZ OPSDECK zaten açık. Sistem tepsisindeki simgesini kullan.","ALAZ OPSDECK");return 2;}
            try{if(args.Contains("--headless"))return Headless(settings,args,Arg).GetAwaiter().GetResult();bool startInTray=args.Contains("--tray");ApplicationConfiguration.Initialize();Application.SetColorMode(SystemColorMode.Dark);Application.Run(new MainForm(settings,startInTray));}finally{mutex.ReleaseMutex();}
            return 0;
        }catch(Exception e){if(args.Contains("--headless")){Console.Error.WriteLine("HEADLESS_FAILED "+e.GetType().Name);return 1;}MessageBox.Show("ALAZ OPSDECK başlatılamadı: "+e.GetType().Name+"\nAyarları ve yerel log dosyasını kontrol et.","ALAZ OPSDECK");return 1;}
    }
    private static async Task<int> Headless(LocalSettings settings,string[] args,Func<string,string?> arg)
    {
        int duration=int.TryParse(arg("--headless"),out int n)?Math.Clamp(n,5,600):20;
        var config=settings.Load();await using var engine=new AppEngine(config,settings);engine.Start(!args.Contains("--no-serial"));engine.StartCodexTelemetry();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        ResourceInventory inventory=ResourceInventory.Empty;
        var delay=Task.Delay(TimeSpan.FromSeconds(duration));
        if(args.Contains("--acceptance"))inventory=await engine.DiscoverResources(timeout.Token);
        await delay;
        var pc=engine.Pc;var f=engine.Fleet;var acks=engine.PanelReceipts;
        var checks=new Dictionary<string,bool>{
            ["pc_fresh"]=engine.PcIsFresh,
            ["cpu_temperature_current"]=pc.CpuTemp is >=0 and <=150,
            ["chassis_temperature_current"]=pc.ChassisTemp is >=0 and <=150,
            ["two_fan_rpm_current"]=pc.Fan1Rpm is >=0 and <=30000&&pc.Fan2Rpm is >=0 and <=30000,
            ["volume_c_current"]=pc.Volumes?.Any(v=>v.Id=="C:"&&v.State(DateTimeOffset.UtcNow)==SourceState.Ok)==true,
            ["hosting_all_reachable"]=f.Hosting.State==SourceState.Ok&&f.Hosting.Value>0&&f.Hosting.Value==f.Hosting.Secondary,
            ["general_scope_preserved"]=engine.GeneralHosting.Secondary==HostingScope.Urls(config.Sites).Length,
            ["account_scopes_preserved"]=engine.Accounts.All(a=>a.Hosting.Secondary==HostingScope.Urls(a.Profile.Sites).Length),
            ["last_read_cost_present"]=f.Cost.LastRead&&f.Cost.Value is >=0&&f.Cost.Unit=="USD",
            ["pages_complete"]=inventory.Sets.Count(s=>s.Kind==ResourceKind.Pages&&s.Complete&&s.State==SourceState.Ok)==config.EffectiveAccounts().Count(a=>a.Enabled),
            ["new_pc_extension_ack"]=acks.Pc>0&&acks.LastPc.Contains("volumes=")&&acks.LastPc.Contains("cpu_sensor=")&&acks.LastPc.Contains("chassis_sensor=1"),
            ["status_ack"]=acks.Status>0,["account_ack"]=acks.Cloud>0,["inventory_ack"]=acks.Inventory>0
        };
        var result=new {version="M4.11-A",environment="REAL READ-ONLY HOST AND PANEL",details_acceptance="NOT_ASSESSED_BY_LEGACY_CHECKLIST",acceptance_scope="Disk, hosting scopes, usage provenance, Pages, transport, OMEN CPU and chassis temperature, two physical notebook fan RPM, read-only Cloudflare history UI, and sanitized Local Codex Bridge task metadata",
            samples=engine.Samples,pc,fleet=f,general_hosting=engine.GeneralHosting,
            accounts=engine.Accounts.Select(a=>new{name=a.Profile.Name,enabled=a.Profile.Enabled,a.Workers,a.D1,a.R2,a.Cost,a.Hosting}),
            inventory=inventory.Sets.Select(s=>new{name=s.ProfileName,kind=s.Kind.ToString(),s.State,s.Complete,count=s.Items.Length,s.Detail}),
            sites=engine.Sites,serial=engine.SerialStatus,panel=engine.PanelEvidence,acks,checks};
        var path=arg("--snapshot")??Path.Combine(settings.DirectoryPath,"observation.json");
        File.WriteAllText(path,JsonSerializer.Serialize(result,new JsonSerializerOptions(Core.Json.Options){WriteIndented=true}));
        return args.Contains("--acceptance")&&checks.Values.Any(ok=>!ok)?1:0;
    }
}
