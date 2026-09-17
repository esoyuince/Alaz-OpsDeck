using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;
public sealed record CloudAccountConfig
{
    public string ProfileId{get;init;}="";public string Name{get;init;}="";public string AccountId{get;init;}="";
    public bool Enabled{get;init;}public bool BillingEnabled{get;init;}public string[] Sites{get;init;}=[];
    public decimal? FixedMonthlyUsd{get;init;}
    public void Validate()
    {
        if(!Regex.IsMatch(ProfileId,"^[a-z0-9_-]{1,32}$"))throw new ArgumentException("Geçersiz profil kimliği.");
        if(string.IsNullOrWhiteSpace(Name)||Name.Length>32||Name.Any(char.IsControl))throw new ArgumentException("Hesap adı 1-32 karakter olmalı.");
        if(AccountId.Length>0&&!Regex.IsMatch(AccountId,"^[a-fA-F0-9]{32}$"))throw new ArgumentException("Hesap ID'si 32 hexadecimal karakter olmalı.");
        if(Enabled&&AccountId.Length==0)throw new ArgumentException("Etkin hesap için Account ID gerekli.");
        if(FixedMonthlyUsd is <0 or >1000000)throw new ArgumentException("Aylık sabit ücret 0–1.000.000 USD aralığında olmalı.");
        if(Sites.Length>12)throw new ArgumentException("Bir profilde en fazla 12 site.");foreach(var site in Sites)HostConfig.ValidateSite(site);
    }
    public static CloudAccountConfig[] Defaults=>[new(){ProfileId="vetakeep",Name="VetaKeep"},new(){ProfileId="projects",Name="Diğer Projeler"}];
}
public sealed record AccountState(CloudAccountConfig Profile,Metric Workers,Metric D1,Metric R2,Metric Cost,Metric Hosting,Metric? Subscription=null)
{
    public static AccountState Empty(CloudAccountConfig p)=>new(p,Metric.Setup(),Metric.Setup(),Metric.Setup(),Metric.Setup(),Metric.Setup());
    public string Wire(int slot,int total,string generation,DateTimeOffset now)=>JsonSerializer.Serialize(new {type="opsdeck.cloud.v1",slot,total,generation,name=PanelName(Profile.Name),enabled=Profile.Enabled,workers=Workers.Wire(now,180),d1=D1.Wire(now,180),r2=R2.Wire(now,900),hosting=Hosting.Wire(now,180),cost=BillingDisplay.ForAccount(this,now).Wire(now,7200)},Json.Options);
    public static string PanelName(string name)
    {
        string ascii=name.Replace('ı','i').Replace('İ','I').Replace('ş','s').Replace('Ş','S').Replace('ğ','g').Replace('Ğ','G').Normalize(NormalizationForm.FormD);
        return new string(ascii.Where(c=>c>=32&&c<=126).Take(22).ToArray());
    }
}
public static class AccountAggregate
{
    public static Metric Combine(IEnumerable<AccountState> all,Func<AccountState,Metric> select,DateTimeOffset now,int ttl,bool cost=false)
    {
        var accounts=all.Where(a=>a.Profile.Enabled).ToArray();if(accounts.Length==0)return Metric.Setup("No enabled Cloudflare accounts");
        if(accounts.Select(a=>a.Profile.AccountId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=accounts.Length)return new(SourceState.Error,Detail:"Duplicate account; no total",Expected:accounts.Length);
        var available=accounts.Select(select).Where(m=>Freshness.MetricState(m,now,ttl)==SourceState.Ok&&m.Value.HasValue&&double.IsFinite(m.Value.Value)&&Freshness.IsCurrent(m.CollectedAt,now,ttl)).ToArray();
        if(available.Length==0)return new(SourceState.Partial,Detail:cost?"Toplam yok: kaynak tarihi eski/bilinmiyor veya hesap dönemleri eksik. Hesapların son kayıtlarını ayrı inceleyin.":$"0/{accounts.Length} accounts available",Covered:0,Expected:accounts.Length,Note:cost?"SOURCE / PERIOD":"NO ACCOUNTS");
        bool sameUnit=available.Select(m=>m.Unit).Distinct(StringComparer.Ordinal).Count()==1;
        bool samePeriod=!cost||available.All(m=>m.PeriodStart.HasValue&&m.PeriodEnd.HasValue)&&available.Select(m=>(m.PeriodStart,m.PeriodEnd)).Distinct().Count()==1;
        bool sameScope=!cost||(available.Select(m=>m.UsageCharges).Distinct().Count()==1&&
            (!available[0].UsageCharges||available.All(m=>m.SourceEnd.HasValue)&&available.Select(m=>m.SourceEnd).Distinct().Count()==1));
        if(!sameScope)return new(SourceState.Partial,Detail:"Usage scope or source cutoff differs; total withheld",Covered:available.Length,Expected:accounts.Length,Note:"SCOPE / SOURCE END");
        if(!sameUnit||!samePeriod)return new(SourceState.Partial,Detail:"Currency, unit or billing period differs/unknown; totals withheld",Covered:available.Length,Expected:accounts.Length,Note:"PERIOD / CURRENCY");
        decimal value=available.Sum(m=>(decimal)m.Value!.Value);double? secondary=available.All(m=>m.Secondary.HasValue)?available.Sum(m=>m.Secondary!.Value):null;
        return new(available.Length==accounts.Length?SourceState.Ok:SourceState.Partial,(double)value,secondary,available.Min(m=>m.CollectedAt),available.Min(m=>m.SourceEnd),available[0].Unit,$"{available.Length}/{accounts.Length} accounts; {(available.Length==accounts.Length?"complete":"known subtotal, incomplete")}",available[0].PeriodStart,available[0].PeriodEnd,available.Length,accounts.Length,Note:cost?(available.Length==accounts.Length?"COMPARABLE TOTAL":"KNOWN SUBTOTAL"):"",UsageCharges:cost&&available.All(m=>m.UsageCharges));
    }
}