using OpsDeck.Core;
namespace OpsDeck.Host;
public sealed class OpsBriefForm : Form
{
    private readonly AppEngine engine;
    private readonly ComboBox window=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=90};
    private readonly Label status=new(){AutoSize=true,Padding=new Padding(8,7,0,0)};
    private readonly ListBox highlights=new(){Dock=DockStyle.Fill,HorizontalScrollbar=true};
    private readonly DataGridView stats=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false};
    private readonly DataGridView events=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=30000};
    public OpsBriefForm(AppEngine engine)
    {
        this.engine=engine;Text="ALAZ OPSDECK — M5.7 Daily / Weekly Ops Brief";ClientSize=new Size(1040,760);MinimumSize=new Size(820,620);Font=new Font("Segoe UI",10);
        window.Items.Add(new WindowItem(TelemetryWindow.H24,"24h"));window.Items.Add(new WindowItem(TelemetryWindow.D7,"7d"));window.SelectedIndex=0;
        var top=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(8)};top.Controls.Add(new Label{Text="Window",AutoSize=true,Padding=new Padding(0,7,0,0)});top.Controls.Add(window);var refresh=new Button{Text="Refresh",AutoSize=true};top.Controls.Add(refresh);top.Controls.Add(status);
        stats.Columns.Add("item","Evidence");stats.Columns.Add("value","Value");stats.Columns[0].Width=280;stats.Columns[1].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;
        events.Columns.Add("time","Time");events.Columns.Add("domain","Domain");events.Columns.Add("severity","Severity");events.Columns.Add("code","Code");events.Columns.Add("summary","Summary");
        events.Columns[0].Width=135;events.Columns[1].Width=90;events.Columns[2].Width=90;events.Columns[3].Width=170;events.Columns[4].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;events.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        var upper=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=180};upper.Panel1.Controls.Add(highlights);upper.Panel2.Controls.Add(stats);
        var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=470};split.Panel1.Controls.Add(upper);split.Panel2.Controls.Add(events);
        Controls.Add(split);Controls.Add(top);window.SelectedIndexChanged+=(_,_)=>Reload();refresh.Click+=(_,_)=>Reload();timer.Tick+=(_,_)=>Reload();Shown+=(_,_)=>{Reload();timer.Start();};FormClosed+=(_,_)=>timer.Dispose();
    }
    private void Reload()
    {
        var selected=(window.SelectedItem as WindowItem)?.Value??TelemetryWindow.H24;var brief=engine.ReadOpsBrief(selected);
        status.Text=$"{TelemetryWindows.Label(selected)} · current {AlertSemantics.Label(brief.CurrentAlerts.Level)} · events {brief.Events.Total} · {OpsBrief.Disclaimer}";
        highlights.Items.Clear();foreach(string line in brief.Highlights)highlights.Items.Add(line);if(brief.Highlights.Length==0)highlights.Items.Add("No summary highlights in this window.");
        stats.Rows.Clear();void R(string name,object value)=>stats.Rows.Add(name,value);
        R("Events total / info / warning / error",$"{brief.Events.Total} / {brief.Events.Info} / {brief.Events.Warning} / {brief.Events.Error}");
        R("Event domains",brief.Events.Domains);R("Link recovery / recovered",$"{brief.Events.LinkRecovery} / {brief.Events.LinkRecovered}");
        R("Panel reboot / host start / stop",$"{brief.Events.PanelReboot} / {brief.Events.HostStarted} / {brief.Events.HostStopped}");
        R("Power suspend / resume",$"{brief.Events.PowerSuspend} / {brief.Events.PowerResume}");R("HTTPS degraded / restored",$"{brief.Events.HttpsDegraded} / {brief.Events.HttpsRestored}");
        R("Codex approval / blocked",$"{brief.Events.TaskApproval} / {brief.Events.TaskBlocked}");R("Cloud state changes",brief.Events.CloudStateChanges);
        R("Lifecycle minute rows",brief.Lifecycle.MinuteRows);R("Host restarts / panel reboots",$"{brief.Lifecycle.HostRestarts} / {brief.Lifecycle.PanelReboots}");
        R("Panel packet advance",brief.Lifecycle.PanelPacketsAdvanced);R("Link reopen / ROM / recovered",$"{brief.Lifecycle.LinkReopens} / {brief.Lifecycle.LinkRomProbes} / {brief.Lifecycle.LinkRecoveries}");
        R("Telemetry minute rows",brief.Telemetry.MinuteRows);
        events.Rows.Clear();foreach(var e in brief.LatestEvents){string stamp=selected==TelemetryWindow.D7?e.At.LocalDateTime.ToString("MM-dd HH:mm:ss"):e.At.LocalDateTime.ToString("HH:mm:ss");events.Rows.Add(stamp,e.Domain,e.Severity,e.Code,e.Summary);}
    }
    private sealed record WindowItem(TelemetryWindow Value,string Label){public override string ToString()=>Label;}
}
