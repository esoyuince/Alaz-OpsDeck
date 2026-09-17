using System.Globalization;
using OpsDeck.Core;
namespace OpsDeck.Host;

public sealed class M5OverviewForm : Form
{
    private readonly AppEngine engine;
    private readonly Label header=new(){Dock=DockStyle.Top,Height=68,Padding=new Padding(10)};
    private readonly DataGridView summary=new(){Dock=DockStyle.Top,Height=310,ReadOnly=true,AllowUserToAddRows=false,
        AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private readonly DataGridView events=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,
        AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=15000};
    public M5OverviewForm(AppEngine engine)
    {
        this.engine=engine;Text="ALAZ OPSDECK — M5 Operational Intelligence Overview";ClientSize=new Size(1080,720);
        MinimumSize=new Size(820,560);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
        string[] s=["Signal","State","Value","Evidence / note"];for(int i=0;i<s.Length;i++)summary.Columns.Add("s"+i,s[i]);
        summary.Columns[0].Width=190;summary.Columns[1].Width=120;summary.Columns[2].Width=200;summary.Columns[3].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;
        string[] e=["Time","Severity","Domain","Code","Summary"];for(int i=0;i<e.Length;i++)events.Columns.Add("e"+i,e[i]);
        events.Columns[0].Width=150;events.Columns[1].Width=90;events.Columns[2].Width=110;events.Columns[3].Width=170;events.Columns[4].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;
        var refresh=new Button{Text="Refresh",Dock=DockStyle.Bottom,Height=38};refresh.Click+=(_,_)=>Reload();
        Controls.Add(events);Controls.Add(summary);Controls.Add(header);Controls.Add(refresh);
        OpsDeckTheme.Apply(this);OpsDeckTheme.StyleGrid(summary);OpsDeckTheme.StyleGrid(events);header.BackColor=OpsDeckTheme.Surface;header.ForeColor=OpsDeckTheme.Muted;header.Font=OpsDeckTheme.UiFont(9.5f);OpsDeckTheme.StyleButton(refresh,true);Shown+=(_,_)=>{Reload();timer.Start();};timer.Tick+=(_,_)=>Reload();FormClosed+=(_,_)=>timer.Dispose();
    }
    private void Reload()
    {
        var x=engine.ReadM5Overview();summary.Rows.Clear();events.Rows.Clear();
        void Row(string signal,string state,string value,string note)=>summary.Rows.Add(signal,state,value,note);
        Row("Operational state",AlertSemantics.Label(x.AlertLevel),$"{x.AssessmentScore}/100",$"{x.ActiveAlerts} active alert(s)");
        Row("Last 24h events",x.ErrorEvents24h>0?"ERRORS":x.WarningEvents24h>0?"WARNINGS":"INFO",
            $"{x.Events24h} total",$"warning {x.WarningEvents24h} · error {x.ErrorEvents24h}");
        Row("Project health",x.ProjectsDegraded>0?"DEGRADED":x.ProjectsAttention>0?"ATTENTION":x.ProjectsUnknown>0?"MIXED/UNKNOWN":"OK",
            $"OK {x.ProjectsOk} · ATT {x.ProjectsAttention}",$"DEG {x.ProjectsDegraded} · UNKNOWN {x.ProjectsUnknown}");
        Row("8h reliability","EVIDENCE",$"{x.ReliabilityObservedMinutes}/{x.ReliabilityExpectedMinutes} min",
            FormattableString.Invariant($"coverage {x.ReliabilityCoveragePercent:0.0}% · host restarts {x.HostRestarts8h} · panel reboots {x.PanelReboots8h} · recovery actions {x.RecoveryActions8h}"));
        if(x.TopDeviations.Length==0)Row("Statistical deviation","NEED DATA","—",BaselineDeviationReport.Disclaimer);
        else foreach(var d in x.TopDeviations)Row("Deviation / "+d.Label,"STATISTICAL",
            FormattableString.Invariant($"z {d.ZScore:0.00}"),FormattableString.Invariant($"recent {d.RecentAvg:0.##} {d.Unit} · baseline {d.BaselineAvg:0.##} {d.Unit}"));
        Row("Recent correlation",x.LatestCorrelation?.Severity.ToString()??"NONE",x.LatestCorrelation==null?"—":$"{x.LatestCorrelation.Domains.Length} domains",
            x.LatestCorrelation?.Summary??"No cross-domain event cluster in the last 24 hours.");
        foreach(var e in x.LatestEvents)events.Rows.Add(e.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),e.Severity,e.Domain,e.Code,e.Summary);
        header.Text=$"M5 overview @ {x.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{M5OverviewSummary.Disclaimer}";
    }
}

