namespace OpsDeck.Host;
public sealed class HistoryChart : Control
{
    private DateTimeOffset[] times=[];private double?[] values=[];private string unit="";
    public HistoryChart(){DoubleBuffered=true;BackColor=SystemColors.Window;ForeColor=SystemColors.WindowText;Dock=DockStyle.Fill;AccessibleName="Saatlik ölçüm grafiği";}
    public void SetSeries(DateTimeOffset[] hours,double?[] data,string label)
    {
        if(hours.Length!=data.Length||hours.Length>24||data.Any(v=>v.HasValue&&(!double.IsFinite(v.Value)||v<0)))throw new ArgumentException("Invalid chart data");
        times=hours.ToArray();values=data.ToArray();unit=label;AccessibleDescription=$"{label}; {data.Count(v=>v.HasValue)}/{data.Length} saat veri var. Tam değerler Saatlik veriler sekmesinde.";Invalidate();
    }
    protected override void OnResize(EventArgs e){base.OnResize(e);Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var g=e.Graphics;using var ink=new SolidBrush(ForeColor);using var pen=new Pen(ForeColor,2);
        var box=new Rectangle(85,45,Math.Max(1,ClientSize.Width-115),Math.Max(1,ClientSize.Height-105));
        if(ClientSize.Width<220||ClientSize.Height<160)return;
        g.DrawString(unit,Font,ink,new RectangleF(12,8,ClientSize.Width-24,32));
        g.DrawLine(SystemPens.GrayText,box.Left,box.Top,box.Left,box.Bottom);g.DrawLine(SystemPens.GrayText,box.Left,box.Bottom,box.Right,box.Bottom);
        double max=Math.Max(1,values.Where(v=>v.HasValue).Select(v=>v!.Value).DefaultIfEmpty(0).Max());
        for(int i=0;i<=2;i++){float y=box.Bottom-box.Height*i/2f;g.DrawString((max*i/2).ToString("0.##"),Font,ink,new RectangleF(0,y-10,80,30));}
        if(times.Length>0){g.DrawString(times[0].ToString("MM-dd HH:mm"),Font,ink,box.Left,box.Bottom+5);g.DrawString(times[^1].ToString("MM-dd HH:mm"),Font,ink,box.Right-110,box.Bottom+5);}
        PointF? previous=null;DateTimeOffset? previousHour=null;
        for(int i=0;i<values.Length;i++)
        {
            if(!values[i].HasValue){previous=null;previousHour=null;continue;}
            var point=new PointF(box.Left+box.Width*i/(float)Math.Max(1,values.Length-1),box.Bottom-(float)(values[i]!.Value/max)*box.Height);
            if(previous.HasValue&&times[i]-previousHour==TimeSpan.FromHours(1))g.DrawLine(pen,previous.Value,point);
            g.FillEllipse(ink,point.X-3,point.Y-3,6,6);previous=point;previousHour=times[i];
        }
        if(values.All(v=>!v.HasValue))g.DrawString("Bu pencerede çizilecek ölçüm yok.",Font,ink,box.Left+20,box.Top+35);
        g.DrawString("Saatler UTC · Boşluk: veri yok · Sıfır tabanlı eksen · Örneklenmiş analitik",Font,ink,box.Left,ClientSize.Height-28);
    }
}
