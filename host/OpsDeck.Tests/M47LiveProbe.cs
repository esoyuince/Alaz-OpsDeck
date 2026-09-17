using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;
internal static class M47LiveProbe
{
 public static async Task<int> Run()
 {
  const int max=3;var local=new LocalSettings();var cfg=local.Load();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(150));var report=new List<object>();
  foreach(var profile in cfg.EffectiveAccounts().Where(x=>x.Enabled))
  {
   string? token=null;try{token=local.ReadTokenForAccount(profile.AccountId);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException){report.Add(new{profile=profile.Name,error="TOKEN_UNAVAILABLE",kind=e.GetType().Name});continue;}
   if(string.IsNullOrEmpty(token)){report.Add(new{profile=profile.Name,error="TOKEN_MISSING"});continue;}
   using var inventory=new InventoryClient(profile,token);ResourceSet d1Set=await inventory.Read(ResourceKind.D1,stop.Token),r2Set=await inventory.Read(ResourceKind.R2,stop.Token);using var cf=new CloudflareClient(profile.AccountId,token);var d1=new List<object>();var r2=new List<object>();
   foreach(var item in d1Set.Items.OrderBy(x=>x.Name,StringComparer.Ordinal).Take(max))
   {
    try{var x=await cf.D1Detail(item.Key,stop.Token);d1.Add(new{name=item.Name,state=x.State.ToString(),read_queries=x.ReadQueries,write_queries=x.WriteQueries,rows_read=x.RowsRead,rows_written=x.RowsWritten,response_bytes=x.ResponseBytes,query_p90_ms=x.QueryP90Ms,database_size_bytes=x.DatabaseSizeBytes,tables=x.TableCount,jurisdiction=x.Jurisdiction,replication=x.ReplicationMode,detail=x.Detail});}
    catch(SourceFailure e){d1.Add(new{name=item.Name,state=e.State.ToString(),detail=e.Message});}
   }
   foreach(var item in r2Set.Items.OrderBy(x=>x.Name,StringComparer.Ordinal).Take(max))
   {
    try{var x=await cf.R2Detail(item.Key,stop.Token);r2.Add(new{name=item.Name,state=x.State.ToString(),requests=x.TotalRequests,success=x.SuccessRequests,user_errors=x.UserErrors,internal_errors=x.InternalErrors,payload_bytes=x.PayloadBytes,metadata_bytes=x.MetadataBytes,objects=x.ObjectCount,pending_uploads=x.UploadCount,storage_at=x.StorageAt,top=x.TopOperations,detail=x.Detail});}
    catch(SourceFailure e){r2.Add(new{name=item.Name,state=e.State.ToString(),detail=e.Message});}
   }
   report.Add(new{profile=profile.Name,d1_inventory=new{state=d1Set.State.ToString(),d1Set.Complete,count=d1Set.Items.Length,checked_count=d1.Count},r2_inventory=new{state=r2Set.State.ToString(),r2Set.Complete,count=r2Set.Items.Length,checked_count=r2.Count,scope="default jurisdiction only"},d1,r2});
  }
  Console.WriteLine(JsonSerializer.Serialize(new{environment="READ-ONLY M4.7 LIVE D1/R2 RESOURCE DIAGNOSTIC",max_per_kind_account=max,report},new JsonSerializerOptions(Json.Options){WriteIndented=true}));return 0;
 }
}
