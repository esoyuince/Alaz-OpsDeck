using System.Runtime.InteropServices;
namespace OpsDeck.Host;

internal static class OpsDeckTheme
{
    public static readonly Color Back=Color.FromArgb(10,14,24);
    public static readonly Color Surface=Color.FromArgb(17,23,36);
    public static readonly Color Surface2=Color.FromArgb(23,31,48);
    public static readonly Color Border=Color.FromArgb(43,56,78);
    public static readonly Color Text=Color.FromArgb(232,238,247);
    public static readonly Color Muted=Color.FromArgb(143,158,181);
    public static readonly Color Cyan=Color.FromArgb(59,198,255);
    public static readonly Color Green=Color.FromArgb(83,211,144);
    public static readonly Color Amber=Color.FromArgb(255,191,71);
    public static readonly Color Red=Color.FromArgb(255,108,122);
    public static readonly Color Purple=Color.FromArgb(165,132,255);

    private static readonly Font TabRegular=new("Segoe UI",9f,FontStyle.Regular);
    private static readonly Font TabBold=new("Segoe UI",9f,FontStyle.Bold);
    private static readonly Font StateBold=new("Segoe UI",9f,FontStyle.Bold);
    public static Font UiFont(float size=10f,FontStyle style=FontStyle.Regular)=>new("Segoe UI",size,style);

    public static void Apply(Form form)
    {
        form.BackColor=Back;form.ForeColor=Text;form.Font=UiFont();EnableDarkChrome(form);
        ApplyRecursive(form);
    }

    public static void ApplyRecursive(Control root)
    {
        foreach(Control c in root.Controls)
        {
            switch(c)
            {
                case DataGridView g: StyleGrid(g); break;
                case Button b: StyleButton(b); break;
                case TextBoxBase box: box.BackColor=Surface2;box.ForeColor=Text;box.BorderStyle=BorderStyle.FixedSingle; break;
                case CheckBox x: x.BackColor=Color.Transparent;x.ForeColor=Text; break;
                case Label l: l.ForeColor=l.ForeColor==SystemColors.ControlText?Text:l.ForeColor; break;
                case TabControl tabs: StyleTabs(tabs); break;
                case TabPage page: page.BackColor=Back;page.ForeColor=Text; break;
                case TableLayoutPanel table: table.BackColor=Back; break;
                case FlowLayoutPanel flow: flow.BackColor=Back; break;
                case Panel panel: panel.BackColor=panel.BackColor==SystemColors.Control?Back:panel.BackColor; break;
            }
            ApplyRecursive(c);
        }
    }

    private static void EnableDarkChrome(Form form)
    {
        void Apply(){try{int dark=1;DwmSetWindowAttribute(form.Handle,20,ref dark,sizeof(int));int corner=2;DwmSetWindowAttribute(form.Handle,33,ref corner,sizeof(int));}catch{}}
        if(form.IsHandleCreated)Apply();form.HandleCreated+=(_,_)=>Apply();
    }
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    [DllImport("uxtheme.dll",CharSet=CharSet.Unicode)]private static extern int SetWindowTheme(IntPtr hwnd,string? subApp,string? subId);
    private static void ApplyDarkTheme(Control c){void A(){try{SetWindowTheme(c.Handle,"DarkMode_Explorer",null);}catch{}}if(c.IsHandleCreated)A();c.HandleCreated+=(_,_)=>A();}
    public static void StyleGrid(DataGridView g)
    {
        g.BackgroundColor=Back;g.BorderStyle=BorderStyle.None;g.GridColor=Border;g.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;
        g.EnableHeadersVisualStyles=false;g.ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.None;g.ColumnHeadersHeight=38;g.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersDefaultCellStyle=new DataGridViewCellStyle{BackColor=Surface2,ForeColor=Muted,Font=UiFont(9f,FontStyle.Bold),SelectionBackColor=Surface2,SelectionForeColor=Muted,Padding=new Padding(8,0,8,0)};
        g.DefaultCellStyle=new DataGridViewCellStyle{BackColor=Surface,ForeColor=Text,SelectionBackColor=Color.FromArgb(28,61,82),SelectionForeColor=Text,Padding=new Padding(8,5,8,5),WrapMode=DataGridViewTriState.True};
        g.AlternatingRowsDefaultCellStyle=new DataGridViewCellStyle{BackColor=Color.FromArgb(14,20,32),ForeColor=Text,SelectionBackColor=Color.FromArgb(28,61,82),SelectionForeColor=Text,Padding=new Padding(8,5,8,5),WrapMode=DataGridViewTriState.True};
        g.RowTemplate.Height=34;ApplyDarkTheme(g);g.RowHeadersVisible=false;g.SelectionMode=DataGridViewSelectionMode.FullRowSelect;g.MultiSelect=false;
    }

    public static void StyleButton(Button b,bool accent=false,bool danger=false)
    {
        b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderSize=1;b.FlatAppearance.BorderColor=accent?Cyan:danger?Red:Border;
        b.BackColor=accent?Color.FromArgb(20,55,72):danger?Color.FromArgb(61,27,35):Surface2;b.ForeColor=danger?Color.FromArgb(255,186,194):Text;
        b.Font=UiFont(8.9f,FontStyle.Regular);b.Cursor=Cursors.Hand;b.Padding=new Padding(10,2,10,2);b.Height=Math.Max(36,b.Height);
        b.FlatAppearance.MouseOverBackColor=accent?Color.FromArgb(28,72,93):danger?Color.FromArgb(82,34,44):Color.FromArgb(31,42,62);
        b.FlatAppearance.MouseDownBackColor=accent?Color.FromArgb(18,48,64):danger?Color.FromArgb(54,23,30):Color.FromArgb(22,31,47);
    }

    public static Button NavButton(string text)
    {
        var b=new Button{Text=text,Width=204,Height=31,TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(0,0,0,2)};
        StyleButton(b);b.Height=31;b.Padding=new Padding(8,0,6,0);b.Font=UiFont(8.9f);return b;
    }

    public static Label Section(string text)=>new(){Text=text.ToUpperInvariant(),AutoSize=false,Width=204,Height=24,ForeColor=Muted,Font=UiFont(8.2f,FontStyle.Bold),TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(2,4,0,0),Margin=new Padding(0,4,0,2)};

    public static Label Badge(string text,Color color)=>new(){Text=text,AutoSize=true,ForeColor=color,BackColor=Surface2,Font=UiFont(9f,FontStyle.Bold),Padding=new Padding(10,5,10,5),Margin=new Padding(4)};
    public static void StyleTabs(TabControl tabs)
    {
        tabs.DrawMode=TabDrawMode.OwnerDrawFixed;tabs.SizeMode=TabSizeMode.Fixed;tabs.ItemSize=new Size(150,34);tabs.Padding=new Point(12,4);tabs.BackColor=Back;
        tabs.DrawItem-=DrawTab;tabs.DrawItem+=DrawTab;
        foreach(TabPage p in tabs.TabPages){p.BackColor=Back;p.ForeColor=Text;}
    }

    private static void DrawTab(object? sender,DrawItemEventArgs e)
    {
        if(sender is not TabControl tabs)return;bool selected=e.Index==tabs.SelectedIndex;Rectangle r=e.Bounds;
        using var bg=new SolidBrush(selected?Surface2:Back);e.Graphics.FillRectangle(bg,r);
        using var pen=new Pen(selected?Cyan:Border);e.Graphics.DrawLine(pen,r.Left,r.Bottom-1,r.Right,r.Bottom-1);
        TextRenderer.DrawText(e.Graphics,tabs.TabPages[e.Index].Text,selected?TabBold:TabRegular,r,selected?Text:Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
    }

    public static void TintStateCell(DataGridViewCell cell,string state)
    {
        string s=(state??"").ToUpperInvariant();Color c=s.Contains("ERROR")||s.Contains("DEGRADED")||s.Contains("DOWN")?Red:
            s.Contains("WARN")||s.Contains("ATTENTION")||s.Contains("PARTIAL")||s.Contains("RECOVER")?Amber:
            s.Contains("OK")||s.Contains("ACTIVE")||s.Contains("HEALTH")||s.Contains("MEASUREMENT")||s.Contains("ÖLÇÜM")?Green:Muted;
        cell.Style.ForeColor=c;cell.Style.Font=StateBold;
    }
}


