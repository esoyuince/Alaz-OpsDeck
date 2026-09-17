using OpsDeck.Core;
namespace OpsDeck.Host;

public sealed class OperationalTimelineForm : Form
{
    private readonly AppEngine engine;
    private readonly DataGridView grid=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,
        AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private readonly Label summary=new(){Dock=DockStyle.Top,Height=42,Padding=new Padding(10)};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=2000};
    public OperationalTimelineForm(AppEngine engine)
    {
        this.engine=engine;Text="ALAZ OPSDECK — Operational Event Timeline";ClientSize=new Size(980,600);MinimumSize=new Size(760,460);Font=new Font("Segoe UI",10);
        grid.Columns.Add("time","Zaman");grid.Columns.Add("severity","Seviye");grid.Columns.Add("domain","Alan");grid.Columns.Add("code","Olay");grid.Columns.Add("summary","Özet");
        grid.Columns[0].Width=150;grid.Columns[1].Width=90;grid.Columns[2].Width=90;grid.Columns[3].Width=190;
        grid.Columns[4].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,Padding=new Padding(8)};
        var refresh=new Button{Text="Yenile",AutoSize=true};refresh.Click+=(_,_)=>RefreshRows();footer.Controls.Add(refresh);
        footer.Controls.Add(new Label{Text="Read-only · 14 gün saklama · ham log/prompt/token yok",AutoSize=true,Padding=new Padding(10,7,0,0)});
        Controls.Add(grid);Controls.Add(summary);Controls.Add(footer);
        timer.Tick+=(_,_)=>RefreshRows();Shown+=(_,_)=>{RefreshRows();timer.Start();};FormClosed+=(_,_)=>timer.Stop();
    }

    private void RefreshRows()
    {
        var rows=engine.ReadOperationalEvents(250);var health=engine.ReadOperationalAssessment();var correlations=engine.ReadOperationalCorrelations();
        summary.Text=$"{health.State.ToString().ToUpperInvariant()} · {health.Score}/100 · {health.PrimaryReason} · {rows.Length} olay · {correlations.Length} cross-domain correlation";
        int i=0;foreach(var e in rows){while(grid.Rows.Count<=i)grid.Rows.Add();
            grid.Rows[i++].SetValues(e.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),e.Severity.ToString().ToUpperInvariant(),
                e.Domain.ToString().ToUpperInvariant(),e.Code,e.Summary);}
        while(grid.Rows.Count>i)grid.Rows.RemoveAt(grid.Rows.Count-1);
    }
}
