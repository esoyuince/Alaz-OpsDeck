using OpsDeck.Core;
namespace OpsDeck.Host;

public sealed partial class ResourcesForm : Form
{
    private readonly AppEngine engine;private readonly ProjectMap store;private ProjectMapSnapshot original;
    private readonly Dictionary<ResourceKey,string> assignments;private readonly CancellationTokenSource stop=new();
    private ResourceInventory inventory=ResourceInventory.Empty;private bool loading,closing,dirty;
    private readonly TextBox search=new(){Width=250,PlaceholderText="Kaynak / proje ara"};
    private readonly Button refresh=new(){Text="Kaynakları keşfet / yenile",AutoSize=true},save=new(){Text="Proje eşlemelerini kaydet",AutoSize=true};
    private readonly TextBox summary=new(){Dock=DockStyle.Top,Height=150,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
    private readonly DataGridView grid=new(){Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private readonly Label notice=new(){Dock=DockStyle.Bottom,AutoSize=true,Padding=new Padding(8),Text="Kaynak sağlığı yalnız resource-specific Worker/D1/R2 evidence'ıdır. Pages ve account/shared scope UNKNOWN kalır. Proje eşlemesi yalnız bu PC'deki gruplamadır."};
    public ResourcesForm(AppEngine engine,LocalSettings settings)
    {
        this.engine=engine;store=new(settings.DirectoryPath);original=store.Load();assignments=original.Assignments.ToDictionary(x=>x.Key,x=>x.Project);
        Text="ALAZ OPSDECK — Kaynaklar / Projeler";ClientSize=new Size(1100,650);MinimumSize=new Size(850,540);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
        var commands=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(8)};commands.Controls.Add(refresh);commands.Controls.Add(search);commands.Controls.Add(save);
        var detail=new Button{Text="Seçili kaynak ölçümleri",AutoSize=true};commands.Controls.Add(detail);
        detail.Click+=(_,_)=>{
            if(grid.CurrentRow?.Tag is not ResourceKey key){MessageBox.Show(this,"Önce listeden bir kaynak seçin.","ALAZ OPSDECK");return;}
            if(key.Kind==ResourceKind.Worker){using var dialog=new WorkerDetailsForm(engine,key);dialog.ShowDialog(this);Render();return;}
            if(key.Kind is ResourceKind.D1 or ResourceKind.R2){using var dialog=new CloudResourceDetailsForm(engine,key);dialog.ShowDialog(this);Render();return;}
            MessageBox.Show(this,"Bu sürümde Pages için kaynak-bazlı performans ayrıntısı yok.","ALAZ OPSDECK");
        };
        string[] names=["Hesap","Tür","Kapsam","Kaynak","Dağıtım bilgisi","Proje (yerel)","Kaynak sağlığı"];
        for(int i=0;i<names.Length;i++){grid.Columns.Add("c"+i,names[i]);grid.Columns[i].ReadOnly=i!=5;grid.Columns[i].SortMode=DataGridViewColumnSortMode.NotSortable;grid.Columns[i].Width=i==4?190:i==3?190:i==6?115:115;}
        grid.Columns[5].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        grid.Columns[5].MinimumWidth=120;
        var healthButton=new Button{Text="Proje sağlık özeti",AutoSize=true};commands.Controls.Add(healthButton);
        healthButton.Click+=(_,_)=>{using var dialog=new ProjectHealthForm(engine);dialog.ShowDialog(this);};
        var filterBar=MakeFilters();
        Controls.Add(grid);Controls.Add(counts);Controls.Add(summary);Controls.Add(filterBar);Controls.Add(commands);Controls.Add(notice);
        search.TextChanged+=(_,_)=>Render();grid.CellEndEdit+=(_,e)=>RecordEdit(e.RowIndex);
        refresh.Click+=async(_,_)=>await Discover();save.Click+=(_,_)=>SaveMap();
        Shown+=(_,_)=>{inventory=engine.Inventory;Render();};FormClosing+=OnClosing;
        FormClosed+=(_,_)=>stop.Dispose();
    }
    private void RecordEdit(int row)
    {
        if(row<0||row>=grid.Rows.Count||grid.Rows[row].Tag is not ResourceKey key)return;
        string value=Convert.ToString(grid.Rows[row].Cells[5].Value)?.Trim()??"";
        assignments.TryGetValue(key,out string? prior);if(value==(prior??""))return;
        if(value.Length==0)assignments.Remove(key);else assignments[key]=value;dirty=true;
        if(!rendering&&!closing&&IsHandleCreated)BeginInvoke((Action)(()=>{if(!closing&&!IsDisposed)Render();}));
    }
    private void Render()=>RenderFiltered();
    private async Task Discover()
    {
        if(loading)return;loading=true;refresh.Enabled=save.Enabled=false;notice.Text="Salt-okunur keşif sürüyor; PC/panel aktarımı devam eder.";
        try {inventory=await engine.DiscoverResources(stop.Token);Render();notice.Text="Liste anlık görüntüdür; proje sütunundan yerel gruplama yapılır. Tekrar sorgular en az 60 saniye aralıklıdır.";}
        catch(OperationCanceledException){notice.Text="Keşif iptal edildi; önceki eşlemeler korunuyor.";}
        catch(Exception e)when(e is IOException or InvalidOperationException or ArgumentException){notice.Text="Keşif tamamlanamadı: "+e.GetType().Name;}
        finally {loading=false;refresh.Enabled=save.Enabled=true;if(closing)Close();}
    }
    private void SaveMap()
    {
        grid.EndEdit();
        try {
            original=store.Save(assignments.Select(x=>new ProjectAssignment(x.Key,x.Value)),original.Revision);dirty=false;engine.ReloadProjectMappings();
            notice.Text="Yerel proje eşlemeleri kaydedildi. Cloudflare kaynaklarında değişiklik yapılmadı.";
        } catch(Exception e)when(e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) {
            MessageBox.Show(this,e is ArgumentException or InvalidOperationException?e.Message:"Yerel kayıt başarısız: "+e.GetType().Name,"Proje eşlemeleri");
        }
    }
    private void OnClosing(object? sender,FormClosingEventArgs e)
    {
        grid.EndEdit();
        if(!closing&&dirty&&MessageBox.Show(this,"Kaydedilmemiş proje değişiklikleri var. Kaydetmeden kapatılsın mı?","ALAZ OPSDECK",MessageBoxButtons.YesNo)!=DialogResult.Yes){e.Cancel=true;return;}
        closing=true;if(loading){e.Cancel=true;stop.Cancel();}
    }
}
