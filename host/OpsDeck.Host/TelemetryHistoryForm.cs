using System.ComponentModel;
using OpsDeck.Core;
namespace OpsDeck.Host;
public sealed class TelemetryHistoryForm : Form
{
    private readonly AppEngine engine;
    private readonly ComboBox window=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=90};
    private readonly ComboBox metric=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=180};
    private readonly Label status=new(){AutoSize=true,Padding=new Padding(8,7,0,0)};
    private readonly DataGridView grid=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private readonly DataGridView eventsGrid=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private readonly TrendPanel chart=new(){Dock=DockStyle.Fill,MinimumSize=new Size(400,220)};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=10000};
    private TelemetryWindow SelectedWindow=TelemetryWindow.H1;
    public TelemetryHistoryForm(AppEngine engine)
    {
        this.engine=engine;Text="ALAZ OPSDECK — M5.6 Trend + Event Context";ClientSize=new Size(1040,820);MinimumSize=new Size(820,640);Font=new Font("Segoe UI",10);
        foreach(var x in new[]{TelemetryWindow.M15,TelemetryWindow.H1,TelemetryWindow.H24,TelemetryWindow.D7})window.Items.Add(new WindowItem(x,TelemetryWindows.Label(x)));
        window.SelectedIndex=1;
        foreach(var x in TrendPanel.Metrics)metric.Items.Add(x);metric.SelectedIndex=3;
        var top=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(8)};top.Controls.Add(new Label{Text="Window",AutoSize=true,Padding=new Padding(0,7,0,0)});top.Controls.Add(window);
        top.Controls.Add(new Label{Text="Chart",AutoSize=true,Padding=new Padding(12,7,0,0)});top.Controls.Add(metric);var refresh=new Button{Text="Refresh",AutoSize=true};top.Controls.Add(refresh);top.Controls.Add(status);
        grid.Columns.Add("metric","Metric");grid.Columns.Add("min","Min");grid.Columns.Add("avg","Avg");grid.Columns.Add("max","Max");grid.Columns.Add("unit","Unit");grid.Columns.Add("points","Minutes");
        grid.Columns[0].Width=210;grid.Columns[1].Width=110;grid.Columns[2].Width=110;grid.Columns[3].Width=110;grid.Columns[4].Width=90;grid.Columns[5].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;
        eventsGrid.Columns.Add("time","Time");eventsGrid.Columns.Add("domain","Domain");eventsGrid.Columns.Add("severity","Severity");eventsGrid.Columns.Add("code","Code");eventsGrid.Columns.Add("summary","Summary");
        eventsGrid.Columns[0].Width=130;eventsGrid.Columns[1].Width=95;eventsGrid.Columns[2].Width=95;eventsGrid.Columns[3].Width=180;eventsGrid.Columns[4].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;eventsGrid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        var lower=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=210};lower.Panel1.Controls.Add(grid);lower.Panel2.Controls.Add(eventsGrid);
        var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=310};split.Panel1.Controls.Add(chart);split.Panel2.Controls.Add(lower);
        Controls.Add(split);Controls.Add(top);window.SelectedIndexChanged+=(_,_)=>Reload();metric.SelectedIndexChanged+=(_,_)=>ReloadChart();refresh.Click+=(_,_)=>Reload();timer.Tick+=(_,_)=>Reload();Shown+=(_,_)=>{Reload();timer.Start();};FormClosed+=(_,_)=>timer.Dispose();
    }
    private void Reload()
    {
        if(window.SelectedItem is WindowItem wi)SelectedWindow=wi.Value;
        var trend=engine.ReadTelemetryTrend(SelectedWindow);grid.Rows.Clear();
        string F(double? v)=>v.HasValue?v.Value.ToString("0.##"):"—";
        foreach(var m in trend.Metrics)grid.Rows.Add(m.Label,F(m.Min),F(m.Avg),F(m.Max),m.Unit,m.Points);
        foreach(var v in trend.Volumes)grid.Rows.Add("Volume "+v.Id+" used",F(v.MinUsed),F(v.AvgUsed),F(v.MaxUsed),"%",v.Points);
        var context=engine.ReadTelemetryEventContext(trend.From,trend.To,200);eventsGrid.Rows.Clear();
        foreach(var e in context.Events){string stamp=SelectedWindow==TelemetryWindow.D7?e.At.LocalDateTime.ToString("MM-dd HH:mm:ss"):e.At.LocalDateTime.ToString("HH:mm:ss");eventsGrid.Rows.Add(stamp,e.Domain,e.Severity,e.Code,e.Summary);}
        status.Text=$"{trend.MinuteRows} min · Link {trend.Link.LatestState} · recover +{trend.Link.Recoveries} · events {context.Events.Length} / domains {context.DomainCount} · W {context.WarningCount} / E {context.ErrorCount} · {TelemetryEventContext.Disclaimer}";
        chart.Points=engine.ReadTelemetrySeries(SelectedWindow,180);chart.EventMarkers=context.Events.Take(80).ToArray();chart.RangeFrom=trend.From;chart.RangeTo=trend.To;ReloadChart();
    }
    private void ReloadChart(){chart.Selected=metric.SelectedItem as TrendMetricItem??TrendPanel.Metrics[0];chart.Invalidate();}
    private sealed record WindowItem(TelemetryWindow Value,string Label){public override string ToString()=>Label;}
}

internal sealed record TrendMetricItem(string Label,string Unit,Func<TelemetryPoint,double?> Read){public override string ToString()=>Label;}
internal sealed class TrendPanel : Panel
{
    public static readonly TrendMetricItem[] Metrics=[
        new("CPU","%",p=>p.Cpu),new("Intel GPU","%",p=>p.IntelGpu),new("NVIDIA GPU","%",p=>p.Gpu),
        new("CPU temp","°C",p=>p.CpuTemp),new("Chassis temp","°C",p=>p.ChassisTemp),new("NVIDIA temp","°C",p=>p.GpuTemp),
        new("Fan 1","RPM",p=>p.Fan1),new("Fan 2","RPM",p=>p.Fan2),new("RAM used","GiB",p=>p.RamUsed),new("VRAM used","GiB",p=>p.VramUsed),
        new("RX","Mb/s",p=>p.Rx),new("TX","Mb/s",p=>p.Tx)];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public TelemetryPoint[] Points{get;set;}=[];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public OperationalEvent[] EventMarkers{get;set;}=[];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public DateTimeOffset RangeFrom{get;set;}
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public DateTimeOffset RangeTo{get;set;}
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public TrendMetricItem Selected{get;set;}=Metrics[0];
    public TrendPanel(){DoubleBuffered=true;BackColor=SystemColors.Window;}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var g=e.Graphics;var rect=ClientRectangle;rect.Inflate(-48,-28);if(rect.Width<80||rect.Height<80)return;
        var values=Points.Select(p=>(Point:p,Value:Selected.Read(p))).Where(x=>x.Value.HasValue&&double.IsFinite(x.Value.Value)).ToArray();
        using var title=new Font(Font,FontStyle.Bold);g.DrawString($"{Selected.Label} trend + event context",title,SystemBrushes.WindowText,12,8);
        DateTimeOffset from=RangeFrom==default?(Points.Length>0?Points[0].At:DateTimeOffset.UtcNow.AddHours(-1)):RangeFrom;
        DateTimeOffset to=RangeTo==default?(Points.Length>0?Points[^1].At:DateTimeOffset.UtcNow):RangeTo;double span=Math.Max(1,(to-from).TotalSeconds);
        g.DrawRectangle(SystemPens.ControlDark,rect);
        foreach(var ev in EventMarkers.Where(x=>x.At>=from&&x.At<=to).Take(80)){float x=rect.Left+(float)((ev.At-from).TotalSeconds/span*rect.Width);using var marker=new Pen(SystemColors.ControlDarkDark,ev.Severity==OperationalSeverity.Error?2:1){DashStyle=ev.Severity==OperationalSeverity.Info?System.Drawing.Drawing2D.DashStyle.Dot:ev.Severity==OperationalSeverity.Warning?System.Drawing.Drawing2D.DashStyle.Dash:System.Drawing.Drawing2D.DashStyle.Solid};g.DrawLine(marker,x,rect.Top,x,rect.Bottom);}
        if(values.Length<2){g.DrawString("No telemetry history yet; event markers still use the selected time window.",Font,SystemBrushes.GrayText,12,42);return;}
        double min=values.Min(x=>x.Value!.Value),max=values.Max(x=>x.Value!.Value);if(Math.Abs(max-min)<1e-9){min=Math.Max(0,min-1);max+=1;}
        g.DrawString($"{max:0.##} {Selected.Unit}",Font,SystemBrushes.GrayText,4,rect.Top-8);g.DrawString($"{min:0.##} {Selected.Unit}",Font,SystemBrushes.GrayText,4,rect.Bottom-10);PointF? prev=null;
        using var pen=new Pen(SystemColors.Highlight,2);
        foreach(var item in values){if(item.Point.At<from||item.Point.At>to)continue;float x=rect.Left+(float)((item.Point.At-from).TotalSeconds/span*rect.Width);float y=rect.Bottom-(float)((item.Value!.Value-min)/(max-min)*rect.Height);var p=new PointF(x,y);if(prev.HasValue)g.DrawLine(pen,prev.Value,p);prev=p;}
        string start=from.LocalDateTime.ToString(SelectedWindowLabel(from,to));string end=to.LocalDateTime.ToString(SelectedWindowLabel(from,to));g.DrawString(start,Font,SystemBrushes.GrayText,rect.Left,rect.Bottom+5);var size=g.MeasureString(end,Font);g.DrawString(end,Font,SystemBrushes.GrayText,rect.Right-size.Width,rect.Bottom+5);
    }
    private static string SelectedWindowLabel(DateTimeOffset from,DateTimeOffset to)=>(to-from)>TimeSpan.FromHours(24)?"MM-dd HH:mm":"HH:mm";
}
