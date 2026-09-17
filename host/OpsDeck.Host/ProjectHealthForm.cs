using OpsDeck.Core;
namespace OpsDeck.Host;

public sealed class ProjectHealthForm : Form
{
    private readonly AppEngine engine;
    private readonly Label status=new(){Dock=DockStyle.Top,Height=48,Padding=new Padding(8)};
    private readonly DataGridView grid=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,
        AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=15000};
    public ProjectHealthForm(AppEngine engine)
    {
        this.engine=engine;Text="ALAZ OPSDECK — Project Health v2";ClientSize=new Size(1040,620);
        MinimumSize=new Size(780,480);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
        string[] names=["Hesap","Grup / proje","Tür","Health","Kaynak","Evidence","OK","ATT","DEG","UNKNOWN"];
        for(int i=0;i<names.Length;i++){grid.Columns.Add("c"+i,names[i]);grid.Columns[i].SortMode=DataGridViewColumnSortMode.NotSortable;}
        grid.Columns[0].Width=150;grid.Columns[1].Width=220;grid.Columns[2].Width=90;grid.Columns[3].Width=105;
        grid.Columns[4].Width=75;grid.Columns[5].Width=80;grid.Columns[6].Width=55;grid.Columns[7].Width=55;grid.Columns[8].Width=55;
        grid.Columns[9].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;
        Controls.Add(grid);Controls.Add(status);Shown+=(_,_)=>{Reload();timer.Start();};timer.Tick+=(_,_)=>Reload();FormClosed+=(_,_)=>timer.Dispose();
    }
    private void Reload()
    {
        var rows=engine.ReadProjectHealth(DateTimeOffset.UtcNow);grid.Rows.Clear();
        foreach(var x in rows)grid.Rows.Add(x.AccountName,x.Project,x.Assigned?"Project":"Unassigned",x.Label,
            x.Resources,x.EvidenceCount,x.OkCount,x.AttentionCount,x.DegradedCount,x.UnknownCount);
        int ok=rows.Count(x=>x.Health==ProjectHealthState.Ok),att=rows.Count(x=>x.Health==ProjectHealthState.Attention);
        int deg=rows.Count(x=>x.Health==ProjectHealthState.Degraded),unknown=rows.Count(x=>x.Health==ProjectHealthState.Unknown);
        status.Text=rows.Length==0?
            "No project-health rows yet. Project map or inventory may be unavailable.":
            $"Groups {rows.Length} · OK {ok} · ATTENTION {att} · DEGRADED {deg} · UNKNOWN {unknown}. "+
            "Only resource-specific Worker/D1/R2 cache evidence is attributed; Pages and account/shared signals are excluded.";
    }
}
