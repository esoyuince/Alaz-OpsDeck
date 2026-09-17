using OpsDeck.Core;
namespace OpsDeck.Host;

public sealed partial class ResourcesForm
{
    private sealed record AccountChoice(string Caption,string? Id){public override string ToString()=>Caption;}
    private sealed record KindChoice(string Caption,ResourceKind? Kind){public override string ToString()=>Caption;}
    private sealed record ProjectChoice(string Caption,ProjectFilterMode Mode,string? Project){public override string ToString()=>Caption;}
    private readonly ComboBox accountFilter=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=185};
    private readonly ComboBox kindFilter=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=140};
    private readonly ComboBox projectFilter=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=235};
    private readonly Label counts=new(){Dock=DockStyle.Top,Height=30,Padding=new Padding(8,3,8,3)};
    private bool rendering;
    private FlowLayoutPanel MakeFilters()
    {
        var bar=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(8,0,8,6)};
        accountFilter.Items.Add(new AccountChoice("Tüm hesaplar",null));accountFilter.SelectedIndex=0;
        kindFilter.Items.Add(new KindChoice("Tüm türler",null));
        foreach(var kind in Enum.GetValues<ResourceKind>())kindFilter.Items.Add(new KindChoice(kind.ToString(),kind));
        kindFilter.SelectedIndex=0;
        projectFilter.Items.Add(new ProjectChoice("Tüm projeler",ProjectFilterMode.All,null));projectFilter.SelectedIndex=0;
        bar.Controls.Add(accountFilter);bar.Controls.Add(kindFilter);bar.Controls.Add(projectFilter);
        var reset=new Button{Text="Filtreleri temizle",AutoSize=true};bar.Controls.Add(reset);
        accountFilter.SelectedIndexChanged+=(_,_)=>Render();kindFilter.SelectedIndexChanged+=(_,_)=>Render();projectFilter.SelectedIndexChanged+=(_,_)=>Render();
        reset.Click+=(_,_)=>{if(rendering)return;rendering=true;try{grid.EndEdit();accountFilter.SelectedIndex=0;kindFilter.SelectedIndex=0;projectFilter.SelectedIndex=0;search.Clear();}finally{rendering=false;}Render();};
        return bar;
    }
    private InventoryFilter CurrentFilter()
    {
        var a=accountFilter.SelectedItem as AccountChoice;var k=kindFilter.SelectedItem as KindChoice;
        var p=projectFilter.SelectedItem as ProjectChoice;
        return new(a?.Id,k?.Kind,p?.Mode??ProjectFilterMode.All,p?.Project,search.Text);
    }
    private void RefreshChoices()
    {
        var old=CurrentFilter();var accounts=new List<AccountChoice>{new("Tüm hesaplar",null)};
        foreach(var a in engine.Accounts.Where(a=>a.Profile.AccountId.Length>0))
            accounts.Add(new(a.Profile.Name,a.Profile.AccountId.ToLowerInvariant()));
        foreach(var set in inventory.Sets.Where(s=>s.AccountId.Length>0))
            if(!accounts.Any(a=>a.Id==set.AccountId))accounts.Add(new(set.ProfileName,set.AccountId));
        if(old.AccountId!=null&&!accounts.Any(a=>a.Id==old.AccountId))
            accounts.Add(new("Önceki hesap (veri yok)",old.AccountId));
        if(!accountFilter.Items.Cast<AccountChoice>().SequenceEqual(accounts)){
            accountFilter.Items.Clear();accountFilter.Items.AddRange(accounts.Cast<object>().ToArray());
            accountFilter.SelectedIndex=accounts.FindIndex(a=>a.Id==old.AccountId);
        }
        var projects=new List<ProjectChoice>{new("Tüm projeler",ProjectFilterMode.All,null),new("Atanmamış kaynaklar",ProjectFilterMode.Unassigned,null)};
        foreach(string p in InventoryFiltering.Projects(assignments,old))projects.Add(new(p,ProjectFilterMode.Named,p));
        if(old.ProjectMode==ProjectFilterMode.Named&&!projects.Any(p=>p.Project==old.Project))
            projects.Add(new(old.Project+" (bu kapsamda yok)",ProjectFilterMode.Named,old.Project));
        if(!projectFilter.Items.Cast<ProjectChoice>().SequenceEqual(projects)){
            projectFilter.Items.Clear();projectFilter.Items.AddRange(projects.Cast<object>().ToArray());
            projectFilter.SelectedIndex=projects.FindIndex(p=>p.Mode==old.ProjectMode&&p.Project==old.Project);
        }
    }
    private void RenderFiltered()
    {
        if(rendering||IsDisposed)return;rendering=true;
        try {
            grid.EndEdit();var selected=grid.CurrentRow?.Tag as ResourceKey;RefreshChoices();
            var filter=CurrentFilter();var view=InventoryFiltering.Apply(inventory,assignments,filter);
            var health=engine.ReadProjectResourceHealth(DateTimeOffset.UtcNow);grid.Rows.Clear();
            foreach(var item in view.Rows){
                var h=health.TryGetValue(item.Resource.Key,out var observed)?observed.Health:ProjectHealthState.Unknown;
                int n=grid.Rows.Add(item.Source.ProfileName,item.Resource.Key.Kind,item.Resource.Key.Scope,
                    item.Resource.Name,item.Resource.Deployment,item.Project??"",ProjectHealthEngine.Label(h));
                grid.Rows[n].Tag=item.Resource.Key;if(item.Resource.Key==selected)grid.CurrentCell=grid.Rows[n].Cells[0];
            }
            int ok=view.Rows.Count(x=>health.TryGetValue(x.Resource.Key,out var h)&&h.Health==ProjectHealthState.Ok);
            int att=view.Rows.Count(x=>health.TryGetValue(x.Resource.Key,out var h)&&h.Health==ProjectHealthState.Attention);
            int deg=view.Rows.Count(x=>health.TryGetValue(x.Resource.Key,out var h)&&h.Health==ProjectHealthState.Degraded);
            counts.Text=$"Görünen: {view.Rows.Length} / keşfedilen: {view.DiscoveredInScope} · Kaynak kapsamı: {view.CompleteSourceCount}/{view.SourceCount} liste tamam · Health OK {ok} / ATT {att} / DEG {deg} / UNKNOWN {Math.Max(0,view.Rows.Length-ok-att-deg)}";
            if(view.DiscoveredInScope==0&&!view.ScopeComplete)counts.Text="Kaynak sayısı henüz doğrulanmadı; boş görünüm sıfır kaynak kanıtı değildir.";
            if(inventory.Sets.Length==0)summary.Text="Henüz keşif yapılmadı. Hesapları yerel ayarlarda bağla; sonra Kaynakları keşfet.\r\nFiltreler yalnız görünümü değiştirir; gizlenen proje eşlemeleri korunur.";
            else summary.Text=string.Join(Environment.NewLine,inventory.Sets.Where(filter.Includes).Select(s=>
                s.State==SourceState.Setup?$"{s.ProfileName} / {s.Kind}: bağlantı kurulmadı; kaynak sayısı bilinmiyor.":
                $"{s.ProfileName} / {s.Kind}: {s.State} · {s.Items.Length} kaynak · {(s.Complete?"bu kapsamda liste tamam":"EKSİK liste")} · {s.CollectedAt.ToLocalTime():HH:mm:ss} · {s.Detail}"));
        }catch(InvalidDataException){grid.Rows.Clear();counts.Text="Kaynak kimlikleri doğrulanamadı";summary.Text="Liste farklı hesap/tür verisi içeriyor; sonuç gösterilmedi.";}
        finally{rendering=false;}
    }
}
