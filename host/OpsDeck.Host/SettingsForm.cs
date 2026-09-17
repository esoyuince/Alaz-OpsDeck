using OpsDeck.Core;
namespace OpsDeck.Host;
public sealed class SettingsForm : Form
{
    private readonly LocalSettings settings;private readonly HostConfig original;
    private readonly TextBox port=new(),nic=new(),bridge=new(),bridgeTasks=new(),edgeNodeUrl=new(),sites=new(){Multiline=true,Height=80,ScrollBars=ScrollBars.Vertical};
    private readonly CheckBox nvidia=new(){Text="NVIDIA sıcaklığını boşta da sorgula (güç tüketimini artırabilir)",AutoSize=true};
    private readonly CheckBox edgeNode=new(){Text="ALAZ EdgeNode telemetrisini Tailscale üzerinden izle",AutoSize=true};
    private readonly CheckBox autostart=new(){Text="Windows oturum açıldığında OpsDeck'i otomatik başlat",AutoSize=true};
    private readonly Label autostartState=new(){AutoSize=true};
    private readonly List<ProfileEditor> editors=[];
    public SettingsForm(LocalSettings settings)
    {
        this.settings=settings;original=settings.Load();Text="ALAZ OPSDECK — Bağlantılar";ClientSize=new Size(830,670);MinimumSize=new Size(760,640);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
        var tabs=new TabControl{Dock=DockStyle.Fill};Controls.Add(tabs);
        TableLayoutPanel Table(string title){var tab=new TabPage(title);tabs.TabPages.Add(tab);var t=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(16),AutoScroll=true};t.ColumnStyles.Add(new(SizeType.Absolute,150));t.ColumnStyles.Add(new(SizeType.Percent,100));tab.Controls.Add(t);return t;}
        var general=Table("Bilgisayar / USB");int row=0;
        port.Text=original.Port;nic.Text=original.NetworkInterface;bridge.Text=original.BridgeHealthUrl;bridgeTasks.Text=original.BridgeTaskDatabasePath;edgeNode.Checked=original.EdgeNodeEnabled;edgeNodeUrl.Text=original.EdgeNodeStatusUrl;sites.Text=string.Join(Environment.NewLine,original.Sites);nvidia.Checked=original.DetailedNvidiaSensors;
        var startup=AutoStartManager.Read();autostart.Checked=startup.Enabled;autostartState.Text=startup.Detail;
        autostart.CheckedChanged+=(_,_)=>autostartState.Text=autostart.Checked?"Kaydedildiğinde sabit OPSDECK_BASLAT.cmd launcher kaydı etkinleştirilecek.":"Kaydedildiğinde Windows açılış kaydı kaldırılacak.";
        Add(general,ref row,"USB",port);Add(general,ref row,"Ağ arayüzü",nic);Add(general,ref row,"Bridge /health",bridge);Add(general,ref row,"Bridge görev DB",bridgeTasks);Add(general,ref row,"EdgeNode",edgeNode);Add(general,ref row,"EdgeNode status",edgeNodeUrl);Add(general,ref row,"EdgeNode güvenlik",new Label{AutoSize=true,Text="Yalnız http://100.64.0.0/10:8787/latest.json veya .ts.net adı kabul edilir. LAN/public HTTP hedefi kabul edilmez; bu sürüm salt-okunur."});Add(general,ref row,"GPU sensörü",nvidia);Add(general,ref row,"Otomatik başlat",autostart);Add(general,ref row,"Başlangıç kaydı",autostartState);Add(general,ref row,"Genel HTTPS siteler",sites);
        Add(general,ref row,"Sensörler",new Label{AutoSize=true,Text="Intel ve NVIDIA kullanımı WDDM üzerinden ayrı okunur.\nCPU, chassis ve NVIDIA sıcaklığı ile fan RPM ayrı gösterilir.\nFan kontrolü, driver kurulumu ve otomatik yönetici yükseltmesi yok."});
        foreach(var profile in original.EffectiveAccounts()){
            var e=new ProfileEditor(profile);editors.Add(e);var table=Table(profile.Name);int r=0;
            Add(table,ref r,"Profil adı",e.Name);Add(table,ref r,"Bağlantı",e.Enabled);Add(table,ref r,"Account ID",e.Account);Add(table,ref r,"API token",e.Token);Add(table,ref r,"Token durumu",e.TokenState);Add(table,ref r,"",e.Remove);Add(table,ref r,"Maliyet",e.Billing);Add(table,ref r,"Sabit USD / ay",e.Monthly);
            Add(table,ref r,"Ücret kaynağı",new Label{AutoSize=true,Text="Boş: sabit ücret tanımlanmamış. API okunamazsa bu yerel tutar korunur.\nAPI aktif aylık Workers aboneliğini doğrularsa onun tutarı kullanılır; iki kez eklenmez."});Add(table,ref r,"Bu hesabın siteleri",e.Sites);
            Add(table,ref r,"Güvenlik",new Label{AutoSize=true,Text="Her hesap için ayrı salt-okunur token. Buraya gir; sohbete gönderme.\nBoş token alanı o Account ID'nin kayıtlı anahtarını korur.\nAccount ID değişirse eski hesabın anahtarı yeni hesaba taşınmaz.\nBu ekran Cloudflare kaynaklarını silmez veya değiştirmez."});
            void TokenHint(){e.TokenState.Text=System.Text.RegularExpressions.Regex.IsMatch(e.Account.Text.Trim(),"^[a-fA-F0-9]{32}$")&&settings.HasTokenForAccount(e.Account.Text.Trim())?"Bu hesap için anahtar kayıtlı (Windows DPAPI).":"Bu hesap için kayıtlı anahtar yok.";}
            e.Account.TextChanged+=(_,_)=>TokenHint();TokenHint();
        }
        var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=50,Padding=new Padding(8)};
        var save=new Button{Text="Kaydet ve bağlantıları yenile",AutoSize=true};save.Click+=(_,_)=>Save();footer.Controls.Add(save);var cancel=new Button{Text="İptal",AutoSize=true};cancel.Click+=(_,_)=>Close();footer.Controls.Add(cancel);Controls.Add(footer);AcceptButton=save;OpsDeckTheme.Apply(this);OpsDeckTheme.StyleButton(save,true);OpsDeckTheme.StyleButton(cancel);
    }
    private static void Add(TableLayoutPanel t,ref int row,string label,Control control)
    {t.RowStyles.Add(new(SizeType.AutoSize));t.Controls.Add(new Label{Text=label,AutoSize=true,Padding=new Padding(0,6,0,0)},0,row);control.Dock=DockStyle.Top;t.Controls.Add(control,1,row++);}
    private void Save()
    {
        try {
            string[] Lines(TextBox b)=>b.Lines.Select(x=>x.Trim()).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            decimal? Monthly(ProfileEditor e){if(string.IsNullOrWhiteSpace(e.Monthly.Text))return null;if(!decimal.TryParse(e.Monthly.Text.Trim(),System.Globalization.NumberStyles.AllowDecimalPoint,System.Globalization.CultureInfo.CurrentCulture,out var v)||v<0||v>1000000)throw new ArgumentException("Aylık sabit ücret geçersiz.");return v;}
            var accounts=editors.Select(e=>e.Original with{FixedMonthlyUsd=Monthly(e),Name=e.Name.Text.Trim(),AccountId=e.Account.Text.Trim().ToLowerInvariant(),Enabled=e.Enabled.Checked,BillingEnabled=e.Billing.Checked,Sites=Lines(e.Sites)}).ToArray();
            var config=original with{SchemaVersion=3,Port=port.Text.Trim().ToUpperInvariant(),NetworkInterface=nic.Text.Trim(),BridgeHealthUrl=bridge.Text.Trim(),BridgeTaskDatabasePath=bridgeTasks.Text.Trim(),EdgeNodeEnabled=edgeNode.Checked,EdgeNodeStatusUrl=edgeNodeUrl.Text.Trim(),DetailedNvidiaSensors=nvidia.Checked,Accounts=accounts,CloudflareAccountId="",CloudflareEnabled=false,BillingEnabled=false,Sites=Lines(sites)};config.Validate();
            for(int i=0;i<editors.Count;i++){
                var e=editors[i];var a=accounts[i];string token=e.Token.Text.Trim();
                if(token.Length>0){LocalSettings.ValidateToken(token);if(a.AccountId.Length==0)throw new ArgumentException("Token için Account ID gerekli.");}
                if(e.Remove.Checked&&token.Length>0)throw new ArgumentException("Aynı anda token silme ve değiştirme seçilemez.");
                if(a.Enabled&&token.Length==0&&(e.Remove.Checked||!settings.HasTokenForAccount(a.AccountId)))throw new ArgumentException(a.Name+": token gir veya bağlantıyı kapat.");
            }
            for(int i=0;i<editors.Count;i++){
                var e=editors[i];string id=accounts[i].AccountId,token=e.Token.Text.Trim();if(e.Remove.Checked&&id.Length>0)settings.DeleteTokenForAccount(id);if(token.Length>0)settings.SaveTokenForAccount(id,token);
            }
            settings.Save(config);foreach(var e in editors)e.Token.Clear();
            try{AutoStartManager.SetEnabled(autostart.Checked);}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or ArgumentException){MessageBox.Show(this,"Diğer ayarlar kaydedildi ancak Windows otomatik başlatma kaydı değiştirilemedi: "+e.Message,"ALAZ OPSDECK — Başlangıç");return;}
            DialogResult=DialogResult.OK;Close();
        }catch(ArgumentException e){MessageBox.Show(this,e.Message,"ALAZ OPSDECK — Ayar kontrolü");}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException){MessageBox.Show(this,"Ayar kaydedilemedi: "+e.GetType().Name,"ALAZ OPSDECK");}
    }
    private sealed class ProfileEditor
    {
        public CloudAccountConfig Original{get;}
        public TextBox Monthly=new(){PlaceholderText="Örn. "+5m.ToString("0.00",System.Globalization.CultureInfo.CurrentCulture)};
        public TextBox Name=new(),Account=new(),Token=new(){UseSystemPasswordChar=true},Sites=new(){Multiline=true,Height=90,ScrollBars=ScrollBars.Vertical};
        public CheckBox Enabled=new(){Text="Bu hesabı salt-okunur izle",AutoSize=true},Billing=new(){Text="Billable Usage sorgula (ayrı yetki gerekebilir)",AutoSize=true},Remove=new(){Text="Bu hesaba ait kayıtlı tokenı kaldır",AutoSize=true};public Label TokenState=new(){AutoSize=true};
        public ProfileEditor(CloudAccountConfig p){Original=p;Monthly.Text=p.FixedMonthlyUsd?.ToString(System.Globalization.CultureInfo.CurrentCulture)??"";Name.Text=p.Name;Account.Text=p.AccountId;Enabled.Checked=p.Enabled;Billing.Checked=p.BillingEnabled;Sites.Text=string.Join(Environment.NewLine,p.Sites);}
    }
}
