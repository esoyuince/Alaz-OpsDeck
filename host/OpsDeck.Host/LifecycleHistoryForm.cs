using OpsDeck.Core;
namespace OpsDeck.Host;
public sealed class LifecycleHistoryForm : Form
{
    private readonly AppEngine engine;
    private readonly ComboBox window=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=90};
    private readonly Label status=new(){Dock=DockStyle.Top,Height=78,Padding=new Padding(10)};
    private readonly DataGridView grid=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=10000};
    public LifecycleHistoryForm(AppEngine engine)
    {
        this.engine=engine;Text="ALAZ OPSDECK — Lifecycle / Soak evidence";ClientSize=new Size(900,580);MinimumSize=new Size(720,480);Font=new Font("Segoe UI",10);
        foreach(var x in new[]{TelemetryWindow.M15,TelemetryWindow.H1,TelemetryWindow.H24,TelemetryWindow.D7})window.Items.Add(new WindowItem(x,TelemetryWindows.Label(x)));window.SelectedIndex=1;
        var top=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(8)};top.Controls.Add(new Label{Text="Window",AutoSize=true,Padding=new Padding(0,7,0,0)});top.Controls.Add(window);
        var refresh=new Button{Text="Refresh",AutoSize=true};top.Controls.Add(refresh);top.Controls.Add(new Label{Text="Evidence only — no automatic PASS/FAIL thresholds.",AutoSize=true,Padding=new Padding(14,7,0,0)});
        grid.Columns.Add("metric","Metric");grid.Columns.Add("min","Min");grid.Columns.Add("avg","Avg");grid.Columns.Add("max","Max");grid.Columns.Add("latest","Latest");grid.Columns.Add("points","Minutes");
        grid.Columns[0].Width=230;for(int i=1;i<=4;i++)grid.Columns[i].Width=110;grid.Columns[5].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;
        Controls.Add(grid);Controls.Add(status);Controls.Add(top);
        window.SelectedIndexChanged+=(_,_)=>Reload();refresh.Click+=(_,_)=>Reload();timer.Tick+=(_,_)=>Reload();Shown+=(_,_)=>{Reload();timer.Start();};FormClosed+=(_,_)=>timer.Dispose();
    }
    private void Reload()
    {
        var selected=window.SelectedItem is WindowItem wi?wi.Value:TelemetryWindow.H1;var t=engine.ReadLifecycleTrend(selected);grid.Rows.Clear();
        string F(double? v,string unit="")=>v.HasValue?$"{v.Value:0.##}{unit}":"—";
        void Row(string name,RangeStat r,string unit)=>grid.Rows.Add(name,F(r.Min,unit),F(r.Avg,unit),F(r.Max,unit),F(r.Latest,unit),r.Points);
        Row("Host private memory",t.HostPrivateMiB," MiB");Row("Host working set",t.HostWorkingSetMiB," MiB");Row("Host handles",t.Handles,"");Row("Host threads",t.Threads,"");
        Row("Panel internal free",t.PanelInternalKiB," KiB");Row("Panel lifetime min free",t.PanelMinKiB," KiB");Row("Panel PSRAM free",t.PanelPsramKiB," KiB");
        string sd=t.PanelSdState switch{0=>"SETUP",1=>"READY",2=>"UNAVAILABLE",3=>"ERROR",_=>"--"};
        string uptime=t.PanelLatestUptimeS>=0?AgentTaskPresentation.Age(DateTimeOffset.UtcNow.AddSeconds(-t.PanelLatestUptimeS),DateTimeOffset.UtcNow):"--";
        status.Text=$"{t.MinuteRows} minute rows · host restarts {t.HostRestarts} · panel reboots {t.PanelReboots} · panel packets +{t.PanelPacketsAdvanced}\n"+
            $"Link {t.LatestLinkState} · reopen +{t.LinkReopens} · ROM +{t.LinkRomProbes} · recovered +{t.LinkRecoveries} · panel uptime {uptime} · SD {sd}";
    }
    private sealed record WindowItem(TelemetryWindow Value,string Label){public override string ToString()=>Label;}
}
