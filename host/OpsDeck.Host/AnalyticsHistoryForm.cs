using OpsDeck.Core;
namespace OpsDeck.Host;
public sealed class AnalyticsHistoryForm : Form
{
    private readonly AppEngine engine;private readonly QueueInfo? queue;private readonly CancellationTokenSource stop=new();private bool loading,closing;
    private readonly ComboBox account=new(){Width=220,DropDownStyle=ComboBoxStyle.DropDownList},metric=new(){Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly Button refresh=new(){Text="Geçmişi yenile (en az 60 sn)",AutoSize=true};
    private readonly TextBox header=new(){Dock=DockStyle.Top,Height=110,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
    private readonly TextBox summary=new(){Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
    private readonly DataGridView hourly=MakeGrid(),groups=MakeGrid();private readonly HistoryChart chart=new();
    private readonly System.Windows.Forms.Timer clock=new(){Interval=1000};private QueueHistory? queueData;private AiHistory? aiData;
    private sealed record Choice(string Id,string Name){public override string ToString()=>Name;}
    public AnalyticsHistoryForm(AppEngine engine,QueueInfo? queue=null)
    {
        this.engine=engine;this.queue=queue;Text="ALAZ OPSDECK — "+(queue==null?"Workers AI ölçümleri":"Kuyruk geçmişi");ClientSize=new Size(1100,650);MinimumSize=new Size(870,540);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
        var commands=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(8)};commands.Controls.Add(account);commands.Controls.Add(refresh);
        foreach(var a in engine.Accounts.Where(a=>a.Profile.Enabled&&(queue==null||a.Profile.AccountId.Equals(queue.AccountId,StringComparison.OrdinalIgnoreCase))))account.Items.Add(new Choice(a.Profile.AccountId,a.Profile.Name));
        if(account.Items.Count>0)account.SelectedIndex=0;
        var tabs=new TabControl{Dock=DockStyle.Fill};void Tab(string name,Control body){var page=new TabPage(name);page.Controls.Add(body);tabs.TabPages.Add(page);}
        Tab("Özet",summary);Tab("Saatlik veriler",hourly);Tab("İşlem / model grupları",groups);
        var graph=new Panel{Dock=DockStyle.Fill};graph.Controls.Add(chart);graph.Controls.Add(metric);Tab("Grafik",graph);
        Controls.Add(tabs);Controls.Add(header);Controls.Add(commands);
        var choices=queue==null?new[]{new Choice("count","Workers AI çağrı sayısı"),new Choice("neurons","Analitik neuron"),new Choice("input","Girdi tokenı"),new Choice("output","Çıktı tokenı"),new Choice("gateway_count","AI Gateway çağrı sayısı (ayrı kapsam)"),new Choice("gateway_errors","AI Gateway hata sayısı"),new Choice("gateway_limited","AI Gateway rate-limit işaretli istekler")}:
            new[]{new Choice("messages","Ortalama bekleyen mesaj"),new Choice("bytes","Ortalama birikim (bayt)"),new Choice("concurrency","Ortalama tüketici eşzamanlılığı"),new Choice("read","ReadMessage işlem sayısı"),new Choice("write","WriteMessage işlem sayısı"),new Choice("lag","ReadMessage ortalama gecikmesi (ms)"),new Choice("retries","ReadMessage başına ortalama retry")};
        metric.Items.AddRange(choices);metric.SelectedIndex=0;metric.SelectedIndexChanged+=(_,_)=>RenderChart();
        account.SelectedIndexChanged+=async(_,_)=>await LoadReading();refresh.Click+=async(_,_)=>await LoadReading();Shown+=async(_,_)=>{clock.Start();await LoadReading();};clock.Tick+=(_,_)=>RenderHeader();
        FormClosing+=(_,e)=>{closing=true;stop.Cancel();if(loading)e.Cancel=true;};FormClosed+=(_,_)=>{clock.Dispose();stop.Dispose();};
    }
    private static DataGridView MakeGrid()=>new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.DisplayedCells,SelectionMode=DataGridViewSelectionMode.FullRowSelect};
    private static void Columns(DataGridView grid,params string[] labels){grid.Rows.Clear();grid.Columns.Clear();foreach(string label in labels){int n=grid.Columns.Add("c"+grid.Columns.Count,label);grid.Columns[n].Width=label.Contains("Model")||label.Contains("Gateway")?230:150;grid.Columns[n].SortMode=DataGridViewColumnSortMode.NotSortable;}grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;}
    private static string N(double? value)=>value.HasValue?value.Value.ToString("N3"):"—";
    private async Task LoadReading()
    {
        if(loading||closing||account.SelectedItem is not Choice a)return;loading=true;refresh.Enabled=account.Enabled=false;
        queueData=null;aiData=null;summary.Text="Salt-okunur analitik sorgulanıyor…";header.Text=a.Name+" · Ölçüm bekleniyor";hourly.Rows.Clear();groups.Rows.Clear();chart.SetSeries([],[],"Ölçüm bekleniyor");
        try{if(queue==null){aiData=await engine.ReadAiHistory(a.Id,stop.Token);RenderAi(aiData);}else{queueData=await engine.ReadQueueHistory(queue,stop.Token);RenderQueue(queueData);}RenderHeader();RenderChart();}
        catch(OperationCanceledException){if(!closing)summary.Text="Okuma iptal edildi.";}
        catch(Exception e)when(e is ArgumentException or InvalidOperationException or SourceFailure){if(!closing){queueData=null;aiData=null;summary.Text="Gösterim açılamadı: "+e.Message;header.Text="Veri doğrulanamadı";}}
        finally{loading=false;refresh.Enabled=true;account.Enabled=queue==null;if(closing)Close();}
    }
    private void RenderHeader()
    {
        var w=queueData?.Window??aiData?.Window;var at=queueData?.CollectedAt??aiData?.CollectedAt;if(w==null||at==null)return;
        string state=(queueData?.State??aiData!.State).ToString();if(!Freshness.IsCurrent(at,DateTimeOffset.UtcNow,180))state+=" / Eski okuma";
        header.Text=$"{account.SelectedItem} · {(queue?.Name??"Workers AI / AI Gateway — ayrı veri kaynakları")}\r\nDurum: {state} · Okuma (yerel): {at.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\nPencere (UTC): {w.Start:yyyy-MM-dd HH:mm} – {w.End:yyyy-MM-dd HH:mm} (son 24 tamamlanmış saat; en az 3 dakika analitik payı)\r\nBoş saatler bilinmiyor; sıfıra tamamlanmaz. Bu ekran fatura veya kalan kota gösterimi değildir.";
    }
    private void RenderChart()
    {
        if(metric.SelectedItem is not Choice m)return;
        if(queueData!=null)chart.SetSeries(queueData.Window.Hours(),AnalyticsHistory.QueueSeries(queueData,m.Id),m.Name);
        else if(aiData!=null)chart.SetSeries(aiData.Window.Hours(),m.Id.StartsWith("gateway_",StringComparison.Ordinal)?GatewaySeries(aiData,m.Id):AnalyticsHistory.AiSeries(aiData,m.Id),m.Name);
    }
    private static double?[] GatewaySeries(AiHistory s,string metric)=>s.Window.Hours().Select(t=>AnalyticsHistory.Sum(s.Gateway.Rows.Where(r=>r.Hour==t).Select(r=>metric switch
    {"gateway_count"=>(double?)r.Count,"gateway_errors"=>r.Errors,"gateway_limited"=>r.RateLimited?r.Count:0,_=>throw new ArgumentException("Unknown gateway metric")}))).ToArray();
    private void RenderQueue(QueueHistory s)
    {
        string Part<T>(string label,AnalyticsSlice<T> p)=>$"{label}: {p.State} · {p.Rows.Length} grup\r\n{p.Detail}\r\n";
        summary.Text=Part("Birikim geçmişi",s.Backlog)+"\r\n"+Part("Tüketici geçmişi",s.Consumers)+"\r\n"+Part("Mesaj işlemleri",s.Operations)+"\r\n"+
            $"Dönen grupların işlem sayısı: {N(AnalyticsHistory.Sum(s.Operations.Rows.Select(r=>(double?)r.Count)))}\r\nDönen grupların billableOperations sayısı: {N(AnalyticsHistory.Sum(s.Operations.Rows.Select(r=>r.BillableOperations)))}\r\n\r\n"+
            "Saatlik backlog ve eşzamanlılık değerleri sunucu ortalamalarıdır; toplanmaz. Lag, yazımdan ilgili işleme kadar geçen ortalama milisaniyedir. Retry, işlem başına ortalamadır; toplam retry sayısı değildir. WriteMessage için bu iki alan uygulanamaz.\r\n\r\nDeleteMessage outcome success/dlq/fail değerleri kendi işlem gruplarıdır; bütün uygulamanın başarı oranı değildir. İşlem sayısı ve billableOperations farklıdır; burada USD maliyeti hesaplanmaz.\r\n\r\nHareketsiz kuyrukların geçmişi boş olabilir; bu, anlık birikimin sıfır olduğu anlamına gelmez. Ham mesaj içeriği toplanmaz. İptal, 60 saniye önbellek ve Retry-After korunur.";
        Columns(hourly,"Saat UTC","Ort. mesaj","Ort. bayt","Ort. eşzamanlılık","ReadMessage","WriteMessage","Read lag (ms)","Read retry / işlem");
        string[] keys=["messages","bytes","concurrency","read","write","lag","retries"];var series=keys.Select(k=>AnalyticsHistory.QueueSeries(s,k)).ToArray();var hours=s.Window.Hours();
        for(int i=0;i<hours.Length;i++)hourly.Rows.Add(new[]{hours[i].ToString("MM-dd HH:mm")}.Concat(series.Select(x=>N(x[i]))).Cast<object>().ToArray());
        Columns(groups,"Saat UTC","İşlem","Outcome","İşlem sayısı","Billable operations","Bayt","Lag ort. (ms)","Retry ort. / işlem");
        foreach(var r in s.Operations.Rows)groups.Rows.Add(r.Hour.ToString("MM-dd HH:mm"),r.Action,r.Outcome,N(r.Count),N(r.BillableOperations),N(r.Bytes),N(r.LagMs),N(r.Retries));
    }
    private void RenderAi(AiHistory s)
    {
        double? nativeCount=AnalyticsHistory.Sum(s.Inference.Rows.Select(r=>(double?)r.Count)),gatewayCount=AnalyticsHistory.Sum(s.Gateway.Rows.Select(r=>(double?)r.Count));
        summary.Text=$"DOĞRUDAN WORKERS AI — {s.Inference.State}\r\n{s.Inference.Detail}\r\nÇağrı: {N(nativeCount)}\r\nGirdi / çıktı tokenı: {N(AnalyticsHistory.Sum(s.Inference.Rows.Select(r=>r.InputTokens)))} / {N(AnalyticsHistory.Sum(s.Inference.Rows.Select(r=>r.OutputTokens)))}\r\nAnalitik neuron: {N(AnalyticsHistory.Sum(s.Inference.Rows.Select(r=>r.Neurons)))}\r\nToplam çıkarım süresi: {N(AnalyticsHistory.Sum(s.Inference.Rows.Select(r=>r.InferenceTimeMs)))} ms\r\n\r\n"+
            $"AI GATEWAY — {s.Gateway.State} (AYRI KAPSAM)\r\n{s.Gateway.Detail}\r\nGateway çağrısı: {N(gatewayCount)}\r\nHata işaretli: {N(AnalyticsHistory.Sum(s.Gateway.Rows.Select(r=>r.Errors)))}\r\nCache'den: {N(AnalyticsHistory.Sum(s.Gateway.Rows.Select(r=>r.Cached)))}\r\nRate-limit işaretli: {N(AnalyticsHistory.Sum(s.Gateway.Rows.Select(r=>(double?)(r.RateLimited?r.Count:0))))}\r\n\r\n"+
            "Workers AI ve AI Gateway sayıları toplanmaz; aynı isteği iki kez temsil edebilirler. Gateway diğer sağlayıcıları ve cache yanıtlarını da içerir; sağlayıcı/model sütunlarından ayrılır. Doğrudan API veya binding çağrıları Gateway kapsamının dışında olabilir.\r\n\r\nNeuron fatura, kalan ücretsiz kota veya USD değildir. 24 saatlik geçmişten anlık rate-limit payı çıkarılmaz. Kodlar Workers AI'ın döndürdüğü errorCode değerleridir; bilinmeyen kodların anlamı tahmin edilmez. Süre toplamdır, P50/P99 değildir. Model alanı API kimliğidir; model kataloğu veya anlık kullanılabilirlik garantisi değildir.\r\n\r\nHam prompt, yanıt, metadata, kullanıcı kimliği veya inference çalıştırılması yoktur.";
        Columns(hourly,"Saat UTC","Workers AI çağrı","Neuron","Girdi tokenı","Çıktı tokenı","Gateway çağrı","Gateway hata","Gateway rate limit");
        var times=s.Window.Hours();var series=new[]{AnalyticsHistory.AiSeries(s,"count"),AnalyticsHistory.AiSeries(s,"neurons"),AnalyticsHistory.AiSeries(s,"input"),AnalyticsHistory.AiSeries(s,"output"),GatewaySeries(s,"gateway_count"),GatewaySeries(s,"gateway_errors"),GatewaySeries(s,"gateway_limited")};
        for(int i=0;i<times.Length;i++)hourly.Rows.Add(new[]{times[i].ToString("MM-dd HH:mm")}.Concat(series.Select(x=>N(x[i]))).Cast<object>().ToArray());
        Columns(groups,"Kaynak","Saat UTC","Gateway / çağrı kaynağı","Provider","Model / API kimliği","errorCode / rate limit","Çağrı","Girdi tokenı","Çıktı tokenı","Neuron","Toplam süre (ms)","Gateway hata","Gateway cache");
        foreach(var r in s.Inference.Rows)groups.Rows.Add("Workers AI",r.Hour.ToString("MM-dd HH:mm"),r.RequestSource,"Workers AI",r.Model,r.ErrorCode.ToString(),N(r.Count),N(r.InputTokens),N(r.OutputTokens),N(r.Neurons),N(r.InferenceTimeMs),"—","—");
        foreach(var r in s.Gateway.Rows)groups.Rows.Add("AI Gateway",r.Hour.ToString("MM-dd HH:mm"),r.Gateway,r.Provider,r.Model,r.RateLimited?"Rate limited":"—",N(r.Count),N(r.InputTokens),N(r.OutputTokens),"—","—",N(r.Errors),N(r.Cached));
    }
}
