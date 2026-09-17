using System.Net.Http.Headers;
using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;
internal static class M44BillingProbe
{
 public static async Task<int> Run()
 {
  var local=new LocalSettings();var cfg=JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(local.ConfigPath),Json.Options)!;cfg.Validate();
  var report=new List<object>();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(80));
  foreach(var p in cfg.EffectiveAccounts().Where(p=>p.Enabled&&p.BillingEnabled))
  {
   var token=local.ReadTokenForAccount(p.AccountId);if(token==null)continue;
   using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}){Timeout=TimeSpan.FromSeconds(12),MaxResponseContentBufferSize=2*1024*1024};
   http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
   foreach(var suffix in new[]{"/info","?from=2026-08-01&to=2026-09-14"})
   {
    using var res=await http.GetAsync($"https://api.cloudflare.com/client/v4/accounts/{p.AccountId}/billable-usage"+suffix,stop.Token);
    if(!res.IsSuccessStatusCode){report.Add(new{profile=p.Name,query=suffix,status=(int)res.StatusCode});continue;}
    using var doc=JsonDocument.Parse(await res.Content.ReadAsStringAsync(stop.Token));var root=doc.RootElement;
    if(!root.TryGetProperty("success",out var ok)||ok.ValueKind!=JsonValueKind.True||!root.TryGetProperty("result",out var result)){report.Add(new{profile=p.Name,error="Envelope not confirmed"});continue;}
    if(suffix=="/info"){
     var timestamps=new List<object>();if(result.TryGetProperty("subscriptions",out var subs)&&subs.ValueKind==JsonValueKind.Array)foreach(var sub in subs.EnumerateArray()){
      string? Text(string n)=>sub.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
      timestamps.Add(new {anchor=Text("billing_cycle_anchor_timestamp"),start=Text("start_timestamp"),end=Text("end_timestamp")});
     }
     report.Add(new{profile=p.Name,source="billing-info",covered=result.TryGetProperty("covered",out var c)&&c.ValueKind==JsonValueKind.True,subscriptions=timestamps});
    }else if(result.ValueKind==JsonValueKind.Array){
     var dates=new List<string>();var periods=new HashSet<string>();int september=0;decimal total=0;
     foreach(var row in result.EnumerateArray()){
      if(row.TryGetProperty("ChargePeriodEnd",out var e)&&e.ValueKind==JsonValueKind.String)dates.Add(e.GetString()!);
      if(row.TryGetProperty("BillingPeriodStart",out var b))periods.Add(b.ToString());
      if(row.TryGetProperty("ChargePeriodStart",out var s)&&DateTimeOffset.TryParse(s.GetString(),out var d)&&d>=new DateTimeOffset(2026,9,1,0,0,0,TimeSpan.Zero)){
       september++;if(row.TryGetProperty("ContractedCost",out var cost)&&cost.TryGetDecimal(out var amount))total+=amount;
      }
     }
     report.Add(new{profile=p.Name,source="explicit-date-query",query=suffix,rows=result.GetArrayLength(),periods,first_charge_end=dates.Order().FirstOrDefault(),last_charge_end=dates.Order().LastOrDefault(),september_rows=september,september_known_amount=total});
    }
   }
  }
  Console.WriteLine(JsonSerializer.Serialize(new{environment="READ-ONLY BILLING DIAGNOSTIC; NOT A VALIDATED TOTAL",report},new JsonSerializerOptions(Json.Options){WriteIndented=true}));return 0;
 }
}
