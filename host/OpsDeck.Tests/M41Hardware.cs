using System.Diagnostics;
using System.IO.Ports;
using System.Text.Json;
using OpsDeck.Core;
internal static class M41Hardware
{
    public static async Task<int> Run()
    {
        var settings=new LocalSettings();var config=settings.Load();
        if(config.EffectiveAccounts().Any(a=>a.Enabled))throw new InvalidOperationException("This offline Cloud test requires all accounts disabled.");
        var result=new Dictionary<string,object?>();
        async Task Wait(Func<bool> test,int seconds,string name) {
            var watch=Stopwatch.StartNew();while(!test()&&watch.Elapsed.TotalSeconds<seconds)await Task.Delay(100);
            if(!test())throw new InvalidOperationException(name);result[name]=true;
        }
        await using(var engine=new AppEngine(config,settings)) {
            engine.Start(wifiTelemetry:false);await Wait(()=>engine.PcIsFresh&&engine.SerialStatus.Contains("device responding"),20,"initial_connected");
            await Task.Delay(4000);result["intel_before"]=engine.Pc.IntelGpu;result["nvidia_before"]=engine.Pc.Gpu;
            engine.SerialPaused=true;await Wait(()=>engine.SerialStatus=="Paused / COM released",5,"pause_releases_com");
            bool stale=false,health=false;
            using(var observer=new SerialPort(config.Port,115200){DtrEnable=false,RtsEnable=false,ReadTimeout=200}) {
                observer.Open();var watch=Stopwatch.StartNew();
                while(watch.Elapsed.TotalSeconds<8) {
                    try {string line=observer.ReadLine();if(line.Contains("SOURCE_STATE stale"))stale=true;if(line.Contains("HEALTH uptime_s="))health=true;}
                    catch(TimeoutException){}
                }
            }
            result["panel_stale_seen"]=stale;result["panel_kept_running"]=health;
            engine.SuspendObservation();await Task.Delay(1500);long count=engine.Samples;
            await Task.Delay(1500);result["suspend_hook_stops_sampling"]=!engine.PcIsFresh&&engine.Samples==count;
            engine.ResumeObservation();await Wait(()=>engine.PcIsFresh,10,"resume_hook_rebaselines");
            result["manual_pause_preserved"]=engine.SerialPaused;
            engine.SerialPaused=false;
            await Wait(()=>engine.SerialStatus.Contains("device responding"),15,"reconnect_responds");
            await Task.Delay(7000);result["intel_after"]=engine.Pc.IntelGpu;result["nvidia_after"]=engine.Pc.Gpu;
            result["samples"]=engine.Samples;result["panel_after"]=engine.PanelEvidence;
        }
        using(var released=new SerialPort(config.Port,115200){DtrEnable=false,RtsEnable=false}) {
            released.Open();result["shutdown_releases_com"]=true;
        }
        await using(var restarted=new AppEngine(config,settings)) {
            restarted.Start(wifiTelemetry:false);await Wait(()=>restarted.PcIsFresh&&restarted.SerialStatus.Contains("device responding"),20,"host_restart_recovers");
            result["restart_samples"]=restarted.Samples;
        }
        result["physical_cable_unplug_tested"]=false;
        result["physical_windows_sleep_tested"]=false;
        string path=Path.Combine(Path.GetTempPath(),"opsdeck-hardware-lifecycle-m41.json");
        File.WriteAllText(path,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(File.ReadAllText(path));
        return result["panel_stale_seen"] is true && result["panel_kept_running"] is true &&
            result["suspend_hook_stops_sampling"] is true && result["manual_pause_preserved"] is true ? 0 : 1;
    }
}
