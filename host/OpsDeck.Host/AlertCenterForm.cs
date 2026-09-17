using OpsDeck.Core;
namespace OpsDeck.Host;
public sealed class AlertCenterForm : Form
{
    private readonly AppEngine engine;
    private readonly Label summary=new(){Dock=DockStyle.Top,Height=68,Padding=new Padding(10)};
    private readonly DataGridView grid=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=10000};
    public AlertCenterForm(AppEngine engine)
    {
        this.engine=engine;Text="ALAZ OPSDECK — Alert Center";ClientSize=new Size(920,560);MinimumSize=new Size(720,460);Font=new Font("Segoe UI",10);
        grid.Columns.Add("level","Level");grid.Columns.Add("source","Source");grid.Columns.Add("code","Code");grid.Columns.Add("detail","Detail");
        grid.Columns[0].Width=120;grid.Columns[1].Width=150;grid.Columns[2].Width=190;grid.Columns[3].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        Controls.Add(grid);Controls.Add(summary);Shown+=(_,_)=>{Reload();timer.Start();};timer.Tick+=(_,_)=>Reload();FormClosed+=(_,_)=>timer.Dispose();
    }
    private void Reload()
    {
        var alerts=engine.ReadAlertRollup();var assessment=engine.ReadOperationalAssessment();grid.Rows.Clear();
        foreach(var a in alerts.Items)grid.Rows.Add(AlertSemantics.Label(a.Level),a.Source,a.Code,a.Text);
        if(alerts.Items.Length==0)grid.Rows.Add("INFO","SYSTEM","NONE","No active alerts");
        summary.Text=$"{AlertSemantics.Label(alerts.Level)} · operational score {assessment.Score}/100 · {alerts.Items.Length} active alert(s)\n"+
            $"Read-only policy. Disk: ATTENTION >= {AlertSemantics.DiskAttentionPercent:0}% / DEGRADED >= {AlertSemantics.DiskDegradedPercent:0}%. No temperature thresholds configured.";
    }
}
