using OpsDeck.Core;
namespace OpsDeck.Host;

public sealed class QueuesForm : Form
{
    private readonly AppEngine engine;private readonly CancellationTokenSource stop=new();private bool loading,closing;
    private readonly ComboBox account=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=230};
    private readonly Button listRefresh=new(){Text="Kuyrukları listele / yenile",AutoSize=true};
    private readonly Button metrics=new(){Text="Seçili kuyruk birikimi",AutoSize=true,Enabled=false};
    private readonly Button history=new(){Text="Seçili kuyruk geçmişi (24 sa)",AutoSize=true,Enabled=false};
    private readonly ListBox queues=new(){Dock=DockStyle.Left,Width=350,DisplayMember="Name",IntegralHeight=false,HorizontalScrollbar=true};
    private readonly TextBox text=new(){Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
    private QueueInventory? inventory;
    private sealed record Choice(string Id,string Name){public override string ToString()=>Name;}
    public QueuesForm(AppEngine engine)
    {
        this.engine=engine;Text="ALAZ OPSDECK — Queues ölçümleri";ClientSize=new Size(1100,580);MinimumSize=new Size(850,480);
        Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
        var commands=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(8)};
        commands.Controls.Add(account);commands.Controls.Add(listRefresh);commands.Controls.Add(metrics);commands.Controls.Add(history);
        Controls.Add(text);Controls.Add(queues);Controls.Add(commands);
        foreach(var a in engine.Accounts.Where(a=>a.Profile.Enabled))account.Items.Add(new Choice(a.Profile.AccountId,a.Profile.Name));
        if(account.Items.Count>0)account.SelectedIndex=0;
        account.SelectedIndexChanged+=async(_,_)=>await LoadList();listRefresh.Click+=async(_,_)=>await LoadList();
        history.Click+=(_,_)=>{if(loading||closing||queues.SelectedItem is not QueueInfo q)return;using var dialog=new AnalyticsHistoryForm(engine,q);dialog.ShowDialog(this);};
        metrics.Click+=async(_,_)=>await LoadMetrics();queues.SelectedIndexChanged+=(_,_)=>ShowSelection();Shown+=async(_,_)=>await LoadList();
        FormClosing+=(_,e)=>{closing=true;stop.Cancel();if(loading)e.Cancel=true;};FormClosed+=(_,_)=>stop.Dispose();
    }
    private static string N(double? value,string unit="")=>value.HasValue?value.Value.ToString("N0")+unit:"—";
    private void Busy(bool value){loading=value;account.Enabled=listRefresh.Enabled=queues.Enabled=!value;history.Enabled=metrics.Enabled=!value&&queues.SelectedItem is QueueInfo;}
    private string Header()=>inventory==null?"Kuyruk listesi henüz okunmadı.":
        $"Liste: {inventory.State} | {(inventory.Complete?"Tamam":"Kısmi / bilinmiyor")} | {inventory.Items.Length} kaynak\r\nOkuma (yerel): {inventory.CollectedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n{inventory.Detail}\r\n\r\n";
    private static string Metadata(QueueInfo q)=>$"Kuyruk: {q.Name}\r\nKimlik: {q.Id}\r\nTeslimat duraklatılmış: {(q.DeliveryPaused.HasValue?(q.DeliveryPaused.Value?"Evet":"Hayır"):"Bilinmiyor")}\r\nTüketici sayısı: {N(q.Consumers)}\r\nSaklama süresi: {N(q.RetentionSeconds," saniye")}\r\nJurisdiction: {(q.Jurisdiction.Length==0?"default / unspecified":q.Jurisdiction)}\r\n\r\n";
    private void ShowSelection()
    {
        history.Enabled=metrics.Enabled=!loading&&queues.SelectedItem is QueueInfo;
        text.Text=Header()+(queues.SelectedItem is QueueInfo q?Metadata(q)+"Birikimi görmek için 'Seçili kuyruk birikimi' düğmesini kullanın.":
            inventory is{Complete:true,Items.Length:0}?"Bu API okumasında kuyruk listesi boş. Bu bir izin hatası veya eksik ölçüm değildir.":"Bir kuyruk seçin; izin reddi boş liste diye sunulmaz.");
    }
    private async Task LoadList()
    {
        if(loading||closing)return;if(account.SelectedItem is not Choice a){text.Text="Önce etkin Cloudflare hesabını yapılandırın.";return;}
        Busy(true);inventory=null;queues.Items.Clear();text.Text="Salt-okunur kuyruk listesi alınıyor…";
        try{inventory=await engine.ReadQueues(a.Id,stop.Token);queues.Items.Clear();queues.Items.AddRange(inventory.Items.Cast<object>().ToArray());queues.HorizontalExtent=inventory.Items.Select(q=>TextRenderer.MeasureText(q.Name,queues.Font).Width+12).DefaultIfEmpty(0).Max();if(queues.Items.Count>0)queues.SelectedIndex=0;ShowSelection();}
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception e)when(e is ArgumentException or InvalidOperationException){if(!closing)text.Text="Liste açılamadı: "+e.Message;}
        finally{Busy(false);if(closing)Close();}
    }
    private async Task LoadMetrics()
    {
        if(loading||closing||queues.SelectedItem is not QueueInfo q)return;Busy(true);
        try
        {
            var s=await engine.ReadQueueBacklog(q,stop.Token);
            text.Text=Header()+Metadata(q)+$"Birikim: {s.State}{(!s.IsCurrent(DateTimeOffset.UtcNow)?" / Eski okuma":"")}\r\nÖlçüm zamanı (yerel): {s.CollectedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\nBekleyen mesaj: {N(s.Messages)}\r\nYaklaşık toplam boyut: {N(s.Bytes," bayt")}\r\nEn eski mesajın ölçüm anındaki yaşı: {N(s.OldestAgeSeconds," saniye")}\r\n\r\n{s.Detail}\r\n\r\nEn az 60 saniye önbellek; 429 / Retry-After korunur. Bu ekran Cloudflare'da değişiklik yapmaz.";
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception e)when(e is ArgumentException or InvalidOperationException){if(!closing)text.Text="Birikim açılamadı: "+e.Message;}
        finally{Busy(false);if(closing)Close();}
    }
}
