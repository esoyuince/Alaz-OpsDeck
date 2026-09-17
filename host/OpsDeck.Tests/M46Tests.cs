using System.Net;
using System.Text;
using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;
public static class M46Tests
{
    public static async Task Run(Action<string,bool> check)
    {
        void C(string n,bool v)=>check("m46-"+n,v);
        void Reject(string n,Action a){try{a();C(n,false);}catch(Exception e)when(e is ArgumentException or SourceFailure or InvalidDataException or JsonException){C(n,true);}}
        JsonElement J(string s)=>JsonDocument.Parse(s).RootElement.Clone();
        var now=new DateTimeOffset(2026,9,14,12,0,0,TimeSpan.Zero);
        var a=new CloudAccountConfig{ProfileId="vetakeep",Name="VetaKeep",AccountId=new string('a',32),Enabled=true,BillingEnabled=true};
        var b=new CloudAccountConfig{ProfileId="projects",Name="Other",AccountId=new string('b',32),Enabled=true,BillingEnabled=true,FixedMonthlyUsd=5};
        Metric Usage(double value)=>new(SourceState.Stale,value,CollectedAt:now,SourceEnd:now.AddDays(-32),Unit:"USD",PeriodStart:now.AddDays(-34),Note:"USAGE RECORDS",UsageCharges:true);
        var x=AccountState.Empty(a) with{Cost=Usage(0)};var y=AccountState.Empty(b) with{Cost=Usage(.45)};
        var dx=BillingDisplay.ForAccount(x,now);var dy=BillingDisplay.ForAccount(y,now);var all=BillingDisplay.CombineLastRead([x,y],now);
        C("zero-old-record-retained",dx.Value==0&&dx.State==SourceState.Ok&&dx.LastRead);
        C("unknown-period-no-main-warning",MetricPresentation.Note(dx,now,7200)=="LAST READ");
        C("fee-once",dy.Value==5.45&&dy.Secondary==5&&dy.Covered==1);
        C("last-read-total",all.Value==5.45&&all.Secondary==5&&all.Covered==2&&all.Expected==2&&all.PeriodStart==null&&all.PeriodEnd==null);
        C("sum-label-explicit",all.Note=="LAST READ 2/2"&&all.Detail.Contains("fatura değildir"));
        C("strict-old-contract-intact",AccountAggregate.Combine([x,y],z=>z.Cost,now,7200,true).Value==null);
        C("disabled-excluded",BillingDisplay.CombineLastRead([x,y with{Profile=b with{Enabled=false}}],now).Value==0);
        C("duplicates-rejected",BillingDisplay.CombineLastRead([x,x],now).Value==null);
        C("billing-disabled-no-fixed",BillingDisplay.ForAccount(y with{Profile=b with{BillingEnabled=false}},now).Value==null);
        C("empty-not-zero",BillingDisplay.ForAccount(x with{Cost=new(SourceState.NoData)},now).Value==null);
        var fixedOnly=BillingDisplay.ForAccount(y with{Cost=new(SourceState.NoData)},now);
        C("no-usage-fixed-only",fixedOnly.Value==5&&fixedOnly.Covered==0&&fixedOnly.Note=="MONTHLY ONLY");
        C("fee-only-denied-visible",BillingDisplay.ForAccount(y with{Cost=Metric.Setup().Failed(SourceState.Denied,"no")},now).State==SourceState.Denied);
        C("saved-denied-retains-amount",BillingDisplay.ForAccount(y with{Cost=y.Cost.Failed(SourceState.Denied,"no")},now).Value==5.45);
        C("saved-denied-note",MetricPresentation.Note(BillingDisplay.ForAccount(y with{Cost=y.Cost.Failed(SourceState.Denied,"no")},now),now,7200)=="PERMISSION DENIED");
        C("aggregate-api-error-visible",BillingDisplay.CombineLastRead([x,y with{Cost=y.Cost.Failed(SourceState.Error,"bad")}],now).Note.Contains("API ERROR"));
        C("mixed-currency-withheld",BillingDisplay.CombineLastRead([x,y with{Profile=b with{FixedMonthlyUsd=null},Cost=y.Cost with{Unit="EUR"}}],now).Value==null);
        C("currency-fee-conflict-not-hidden",BillingDisplay.CombineLastRead([x,y with{Cost=y.Cost with{Unit="EUR"}}],now).Value==null);
        C("old-collection-stale",Freshness.MetricState(BillingDisplay.ForAccount(x with{Cost=x.Cost with{CollectedAt=now.AddHours(-3)}},now),now,7200)==SourceState.Stale);
        C("not-a-fresh-invoice",!all.UsageCharges&&all.LastRead&&all.PeriodEnd==null);
        C("finite-only",!BillingDisplay.HasReading(Usage(double.NaN))&&!BillingDisplay.HasReading(Usage(double.PositiveInfinity)));
        C("aggregate-bound",BillingDisplay.CombineLastRead([x with{Cost=Usage(1e16)},y with{Cost=Usage(1e16),Profile=b with{FixedMonthlyUsd=null}}],now).Value==null);
        Reject("negative-config",()=> (b with{FixedMonthlyUsd=-1}).Validate());
        Reject("huge-config",()=> (b with{FixedMonthlyUsd=1000001}).Validate());
        C("config-roundtrip",JsonSerializer.Deserialize<CloudAccountConfig>(JsonSerializer.Serialize(b,Json.Options),Json.Options)!.FixedMonthlyUsd==5);
        Metric Sub(double price)=>new(SourceState.Ok,price,CollectedAt:now,Unit:"USD",PeriodStart:now.AddDays(-2),PeriodEnd:now.AddDays(28));
        C("api-fee-replaces-config",BillingDisplay.ForAccount(y with{Subscription=Sub(7)},now).Value==7.45);
        C("api-config-not-double-counted",BillingDisplay.ForAccount(y with{Subscription=Sub(5)},now).Value==5.45);
        C("zero-api-fee-valid",BillingDisplay.ForAccount(y with{Subscription=Sub(0)},now).Value==.45);
        C("stale-subscription-config-retained",BillingDisplay.ForAccount(y with{Subscription=Sub(7) with{CollectedAt=now.AddHours(-3)}},now).Value==5.45);
        C("expired-subscription-config-retained",BillingDisplay.ForAccount(y with{Subscription=Sub(7) with{PeriodEnd=now}},now).Value==5.45);
        C("denied-subscription-config-retained",BillingDisplay.ForAccount(y with{Subscription=Sub(7).Failed(SourceState.Denied,"denied")},now).Value==5.45);
        string Row(string name="Workers Paid",string freq="monthly",string state="Paid",string price="5",string currency="USD")=>JsonSerializer.Serialize(new{currency,price=decimal.Parse(price,System.Globalization.CultureInfo.InvariantCulture),frequency=freq,state,current_period_start="2026-09-01T00:00:00Z",current_period_end="2026-10-01T00:00:00Z",rate_plan=new{public_name=name,id="test_plan",scope="account"}});
        string List(params string[] rows)=>"{\"success\":true,\"result\":["+string.Join(',',rows)+"]}";
        var sub=CloudflareClient.ParseWorkersSubscription(J(List(Row())),now);
        C("subscription-monthly-usd",sub.Value==5&&sub.Unit=="USD"&&sub.State==SourceState.Ok);
        C("subscription-unrelated-not-fee",CloudflareClient.ParseWorkersSubscription(J(List(Row("Business"))),now).Value==null);
        C("subscription-empty-not-free",CloudflareClient.ParseWorkersSubscription(J(List()),now).Value==null);
        C("subscription-workers-ai-not-base",CloudflareClient.ParseWorkersSubscription(J(List(Row("Workers AI"))),now).Value==null);
        C("subscription-workers-platforms-not-base",CloudflareClient.ParseWorkersSubscription(J(List(Row("Workers for Platforms"))),now).Value==null);
        C("subscription-other-worker-addon-not-base",CloudflareClient.ParseWorkersSubscription(J(List(Row("Workers Observability"))),now).Value==null);
        C("subscription-base-with-ai-not-double-counted",CloudflareClient.ParseWorkersSubscription(J(List(Row(),Row("Workers AI",price:"20"))),now).Value==5);
        Reject("subscription-invalid-present-date",()=>CloudflareClient.ParseWorkersSubscription(J(List(Row()).Replace("2026-10-01T00:00:00Z","not-a-date")),now));
        Reject("subscription-empty-present-date",()=>CloudflareClient.ParseWorkersSubscription(J(List(Row()).Replace("2026-10-01T00:00:00Z","")),now));
        C("subscription-cancelled-excluded",CloudflareClient.ParseWorkersSubscription(J(List(Row(state:"Cancelled"))),now).Value==null);
        foreach(var bad in new[]{Row(freq:"yearly"),Row(state:"Trial"),Row(currency:"EUR"),Row(price:"-1"),Row(price:"1000001")})Reject("subscription-invalid-"+bad,()=>CloudflareClient.ParseWorkersSubscription(J(List(bad)),now));
        Reject("subscription-duplicate",()=>CloudflareClient.ParseWorkersSubscription(J(List(Row(),Row())),now));
        Reject("subscription-truncated",()=>CloudflareClient.ParseWorkersSubscription(J(List(Row()).Replace("\"success\":true","\"success\":true,\"result_info\":{\"total_count\":2}")),now));
        Reject("subscription-bad-period",()=>CloudflareClient.ParseWorkersSubscription(J(List(Row()).Replace("2026-10-01","2026-08-01")),now));
        C("subscription-expired-excluded",CloudflareClient.ParseWorkersSubscription(J(List(Row()).Replace("2026-10-01","2026-09-02")),now).Value==null);
        var handler=new SubscriptionHandler(List(Row()));using(var client=new CloudflareClient(a.AccountId,"offline-not-real-token",handler))
        {
            await client.WorkersSubscription(CancellationToken.None);
            C("subscription-get-account-only",handler.Uri?.AbsolutePath=="/client/v4/accounts/"+a.AccountId+"/subscriptions"&&handler.Method==HttpMethod.Get&&handler.Uri.Host=="api.cloudflare.com");
        }
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m46-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            var store=new BillingReadingStore(dir);C("cache-absent",store.Load(a.AccountId,now)==null);
            store.Save(a.AccountId,x.Cost,now);var restored=store.Load(a.AccountId,now)!;
            C("cache-value-time-preserved",restored.Value==0&&restored.CollectedAt==now&&restored.SourceEnd==x.Cost.SourceEnd&&restored.State==SourceState.Stale);
            C("cache-account-isolation",store.Load(b.AccountId,now)==null);
            Reject("cache-denied-not-written",()=>store.Save(a.AccountId,x.Cost.Failed(SourceState.Denied,"denied"),now));
            Reject("cache-future-rejected",()=>store.Save(a.AccountId,x.Cost with{CollectedAt=now.AddMinutes(1)},now));
            Reject("cache-path-injection",()=>store.Load("../settings",now));
            Reject("cache-invalid-value",()=>store.Save(a.AccountId,x.Cost with{Value=double.NaN},now));
            string path=Directory.GetFiles(dir).Single();string saved=File.ReadAllText(path);
            C("cache-no-credentials",!saved.Contains("token")&&!saved.Contains("Authorization"));
            File.WriteAllText(path,saved.Replace(a.AccountId,b.AccountId));Reject("cache-wrong-account-rejected",()=>store.Load(a.AccountId,now));
            File.WriteAllText(path,new string('x',8193));Reject("cache-size-bounded",()=>store.Load(a.AccountId,now));
            File.WriteAllText(path,"{\"version\":1,\"account\":\""+a.AccountId+"\",\"reading\":null}");Reject("cache-null-rejected",()=>store.Load(a.AccountId,now));
        }
        finally{Directory.Delete(dir,true);}
        var status=FleetState.Empty with{Cost=all};var sw=status.Wire(now,false);var aw=y.Wire(1,2,"abcd1234",now);
        C("status-wire-budget",Encoding.UTF8.GetByteCount(sw)<3000);C("account-wire-budget",Encoding.UTF8.GetByteCount(aw)<3000);
        C("wire-total-and-fixed",J(sw).GetProperty("cost").GetProperty("value").GetDouble()==5.45&&J(sw).GetProperty("cost").GetProperty("secondary").GetDouble()==5);
        C("wire-no-period-warning",!sw.Contains("PERIOD UNKNOWN")&&!aw.Contains("SOURCE STALE"));
        var fixtureDir=Path.Combine(AppContext.BaseDirectory,"m46-fixtures");Directory.CreateDirectory(fixtureDir);
        File.WriteAllText(Path.Combine(fixtureDir,"status-synthetic.json"),sw);File.WriteAllText(Path.Combine(fixtureDir,"cloud-synthetic.json"),aw);
    }
    private sealed class SubscriptionHandler(string json):HttpMessageHandler
    {
        public Uri? Uri;public HttpMethod? Method;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Uri=request.RequestUri;Method=request.Method;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});}
    }
}
