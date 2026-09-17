using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;
internal static class M46WorkerLiveProbe
{
 public static async Task<int> Run()
 {
  const int maxCandidates=8;var local=new LocalSettings();var cfg=local.Load();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(120));var report=new List<object>();
  foreach(var profile in cfg.EffectiveAccounts().Where(x=>x.Enabled))
  {
   string? token=null;try{token=local.ReadTokenForAccount(profile.AccountId);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException){report.Add(new{profile=profile.Name,error="TOKEN_UNAVAILABLE",kind=e.GetType().Name});continue;}
   if(string.IsNullOrEmpty(token)){report.Add(new{profile=profile.Name,error="TOKEN_MISSING"});continue;}
   using var inventory=new InventoryClient(profile,token);ResourceSet set;
   try{set=await inventory.Read(ResourceKind.Worker,stop.Token);}catch(SourceFailure e){report.Add(new{profile=profile.Name,inventory_state=e.State.ToString(),detail=e.Message});continue;}
   var candidates=set.Items.OrderBy(x=>x.Name,StringComparer.Ordinal).Take(maxCandidates).ToArray();var sampled=new List<object>();bool activeFound=false;
   using var cf=new CloudflareClient(profile.AccountId,token);
   foreach(var item in candidates)
   {
    try
    {
     var d=await cf.WorkerDetail(item.Key,stop.Token);sampled.Add(new{name=item.Name,state=d.State.ToString(),requests=d.Requests,errors=d.Errors,error_percent=d.ErrorPercent,cpu_p50_ms=d.CpuP50Ms,cpu_p99_ms=d.CpuP99Ms,wall_p50_ms=d.WallP50Ms,wall_p99_ms=d.WallP99Ms,detail=d.Detail});
     if(d.Requests>0){activeFound=true;break;}
    }
    catch(SourceFailure e){sampled.Add(new{name=item.Name,state=e.State.ToString(),detail=e.Message});}
   }
   report.Add(new{profile=profile.Name,inventory_state=set.State.ToString(),set.Complete,worker_count=set.Items.Length,candidates_checked=sampled.Count,active_found=activeFound,sampled});
  }
  Console.WriteLine(JsonSerializer.Serialize(new{environment="READ-ONLY M4.6 LIVE WORKER DETAIL DIAGNOSTIC",max_candidates_per_account=maxCandidates,report},new JsonSerializerOptions(Json.Options){WriteIndented=true}));return 0;
 }
}
