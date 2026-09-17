using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;
internal static class M46LiveProbe
{
 public static async Task<int> Run()
 {
  var local=new LocalSettings();var cfg=local.Load();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(90));
  var report=new List<object>();
  foreach(var profile in cfg.EffectiveAccounts().Where(x=>x.Enabled))
  {
   string? token=null;try{token=local.ReadTokenForAccount(profile.AccountId);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException){report.Add(new{profile=profile.Name,error="TOKEN_UNAVAILABLE",kind=e.GetType().Name});continue;}
   if(string.IsNullOrEmpty(token)){report.Add(new{profile=profile.Name,error="TOKEN_MISSING"});continue;}
   using var cf=new CloudflareClient(profile.AccountId,token);
   Metric? cost=null,subscription=null;
   try{cost=profile.BillingEnabled?await cf.Cost(stop.Token):Metric.Setup("Billing disabled");}catch(SourceFailure e){cost=new(e.State,Detail:e.Message);}
   try{subscription=profile.BillingEnabled?await cf.WorkersSubscription(stop.Token):Metric.Setup("Billing disabled");}catch(SourceFailure e){subscription=new(e.State,Detail:e.Message);}
   var account=AccountState.Empty(profile) with{Cost=cost!,Subscription=subscription};var shown=BillingDisplay.ForAccount(account,DateTimeOffset.UtcNow);
   report.Add(new{profile=profile.Name,fixed_monthly_usd=profile.FixedMonthlyUsd,cost=new{state=cost!.State,value=cost.Value,unit=cost.Unit,source_end=MetricPresentation.Date(cost.SourceEnd),period_start=MetricPresentation.Date(cost.PeriodStart),period_end=MetricPresentation.Date(cost.PeriodEnd)},subscription=new{state=subscription!.State,value=subscription.Value,unit=subscription.Unit,period_start=MetricPresentation.Date(subscription.PeriodStart),period_end=MetricPresentation.Date(subscription.PeriodEnd),note=subscription.Note,detail=subscription.Detail},display=new{state=shown.State,value=shown.Value,fixed_component=shown.Secondary,unit=shown.Unit,note=shown.Note,detail=shown.Detail}});
  }
  Console.WriteLine(JsonSerializer.Serialize(new{environment="READ-ONLY M4.6 LIVE BILLING/SUBSCRIPTION DIAGNOSTIC",report},new JsonSerializerOptions(Json.Options){WriteIndented=true}));return 0;
 }
}
