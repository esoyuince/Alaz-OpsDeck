using OpsDeck.Core;
namespace OpsDeck.Host;
public sealed class BaselineDeviationForm : Form
{
    private readonly AppEngine engine;
    private readonly Label status=new(){Dock=DockStyle.Top,Height=62,Padding=new Padding(10)};
    private readonly DataGridView grid=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=30000};
    public BaselineDeviationForm(AppEngine engine)
    {
        this.engine=engine;Text="ALAZ OPSDECK — M5.8 Statistical Baseline / Deviation";ClientSize=new Size(1040,650);MinimumSize=new Size(820,520);Font=new Font("Segoe UI",10);
        grid.Columns.Add("metric","Metric");grid.Columns.Add("recent","Recent 15m avg");grid.Columns.Add("baseline","Prior 24h avg");grid.Columns.Add("sigma","Baseline σ");grid.Columns.Add("z","Z-score");grid.Columns.Add("delta","Δ %");grid.Columns.Add("points","Recent / baseline points");
        grid.Columns[0].Width=180;for(int i=1;i<=5;i++)grid.Columns[i].Width=125;grid.Columns[6].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;
        var refresh=new Button{Text="Refresh",Dock=DockStyle.Bottom,Height=38};refresh.Click+=(_,_)=>Reload();
        Controls.Add(grid);Controls.Add(status);Controls.Add(refresh);Shown+=(_,_)=>{Reload();timer.Start();};timer.Tick+=(_,_)=>Reload();FormClosed+=(_,_)=>timer.Dispose();
    }
    private void Reload()
    {
        var report=engine.ReadBaselineDeviation();status.Text=$"Recent: {report.RecentFrom.LocalDateTime:g} → {report.RecentTo.LocalDateTime:t} | Baseline: {report.BaselineFrom.LocalDateTime:g} → {report.BaselineTo.LocalDateTime:g}\n{BaselineDeviationReport.Disclaimer}";
        grid.Rows.Clear();string F(double? v,string fmt="0.##")=>v.HasValue?v.Value.ToString(fmt,System.Globalization.CultureInfo.InvariantCulture):"—";
        foreach(var m in report.Metrics){string state=m.Sufficient?"OK":"NEED DATA";grid.Rows.Add(m.Label+" ["+state+"]",F(m.RecentAvg),F(m.BaselineAvg),F(m.BaselineStdDev),F(m.ZScore),F(m.DeltaPercent),$"{m.RecentPoints} / {m.BaselinePoints} {m.Unit}");}
    }
}
