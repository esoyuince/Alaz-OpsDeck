using Microsoft.Win32;
using OpsDeck.Core;
namespace OpsDeck.Host;
public sealed class MainForm : Form
{
    private readonly LocalSettings settings;private AppEngine? engine;
    private readonly DataGridView grid=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private readonly Label connection=new(){Dock=DockStyle.Top,Height=72,Padding=new Padding(14)},sites=new(){Dock=DockStyle.Bottom,Height=48,Padding=new Padding(14,10,14,8)};
    private readonly Label headerStatus=OpsDeckTheme.Badge("INITIALIZING",OpsDeckTheme.Amber);
    private readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    private readonly NotifyIcon tray=new(){Icon=SystemIcons.Application,Text="ALAZ OPSDECK",Visible=true};
    private readonly Button configure=new(){Text="Bilgisayar / İki Cloudflare hesabı",AutoSize=true};private bool exiting,sessionLocked;
    public MainForm(LocalSettings settings,bool startInTray=false)
    {
        this.settings=settings;Text="ALAZ OPSDECK — M6.15-B / Codex Quota Fix";ClientSize=new Size(1280,780);MinimumSize=new Size(1040,660);StartPosition=FormStartPosition.CenterScreen;
        OpsDeckTheme.Apply(this);if(startInTray){WindowState=FormWindowState.Minimized;ShowInTaskbar=false;}
        grid.Columns.Add("source","KAYNAK");grid.Columns.Add("state","DURUM");grid.Columns.Add("value","ÖLÇÜM");grid.Columns.Add("detail","AYRINTI / VERİ YAŞI");
        grid.Columns[0].Width=190;grid.Columns[1].Width=115;grid.Columns[2].Width=190;grid.Columns[3].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;OpsDeckTheme.StyleGrid(grid);
        connection.BackColor=OpsDeckTheme.Surface;connection.ForeColor=OpsDeckTheme.Muted;connection.Font=OpsDeckTheme.UiFont(9.5f);connection.BorderStyle=BorderStyle.FixedSingle;
        sites.BackColor=OpsDeckTheme.Surface;sites.ForeColor=OpsDeckTheme.Muted;sites.Font=OpsDeckTheme.UiFont(9f);sites.BorderStyle=BorderStyle.FixedSingle;

        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,BackColor=OpsDeckTheme.Back,Margin=Padding.Empty,Padding=Padding.Empty};
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,240));root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,96));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var header=new Panel{Dock=DockStyle.Fill,BackColor=OpsDeckTheme.Surface,Padding=new Padding(18,10,18,12),Margin=Padding.Empty};root.Controls.Add(header,0,0);root.SetColumnSpan(header,2);
        var brand=new Label{Text="ALAZ OPSDECK",AutoSize=true,Location=new Point(20,10),ForeColor=OpsDeckTheme.Text,Font=OpsDeckTheme.UiFont(20f,FontStyle.Bold)};header.Controls.Add(brand);
        var subtitle=new Label{Text="LOCAL OPERATIONS  •  SECURE TELEMETRY  •  CLOUD + AGENTS",AutoSize=true,Location=new Point(22,58),ForeColor=OpsDeckTheme.Muted,Font=OpsDeckTheme.UiFont(9f,FontStyle.Bold)};header.Controls.Add(subtitle);
        var statusHost=new Panel{Dock=DockStyle.Right,Width=145,BackColor=OpsDeckTheme.Surface,Padding=new Padding(8,22,14,18)};headerStatus.AutoSize=false;headerStatus.Dock=DockStyle.Fill;headerStatus.TextAlign=ContentAlignment.MiddleCenter;headerStatus.Margin=Padding.Empty;statusHost.Controls.Add(headerStatus);header.Controls.Add(statusHost);

        var nav=new Panel{Dock=DockStyle.Fill,BackColor=OpsDeckTheme.Surface,Padding=new Padding(16,6,14,6),Margin=Padding.Empty};root.Controls.Add(nav,0,1);
        var navFlow=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=false,BackColor=OpsDeckTheme.Surface,Padding=Padding.Empty,Margin=Padding.Empty};nav.Controls.Add(navFlow);
        navFlow.Controls.Add(OpsDeckTheme.Section("Monitoring"));
        Button Nav(string text){var b=OpsDeckTheme.NavButton(text);navFlow.Controls.Add(b);return b;}
        var overview=Nav("  Ops Overview");var resources=Nav("  Kaynaklar / Projeler");var queuesButton=Nav("  Queues / Kuyruklar");var aiButton=Nav("  Workers AI / Gateway");
        navFlow.Controls.Add(OpsDeckTheme.Section("Agents & Analysis"));
        var agents=Nav("  Agent / Tool Center");var timelineButton=Nav("  Olay Zaman Çizelgesi");var trends=Nav("  Trend / History");var lifecycle=Nav("  Lifecycle / Soak");var reliability=Nav("  Reliability / Soak");var alerts=Nav("  Alert Center");var brief=Nav("  Ops Brief");var deviation=Nav("  Baseline / Deviation");
        var systemBar=new TableLayoutPanel{Width=204,Height=82,ColumnCount=2,RowCount=2,BackColor=OpsDeckTheme.Surface,Padding=new Padding(0,6,0,0),Margin=new Padding(0,6,0,0)};
        systemBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));systemBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));systemBar.RowStyles.Add(new RowStyle(SizeType.Percent,50));systemBar.RowStyles.Add(new RowStyle(SizeType.Percent,50));navFlow.Controls.Add(systemBar);
        configure.Text="Ayarlar";configure.AutoSize=false;configure.Dock=DockStyle.Fill;configure.Margin=new Padding(0,0,3,3);OpsDeckTheme.StyleButton(configure,true);configure.Padding=new Padding(3,0,3,0);configure.Font=OpsDeckTheme.UiFont(8.5f);
        var usbPause=new Button{Text="USB",Dock=DockStyle.Fill,Margin=new Padding(3,0,0,3)};OpsDeckTheme.StyleButton(usbPause);usbPause.Padding=new Padding(3,0,3,0);usbPause.Font=OpsDeckTheme.UiFont(8.5f);var hide=new Button{Text="Tepsi",Dock=DockStyle.Fill,Margin=new Padding(0,0,3,0)};OpsDeckTheme.StyleButton(hide);hide.Padding=new Padding(3,0,3,0);hide.Font=OpsDeckTheme.UiFont(8.5f);var quit=new Button{Text="Çıkış",Dock=DockStyle.Fill,Margin=new Padding(3,0,0,0)};OpsDeckTheme.StyleButton(quit,false,true);quit.Padding=new Padding(3,0,3,0);quit.Font=OpsDeckTheme.UiFont(8.5f);
        systemBar.Controls.Add(configure,0,0);systemBar.Controls.Add(usbPause,1,0);systemBar.Controls.Add(hide,0,1);systemBar.Controls.Add(quit,1,1);

        var content=new Panel{Dock=DockStyle.Fill,BackColor=OpsDeckTheme.Back,Padding=new Padding(16),Margin=Padding.Empty};root.Controls.Add(content,1,1);
        var telemetryCard=new Panel{Dock=DockStyle.Fill,BackColor=OpsDeckTheme.Surface,Padding=new Padding(1)};content.Controls.Add(telemetryCard);telemetryCard.Controls.Add(grid);telemetryCard.Controls.Add(connection);telemetryCard.Controls.Add(sites);
        Controls.Add(root);

        usbPause.Click+=(_,_)=>{if(engine==null)return;engine.SerialPaused=!engine.SerialPaused;usbPause.Text=engine.SerialPaused?"  USB Yeniden Bağlan":"  USB Duraklat";};
        resources.Click+=(_,_)=>{if(engine==null)return;try{using var dialog=new ResourcesForm(engine,settings);dialog.ShowDialog(this);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException){MessageBox.Show(this,"Proje listesi açılamadı: "+e.GetType().Name,"ALAZ OPSDECK");}};
        queuesButton.Click+=(_,_)=>{if(engine==null)return;using var dialog=new QueuesForm(engine);dialog.ShowDialog(this);};
        aiButton.Click+=(_,_)=>{if(engine==null)return;using var dialog=new AnalyticsHistoryForm(engine);dialog.ShowDialog(this);};
        overview.Click+=(_,_)=>{if(engine==null)return;using var dialog=new M5OverviewForm(engine);dialog.ShowDialog(this);};
        agents.Click+=(_,_)=>{if(engine==null)return;using var dialog=new AgentTaskCenterForm(engine);dialog.ShowDialog(this);};
        timelineButton.Click+=(_,_)=>{if(engine==null)return;using var dialog=new OperationalTimelineForm(engine);dialog.ShowDialog(this);};
        trends.Click+=(_,_)=>{if(engine==null)return;using var dialog=new TelemetryHistoryForm(engine);dialog.ShowDialog(this);};
        lifecycle.Click+=(_,_)=>{if(engine==null)return;using var dialog=new LifecycleHistoryForm(engine);dialog.ShowDialog(this);};
        reliability.Click+=(_,_)=>{if(engine==null)return;using var dialog=new ReliabilitySoakForm(engine);dialog.ShowDialog(this);};
        alerts.Click+=(_,_)=>{if(engine==null)return;using var dialog=new AlertCenterForm(engine);dialog.ShowDialog(this);};
        brief.Click+=(_,_)=>{if(engine==null)return;using var dialog=new OpsBriefForm(engine);dialog.ShowDialog(this);};
        deviation.Click+=(_,_)=>{if(engine==null)return;using var dialog=new BaselineDeviationForm(engine);dialog.ShowDialog(this);};
        hide.Click+=(_,_)=>{ShowInTaskbar=false;Hide();};quit.Click+=async(_,_)=>await Exit();
        void ShowWindow(){ShowInTaskbar=true;Show();WindowState=FormWindowState.Normal;Activate();}
        var menu=new ContextMenuStrip();menu.Items.Add("ALAZ OPSDECK'i aç",null,(_,_)=>ShowWindow());menu.Items.Add("Durdur ve çık",null,async(_,_)=>await Exit());tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>ShowWindow();
        configure.Click+=async(_,_)=>{using var dialog=new SettingsForm(settings);if(dialog.ShowDialog(this)==DialogResult.OK){configure.Enabled=false;try{if(engine!=null)await engine.DisposeAsync();engine=new(settings.Load(),settings){Locked=sessionLocked};engine.Start();engine.StartCodexTelemetry();}finally{configure.Enabled=true;}}};
        foreach(var label in new[]{connection,sites}){label.TextChanged+=(_,_)=>FitLabel(label);label.SizeChanged+=(_,_)=>FitLabel(label);}
        timer.Tick+=(_,_)=>RefreshStatus();Shown+=(_,_)=>{Text="ALAZ OPSDECK — M6.15-B / Codex Quota Fix";engine=new(settings.Load(),settings){Locked=sessionLocked};engine.Start();engine.StartCodexTelemetry();timer.Start();if(startInTray)BeginInvoke(new Action(()=>{ShowInTaskbar=false;Hide();}));};
        FormClosing+=(_,e)=>{if(!exiting){e.Cancel=true;ShowInTaskbar=false;Hide();}};SystemEvents.SessionSwitch+=SessionChanged;SystemEvents.PowerModeChanged+=PowerChanged;
    }
    private static void FitLabel(Label label)
    {
        int width=Math.Max(80,label.ClientSize.Width-label.Padding.Horizontal);
        int h=TextRenderer.MeasureText(label.Text,label.Font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl|TextFormatFlags.NoPrefix).Height+label.Padding.Vertical+4;
        if(label.Height!=h)label.Height=h;
    }
    private void SessionChanged(object sender,SessionSwitchEventArgs e){if(e.Reason==SessionSwitchReason.SessionLock)sessionLocked=true;if(e.Reason==SessionSwitchReason.SessionUnlock)sessionLocked=false;if(engine!=null)engine.Locked=sessionLocked;}
    private void PowerChanged(object sender,PowerModeChangedEventArgs e)
    {
        if(e.Mode==PowerModes.Suspend)engine?.SuspendObservation();
        else if(e.Mode==PowerModes.Resume)engine?.ResumeObservation();
    }
    private static string N(double? v,string unit="",string format="0.0")=>v.HasValue?v.Value.ToString(format)+unit:"—";
    private void RefreshStatus()
    {
        if(engine==null)return;var f=engine.Fleet;var p=engine.Pc;var now=DateTimeOffset.UtcNow;var liveLink=engine.LinkHealth;headerStatus.Text=sessionLocked?"LOCKED":liveLink.Recovering?"RECOVERING":liveLink.State==SourceState.Ok?"SYSTEM OK":liveLink.State.ToString().ToUpperInvariant();headerStatus.ForeColor=sessionLocked?OpsDeckTheme.Amber:liveLink.Recovering?OpsDeckTheme.Amber:liveLink.State==SourceState.Ok?OpsDeckTheme.Green:OpsDeckTheme.Muted;connection.Text=$"{engine.SerialStatus} | Örnek: {engine.Samples}\n{engine.PanelEvidence}";
        int i=0;void Row(string name,string state,string value,string detail){while(grid.Rows.Count<=i)grid.Rows.Add();var row=grid.Rows[i++];row.SetValues(name,state,value,detail);OpsDeckTheme.TintStateCell(row.Cells[1],state);}
        var assessment=engine.ReadOperationalAssessment();var alerts=engine.ReadAlertRollup();
        Row("Alert state",AlertSemantics.Label(alerts.Level),$"{assessment.Score}/100",$"{alerts.Primary} · {alerts.Items.Length} active alert(s). Warning-only presentation; no automatic control.");
        var correlations=engine.ReadOperationalCorrelations();
        if(correlations.Length>0){var c=correlations[0];Row("Recent correlation",c.Severity.ToString(),$"{c.Domains.Length} domains",c.Summary);}else Row("Recent correlation","None","—","No cross-domain event cluster in the last 24 hours.");
        var link=engine.LinkHealth;string ackAge=AgentTaskPresentation.Age(link.LastForwardAt,now);string recoveryAge=AgentTaskPresentation.Age(link.LastRecoveryAt,now);string hostAge=AgentTaskPresentation.Age(link.StartedAt,now);
        string linkValue=link.Recovering?"RECOVERING":link.State==SourceState.Ok?"OK":link.State.ToString().ToUpperInvariant();
        string lastRecovery=link.LastAction==SerialRecoveryAction.None?"none":$"{LinkHealthSnapshot.ActionLabel(link.LastAction)} / {LinkHealthSnapshot.ReasonLabel(link.LastReason)} · {recoveryAge} ago";
        Row("Panel link / recovery",link.State.ToString(),linkValue,$"Forward ACK {ackAge} ago · reopen {link.ReopenCount} · ROM probe {link.RomProbeCount} · recovered {link.RecoveredCount} · last {lastRecovery} · host up {hostAge}.");
        var edge=engine.EdgeNode;var es=edge.Snapshot;
        string edgeValue=es==null?"—":$"{es.TempC:0.0} °C · disk %{es.DiskUsedPct}";
        string edgeDetail=es==null?edge.Detail:$"{es.Host} · Tailscale {es.TailscaleIp} · RAM {(es.MemTotalKb-es.MemAvailableKb)/1048576d:0.00}/{es.MemTotalKb/1048576d:0.00} GiB · MQTT {es.Services.Mosquitto} · HA {es.Services.Homeassistant} · snapshot {Freshness.AgeSeconds(es.Ts,now)} sn";
        Row("ALAZ EdgeNode",edge.State.ToString(),edgeValue,edgeDetail);
        Row("CPU / RAM",engine.PcState,N(p.Cpu,"%"),$"RAM {N(p.RamUsedGib)}/{N(p.RamTotalGib)} GiB");
        Row("CPU sıcaklığı",!engine.PcIsFresh?"Eski/veri yok":p.CpuTemp.HasValue?"Ölçüm":"Kaynak yok",N(p.CpuTemp," °C"),p.CpuSensorStatus+". Intel GPU sıcaklığı CPU olarak kullanılmaz.");
        Row("Chassis sıcaklığı",!engine.PcIsFresh?"Eski/veri yok":p.ChassisTemp.HasValue?"Ölçüm":"Kaynak yok",N(p.ChassisTemp," °C"),p.ChassisSensorStatus+". OMEN notebook IR sıcaklık kanalı.");
        foreach(var volume in p.Volumes??[]){
            var diskLevel=AlertSemantics.VolumeLevel(volume,engine.PcIsFresh,now);
            string state=!engine.PcIsFresh?"Eski/veri yok":diskLevel!=AlertLevel.Info?AlertSemantics.Label(diskLevel):volume.State(now).ToString();
            string diskPolicy=diskLevel==AlertLevel.Info?"":$" · Warning-only storage policy: ATTENTION >= {AlertSemantics.DiskAttentionPercent:0}%, DEGRADED >= {AlertSemantics.DiskDegradedPercent:0}%.";
            Row("Disk / hacim "+volume.Id,state,N(volume.UsedPercent,"% dolu"),
                $"Boş {N(volume.FreeGib)}/{N(volume.TotalGib)} GiB; kullanıcıya ayrılan boş {N(volume.AvailableBytes.HasValue?volume.AvailableBytes.Value/1073741824d:null)} GiB. {volume.Status}; fiziksel HDD/SSD türü değildir. Örnek yaşı {Freshness.AgeSeconds(volume.ObservedAt,now)} sn.{diskPolicy}");
        }
        if(p.Volumes is not {Length:>0})Row("Disk / hacim","Veri yok","—","Okunabilir yerel hacim kapasitesi henüz yok.");
        Row("Intel Arc / iGPU",!engine.PcIsFresh?"Eski/veri yok":p.IntelGpu.HasValue?"WDDM":"Veri yok",N(p.IntelGpu,"%"),$"Paylaşılan {N(p.IntelSharedGib)} GiB; sınır {N(p.IntelSharedLimitGib)} GiB (ayrılmış VRAM değil).");
        Row("NVIDIA / dGPU",!engine.PcIsFresh?"Eski/veri yok":p.Gpu.HasValue?"WDDM":"Veri yok",N(p.Gpu,"%"),$"WDDM ayrılmış {N(p.VramUsedGib)}/{N(p.VramTotalGib)} GiB; {p.NvidiaSensorStatus}");
        Row("NVIDIA sıcaklığı",!engine.PcIsFresh?"Eski/veri yok":p.GpuTemp.HasValue?"Ölçüm":"Kaynak yok",N(p.GpuTemp," °C"),p.NvidiaSensorStatus);
        Row("Fan 1 / Fan 2",!engine.PcIsFresh?"Eski/veri yok":p.Fan1Rpm.HasValue||p.Fan2Rpm.HasValue?"Gerçek RPM":"Kaynak yok",$"{N(p.Fan1Rpm," RPM","0")} / {N(p.Fan2Rpm," RPM","0")}",p.FanSensorStatus);
        Row("Yerel Codex",f.Agents.CodexState.ToString(),sessionLocked?"Kilitli":f.Agents.CodexCount>=0?$"{f.Agents.CodexCount} süreç":"—","Gerçek Codex.exe süreç varlığı; görev veya thread sayısı değildir.");
        var managed=engine.ManagedCodex;Row("Managed Codex",managed.State.ToString(),sessionLocked?"Kilitli":managed.Activity,$"OpsDeck-owned thread {managed.OwnedThreads} · target {(string.IsNullOrWhiteSpace(managed.ActiveTarget)?"—":managed.ActiveTarget)} · reconcile {(managed.ReconciliationRequired?"required":"no")}. Yalnız OpsDeck'in açtığı thread/turn'ler kontrol edilir.");
        var rdc=engine.DesktopCommander;string rdcValue=sessionLocked?"Kilitli":rdc.ProcessCount>0?$"LOCAL ACTIVE · {rdc.ProcessCount} proc":"OFFLINE";string rdcDetail=sessionLocked?"Windows oturumu kilitli; RDC ayrıntısı gizlendi.":rdc.TotalCalls.HasValue?$"{rdc.TotalCalls:N0} tool call · {rdc.Sessions??0} session · başarı {(rdc.SuccessPermille.HasValue?rdc.SuccessPermille.Value/10d:0):0.0}% · son tool {(string.IsNullOrWhiteSpace(rdc.LastTool)?"—":rdc.LastTool)}":"Remote process gözlemi var; yerel usageStats bulunamadı.";Row("Remote Desktop Commander",rdc.State.ToString(),rdcValue,rdcDetail);
        string Tokens(long? value)=>!value.HasValue?"—":value.Value>=1_000_000_000?$"{value.Value/1_000_000_000d:0.00}B":value.Value>=1_000_000?$"{value.Value/1_000_000d:0.0}M":value.Value>=1_000?$"{value.Value/1_000d:0.0}K":value.Value.ToString("N0");
        string Window(CodexQuotaWindow? window)=>window==null?"—":$"kullanım %{window.UsedPercent} · kalan %{window.RemainingPercent}"+(window.WindowMinutes.HasValue?$" / {window.WindowMinutes.Value/60d:0.#} sa":"")+(window.ResetsAt.HasValue?$" · reset {window.ResetsAt.Value.ToLocalTime():dd.MM HH:mm}":"");
        var cu=engine.CodexUsage;string codexUsageState=sessionLocked?"Kilitli":cu.State.ToString();
        string codexUsageValue=sessionLocked?"—":cu.State==SourceState.Ok?$"Bugün {Tokens(cu.TodayTokens)} · toplam {Tokens(cu.LifetimeTokens)}":"—";
        string codexUsageDetail=sessionLocked?"Windows oturumu kilitli; Codex kullanım bilgisi gizlendi.":"Codex kullanım bilgisi henüz alınmadı.";
        if(!sessionLocked)codexUsageDetail=$"Plan {(string.IsNullOrWhiteSpace(cu.PlanType)?"—":cu.PlanType)} · Codex {(!string.IsNullOrWhiteSpace(cu.Version)?cu.Version:"—")} · günlük tepe {Tokens(cu.PeakDailyTokens)} · seri {cu.CurrentStreakDays?.ToString()??"—"} gün. {cu.Detail}";
        Row("Codex token kullanımı",codexUsageState,codexUsageValue,codexUsageDetail);
        if(!sessionLocked)foreach(var quota in cu.EffectiveQuotas.Take(4))Row("Codex kota / "+quota.Name,cu.State.ToString(),Window(quota.Primary),quota.Secondary==null?$"Plan {(string.IsNullOrWhiteSpace(quota.PlanType)?"—":quota.PlanType)}":$"İkincil: {Window(quota.Secondary)} · plan {(string.IsNullOrWhiteSpace(quota.PlanType)?"—":quota.PlanType)}");
        var bt=f.Agents.Tasks;string I(int? v)=>AgentTaskPresentation.Count(v);
        string taskState=sessionLocked?"Kilitli":(bt?.State??SourceState.Setup).ToString();
        string taskValue=sessionLocked?"—":$"Aktif {I(bt?.Active)} · Açık {I(bt?.Open)} · Alındı {I(bt?.Claimed)} · Çalışan {I(bt?.InProgress)} · Onay {I(bt?.NeedsApproval)}";
        string taskAge=bt?.LatestTaskAt.HasValue==true?$"son görev güncellemesi {AgentTaskPresentation.Age(bt.LatestTaskAt,now)} önce":"son görev güncellemesi yok";
        string eventLabel=AgentTaskPresentation.EventLabel(bt?.LatestEvent??"");
        string eventText=eventLabel!="—"?$"son olay {eventLabel}; ":"";
        Row("ChatGPT MCP görev metadata",taskState,taskValue,sessionLocked?"Windows oturumu kilitli; görev metadata gizlendi.":$"{eventText}{taskAge}; engelli {I(bt?.Blocked)}; süresi geçmiş claim {I(bt?.ExpiredClaims)}. Bu Local ChatGPT MCP Bridge görev kuyruğudur; gerçek Codex Desktop thread/turn durumu değildir.");
        Row("ChatGPT MCP Bridge",f.Agents.BridgeState.ToString(),"/health","Web ChatGPT yerel MCP bridge erişimi; gerçek Codex Desktop etkinliği değildir.");
        void MetricRow(string name,Metric m){
            int ttl=m.LastRead?7200:name.Contains("R2")?900:name.Contains("Kullanım")?7200:180;
            string age=m.CollectedAt.HasValue?$" · API/ölçüm alımı: {(now-m.CollectedAt.Value).TotalSeconds:0} sn önce":"";
            string value=m.Unit=="sites"&&m.Secondary.HasValue?$"{N(m.Value,format:"0")}/{N(m.Secondary,format:"0")} erişilebilir":N(m.Value," "+m.Unit,m.Unit.Length==3&&m.Unit.All(char.IsUpper)?"0.00":"0.0");
            if(m.LastRead&&!m.Value.HasValue)value="Kayıt yok";
            string note=MetricPresentation.Note(m,now,ttl);
            Row(name,Freshness.MetricState(m,now,ttl).ToString(),value,MetricPresentation.Detail(m)+age+(note.Length>0?" · "+note:""));
        }
        MetricRow("Tümü / Hosting",f.Hosting);MetricRow("Genel HTTPS / Hosting",engine.GeneralHosting);
        MetricRow("Tümü / Workers",f.Workers);MetricRow("Tümü / Son okunan ücret",f.Cost);
        foreach(var a in engine.Accounts){
            MetricRow(a.Profile.Name+" / Hosting",a.Hosting);MetricRow(a.Profile.Name+" / Workers",a.Workers);
            MetricRow(a.Profile.Name+" / D1",a.D1);MetricRow(a.Profile.Name+" / R2",a.R2);MetricRow(a.Profile.Name+" / Son okunan ücret",BillingDisplay.ForAccount(a,now));
        }
        foreach(var site in engine.Sites)Row("HTTPS / "+site.Host,site.Healthy?"Erişilebilir":"Kontrol başarısız",site.HttpCode?.ToString()??"—",$"{site.State}; {site.HeaderMs:0} ms; izlenen: {site.Url}");
        while(grid.Rows.Count>i)grid.Rows.RemoveAt(grid.Rows.Count-1);
        sites.Text=$"{engine.Sites.Count(s=>s.Healthy)}/{engine.Sites.Length} yapılandırılmış HTTPS adresi erişilebilir. Genel ve hesap adresleri ayrı kapsamdır. Ayrıntılar tabloda; ücretler son API kayıtlarıdır, fatura değildir.";

    }
    private async Task Exit()
    {if(exiting)return;exiting=true;timer.Stop();configure.Enabled=false;try{if(engine!=null)await engine.DisposeAsync();}finally{SystemEvents.SessionSwitch-=SessionChanged;SystemEvents.PowerModeChanged-=PowerChanged;tray.Visible=false;tray.Dispose();Close();}}
}
