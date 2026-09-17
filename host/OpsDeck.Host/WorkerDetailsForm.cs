using OpsDeck.Core;
namespace OpsDeck.Host;
public sealed class WorkerDetailsForm : Form
{
    private readonly AppEngine engine;private readonly ResourceKey key;
    private readonly CancellationTokenSource stop=new();
    private readonly TextBox content=new(){Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical};
    private readonly Button refresh=new(){Text="Ölçümü yenile (en az 60 sn)",AutoSize=true,Dock=DockStyle.Bottom};
    private bool loading,closing;
    public WorkerDetailsForm(AppEngine engine,ResourceKey key)
    {
        this.engine=engine;this.key=key;Text="ALAZ OPSDECK — Worker ölçümleri";ClientSize=new Size(800,460);MinimumSize=new Size(650,400);Font=new Font("Segoe UI",11);StartPosition=FormStartPosition.CenterParent;
        Controls.Add(content);Controls.Add(refresh);content.Text="Seçili Worker için salt-okunur analiz bekleniyor.";
        Shown+=async(_,_)=>await LoadReading();refresh.Click+=async(_,_)=>await LoadReading();
        FormClosing+=(_,e)=>{closing=true;stop.Cancel();if(loading)e.Cancel=true;};FormClosed+=(_,_)=>stop.Dispose();
    }
    private async Task LoadReading()
    {
        if(loading||closing)return;loading=true;refresh.Enabled=false;
        try
        {
            var s=await engine.ReadWorkerDetail(key,stop.Token);
            if(closing)return;
            string N(double? v,string unit="")=>v.HasValue?v.Value.ToString("0.###")+unit:"—";
            content.Text=$"Worker: {s.Key.Id}\r\nDurum: {(!s.IsCurrent(DateTimeOffset.UtcNow)?"Eski okuma":s.State.ToString())}\r\nPencere (UTC): {s.Start:yyyy-MM-dd HH:mm} – {s.End:HH:mm}\r\nOkuma (yerel): {s.CollectedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n\r\nİstek: {N(s.Requests)}\r\nHata: {N(s.Errors)}  |  Hata oranı: {N(s.ErrorPercent," %")}\r\nCPU P50 / P99: {N(s.CpuP50Ms," ms")} / {N(s.CpuP99Ms," ms")}\r\nDuvar saati P50 / P99: {N(s.WallP50Ms," ms")} / {N(s.WallP99Ms," ms")}\r\n\r\n{s.Detail}\r\n\r\nAynı kaynak için 60 saniye içinde önbellek kullanılır. Canlı kaynak, ayar, kod veya dağıtım değiştirilmez.";
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception e)when(e is ArgumentException or InvalidOperationException){if(!closing)content.Text="Ölçüm açılamadı: "+e.Message;}
        finally{loading=false;if(closing)Close();else refresh.Enabled=true;}
    }
}
