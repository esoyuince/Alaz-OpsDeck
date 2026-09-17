using System.Globalization;
using OpsDeck.Core;
namespace OpsDeck.Host;

public sealed class ReliabilitySoakForm : Form
{
    private readonly AppEngine engine;
    private readonly ComboBox window=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=90};
    private readonly Label status=new(){Dock=DockStyle.Top,Height=92,Padding=new Padding(10)};
    private readonly DataGridView grid=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,
        AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=15000};
    public ReliabilitySoakForm(AppEngine engine)
    {
        this.engine=engine;Text="ALAZ OPSDECK — Reliability / Soak Analytics";ClientSize=new Size(1040,650);MinimumSize=new Size(800,520);Font=new Font("Segoe UI",10);
        foreach(var x in Enum.GetValues<ReliabilityWindow>())window.Items.Add(new WindowItem(x,ReliabilityWindows.Label(x)));window.SelectedIndex=1;
        var top=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(8)};top.Controls.Add(new Label{Text="Window",AutoSize=true,Padding=new Padding(0,7,0,0)});top.Controls.Add(window);
        var refresh=new Button{Text="Refresh",AutoSize=true};top.Controls.Add(refresh);top.Controls.Add(new Label{Text="Evidence only — no leak, durability, or PASS/FAIL threshold is inferred.",AutoSize=true,Padding=new Padding(14,7,0,0)});
        string[] names=["Metric","First","Latest","Delta","Min","Max","Minutes"];
        for(int i=0;i<names.Length;i++)grid.Columns.Add("c"+i,names[i]);grid.Columns[0].Width=220;for(int i=1;i<=5;i++)grid.Columns[i].Width=115;grid.Columns[6].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;
        Controls.Add(grid);Controls.Add(status);Controls.Add(top);window.SelectedIndexChanged+=(_,_)=>Reload();refresh.Click+=(_,_)=>Reload();timer.Tick+=(_,_)=>Reload();Shown+=(_,_)=>{Reload();timer.Start();};FormClosed+=(_,_)=>timer.Dispose();
    }
    private void Reload()
    {
        var selected=window.SelectedItem is WindowItem wi?wi.Value:ReliabilityWindow.H8;var r=engine.ReadReliabilityReport(selected);grid.Rows.Clear();
        string F(double? v,string unit="")=>v.HasValue?v.Value.ToString("0.##",CultureInfo.InvariantCulture)+unit:"—";
        void Row(ReliabilityMetric m)=>grid.Rows.Add(m.Label,F(m.First,m.Unit),F(m.Latest,m.Unit),F(m.Delta,m.Unit),F(m.Min,m.Unit),F(m.Max,m.Unit),m.Points);
        foreach(var m in new[]{r.HostPrivateMiB,r.HostWorkingSetMiB,r.Handles,r.Threads,r.PanelInternalKiB,r.PanelMinKiB,r.PanelPsramKiB})Row(m);
        string first=r.FirstObservedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")??"--",last=r.LastObservedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")??"--";
        string sd=r.PanelSdState switch{0=>"SETUP",1=>"READY",2=>"UNAVAILABLE",3=>"ERROR",_=>"--"};
        status.Text=$"Observed {r.ObservedMinutes}/{r.ExpectedMinutes} min ({r.CoveragePercent.ToString("0.0",CultureInfo.InvariantCulture)}%) · first {first} · last {last} · max unobserved gap {r.MaxGapMinutes} min\n"+
            $"Host restarts {r.HostRestarts} · panel reboots {r.PanelReboots} · packets +{r.PanelPacketsAdvanced} · reopen +{r.LinkReopens} · ROM +{r.LinkRomProbes} · recovered +{r.LinkRecoveries}\n"+
            $"Recovery actions / window hour {r.RecoveryActionsPerWindowHour.ToString("0.00",CultureInfo.InvariantCulture)} · recoveries / window hour {r.RecoveriesPerWindowHour.ToString("0.00",CultureInfo.InvariantCulture)} · link {r.LatestLinkState} · SD {sd}";
    }
    private sealed record WindowItem(ReliabilityWindow Value,string Label){public override string ToString()=>Label;}
}
