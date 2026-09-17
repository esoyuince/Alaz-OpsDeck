using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;

// A last-reading summary is NOT a comparable-period invoice. Raw Cost/Combine stay strict.
public static class BillingDisplay
{
    public static bool HasReading(Metric m)=>m.Value is >=0 and <=1e16 && double.IsFinite(m.Value.Value)
        &&m.CollectedAt.HasValue&&Regex.IsMatch(m.Unit,"^[A-Z]{3}$");
    public static Metric ForAccount(AccountState a,DateTimeOffset now)
    {
        if(!a.Profile.Enabled||!a.Profile.BillingEnabled)return Metric.Setup("Ücret okuma kapalı.") with{LastRead=true};
        var raw=a.Cost;bool usage=raw.UsageCharges&&HasReading(raw);
        var sub=a.Subscription;
        bool apiFee=sub!=null&&HasReading(sub)&&sub.State==SourceState.Ok&&sub.Unit=="USD"
            &&Freshness.IsCurrent(sub.CollectedAt,now,7200)&&(!sub.PeriodEnd.HasValue||sub.PeriodEnd>now);
        double? fee=apiFee?sub!.Value:a.Profile.FixedMonthlyUsd.HasValue?(double)a.Profile.FixedMonthlyUsd.Value:null;
        string feeSource=apiFee?"abonelik API":"yerel ayar";
        string unit=usage?raw.Unit:fee.HasValue?"USD":"";
        if(usage&&fee.HasValue&&unit!="USD")return new(SourceState.Partial,Unit:unit,Detail:"Kullanım ve sabit ücretin para birimleri farklı; toplama yapılmadı.",Note:"CURRENCY MISMATCH",LastRead:true,Covered:0);
        if(!usage&&!fee.HasValue)return raw with{Value=null,Secondary=null,LastRead=true,UsageCharges=false,Covered=0,
            Note=raw.State==SourceState.NoData?"NO USAGE RECORDS":"WAITING FOR READING",
            Detail="Henüz okunmuş kullanım ücreti yok. Boş API yanıtı sıfır ücret sayılmaz."};
        double? value=(double)((usage?(decimal)raw.Value!.Value:0)+(fee.HasValue?(decimal)fee.Value:0));
        var state=raw.State is SourceState.Error or SourceState.Denied?raw.State:usage?(raw.Covered==0||raw.State==SourceState.NoData?SourceState.Partial:SourceState.Ok):SourceState.Partial;
        if(value>1e16)return new(SourceState.Error,Detail:"Display amount out of range.",Note:"AMOUNT OUT OF RANGE",LastRead:true,Covered:0);
        string note=!usage?"MONTHLY ONLY":raw.Covered==0||raw.State==SourceState.NoData?"SAVED LAST READING":fee.HasValue?"LAST READ + MONTHLY":"LAST READ";
        string amount=usage?$"{raw.Value:0.00} {raw.Unit}":"kayıt yok";
        string fixedText=fee.HasValue?$"{fee:0.00} USD/ay ({feeSource})":"tanımlı ücret yok";
        string subState=sub?.State is SourceState.Error or SourceState.Denied?$" Abonelik API: {sub.State}; yerel ayar korunuyor.":"";
        return new(state,value,fee,usage?raw.CollectedAt:apiFee?sub!.CollectedAt:null,raw.SourceEnd,unit,
            $"Son okunan kullanım: {amount}; sabit: {fixedText}. Kaynak son kaydı: {MetricPresentation.Date(raw.SourceEnd)??"belirtilmedi"}. Fatura/dönem toplamı değildir.{subState}",
            raw.PeriodStart,raw.PeriodEnd,usage?1:0,1,note,LastRead:true);
    }
    public static Metric CombineLastRead(IEnumerable<AccountState> source,DateTimeOffset now)
    {
        var accounts=source.Where(a=>a.Profile.Enabled).ToArray();
        if(accounts.Length==0)return Metric.Setup("Etkin hesap yok.") with{LastRead=true};
        if(accounts.Length>2||accounts.Select(a=>a.Profile.AccountId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=accounts.Length)
            return new(SourceState.Error,Detail:"Tekrarlanan hesap; toplama yapılmadı.",Covered:0,Expected:Math.Min(2,accounts.Length),LastRead:true);
        var shown=accounts.Select(a=>ForAccount(a,now)).ToArray();var known=shown.Where(m=>m.Value.HasValue).ToArray();
        if(known.Length==0)return new(shown.Any(m=>m.State==SourceState.Denied)?SourceState.Denied:shown.Any(m=>m.State==SourceState.Error)?SourceState.Error:SourceState.NoData,
            Detail:"Henüz okunmuş ücret kaydı yok.",Covered:0,Expected:accounts.Length,Note:"NO USAGE RECORDS",LastRead:true);
        if(shown.Any(m=>m.Note=="CURRENCY MISMATCH")||known.Select(m=>m.Unit).Distinct(StringComparer.Ordinal).Count()!=1)
            return new(SourceState.Partial,Detail:"Farklı para birimleri; hesapları ayrı inceleyin.",Covered:0,Expected:accounts.Length,Note:"CURRENCY MISMATCH",LastRead:true);
        if(known.Sum(m=>(decimal)m.Value!.Value)>10000000000000000m)return new(SourceState.Error,Covered:0,Expected:accounts.Length,Note:"AMOUNT OUT OF RANGE",LastRead:true);
        int covered=shown.Sum(m=>m.Covered);var state=covered==accounts.Length&&shown.All(m=>Freshness.MetricState(m,now,7200)==SourceState.Ok)?SourceState.Ok:SourceState.Partial;
        string note=shown.Any(m=>m.State==SourceState.Denied)?"LAST READ / API DENIED":shown.Any(m=>m.State==SourceState.Error)?"LAST READ / API ERROR":$"LAST READ {covered}/{accounts.Length}";
        // No PeriodStart/End on this deliberately cross-period sum.
        return new(state,(double)known.Sum(m=>(decimal)m.Value!.Value),known.Any(m=>m.Secondary.HasValue)?(double)known.Sum(m=>(decimal)(m.Secondary??0)):null,
            known.Where(m=>m.CollectedAt.HasValue).Select(m=>m.CollectedAt).Min(),known.Where(m=>m.SourceEnd.HasValue).Select(m=>m.SourceEnd).Max(),known[0].Unit,
            "Hesapların son okunan tutarları + bilinen aylık sabitler. Dönemler farklı olabilir; fatura değildir. "+string.Join(" | ",accounts.Zip(shown,(a,m)=>$"{a.Profile.Name}: {(m.Value.HasValue?m.Value.Value.ToString("0.00",CultureInfo.InvariantCulture)+" "+m.Unit:"kayıt yok")}")),
            Covered:covered,Expected:accounts.Length,Note:note,LastRead:true);
    }
}

// Only sanitized successful usage snapshots are persisted. No credentials or raw API responses.
public sealed class BillingReadingStore(string directory)
{
    private string PathFor(string account)
    {
        if(!Regex.IsMatch(account,"^[a-fA-F0-9]{32}$"))throw new ArgumentException("Invalid account identity.");
        return Path.Combine(directory,"billing-last-"+account.ToLowerInvariant()+".json");
    }
    public Metric? Load(string account,DateTimeOffset now)
    {
        string path=PathFor(account);if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>8192)throw new InvalidDataException("Oversized saved reading.");
        var saved=JsonSerializer.Deserialize<Saved>(File.ReadAllText(path),Json.Options);
        if(saved==null||saved.Version!=1||!string.Equals(saved.Account,account,StringComparison.OrdinalIgnoreCase)||!Valid(saved.Reading,now))
            throw new InvalidDataException("Invalid saved reading.");
        return saved.Reading with{State=SourceState.Stale,Note="SAVED LAST READING"};
    }
    public void Save(string account,Metric reading,DateTimeOffset now)
    {
        string path=PathFor(account);if(!Valid(reading,now)||reading.Covered!=1||reading.State is SourceState.Error or SourceState.Denied or SourceState.NoData)
            throw new InvalidDataException("Only successful usage readings can be saved.");
        var clean=reading with{Detail="Saved usage records; not an invoice.",Note="USAGE RECORDS"};
        Directory.CreateDirectory(directory);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(new Saved(1,account.ToLowerInvariant(),clean),Json.Options));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private static bool Valid(Metric? m,DateTimeOffset now)=>m!=null&&m.State is SourceState.Ok or SourceState.Partial or SourceState.Stale&&m.UsageCharges&&!m.LastRead&&BillingDisplay.HasReading(m)
        &&m.Secondary==null&&m.Expected==1&&m.Covered==1&&m.CollectedAt<=now.AddSeconds(5)
        &&(!m.PeriodStart.HasValue||!m.PeriodEnd.HasValue||m.PeriodEnd>m.PeriodStart);
    private sealed record Saved(int Version,string Account,Metric Reading);
}

public sealed partial class AppEngine
{
    private Metric ReadSavedCost(CloudAccountConfig p)
    {
        if(!p.Enabled||!p.BillingEnabled||p.AccountId.Length==0)return Metric.Setup();
        try{return new BillingReadingStore(settings.DirectoryPath).Load(p.AccountId,DateTimeOffset.UtcNow)??Metric.Setup();}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {log.Event("billing_cache_unavailable",new{kind=e.GetType().Name});return Metric.Setup();}
    }
    private void PutCost(int slot,Metric incoming)
    {
        var previous=Accounts[slot].Cost;
        if(!BillingDisplay.HasReading(incoming)&&incoming.State is SourceState.NoData or SourceState.Partial&&BillingDisplay.HasReading(previous))
        {UpdateAccount(slot,a=>a with{Cost=previous with{State=incoming.State,Covered=0,Detail="No usable new usage records; previous reading retained.",Note="SAVED LAST READING"}});return;}
        UpdateAccount(slot,a=>a with{Cost=incoming});
        if(incoming.UsageCharges&&BillingDisplay.HasReading(incoming)&&incoming.Covered==1&&incoming.State is SourceState.Ok or SourceState.Stale or SourceState.Partial)
            try{new BillingReadingStore(settings.DirectoryPath).Save(Accounts[slot].Profile.AccountId,incoming,DateTimeOffset.UtcNow);}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){log.Event("billing_cache_write_failed",new{kind=e.GetType().Name});}
    }
}
