using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;
// Explicit, read-only acceptance diagnostic. No serial port, no configuration writes,
// no raw API responses, account IDs or secrets in output.
internal static class M44Diagnostics
{
    public static async Task<int> Run()
    {
        var settings=new LocalSettings();
        var config=JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(settings.ConfigPath),Json.Options)??throw new InvalidDataException("Missing settings");
        config.Validate();var accounts=config.EffectiveAccounts();var report=new List<object>();
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(100));
        foreach(var profile in accounts.Where(a=>a.Enabled))
        {
            string? token=settings.ReadTokenForAccount(profile.AccountId);
            if(token==null){report.Add(new {profile=profile.Name,error="Saved token unavailable"});continue;}
            using var client=new InventoryClient(profile,token);
            var pages=await client.Read(ResourceKind.Pages,stop.Token);
            report.Add(new {profile=profile.Name,source="Pages",pages.Complete,state=pages.State.ToString(),count=pages.Items.Length,pages.Detail});
            if(profile.BillingEnabled)
            {
                using var cloud=new CloudflareClient(profile.AccountId,token);
                try{var cost=await cloud.Cost(stop.Token);report.Add(new {profile=profile.Name,source="Usage charges",metric=cost});}
                catch(SourceFailure e){report.Add(new {profile=profile.Name,source="Usage charges",state=e.State.ToString(),detail=e.Message});}
            }
        }
        using var health=new HealthChecks();
        foreach(var url in config.Sites.Concat(accounts.SelectMany(a=>a.Sites)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result=await health.Check(url,stop.Token);
            report.Add(new {source="HTTPS",url,general=config.Sites.Contains(url,StringComparer.OrdinalIgnoreCase),result});
        }
        Console.WriteLine(JsonSerializer.Serialize(new {environment="REAL READ-ONLY ACCEPTANCE",time=DateTimeOffset.UtcNow,report},new JsonSerializerOptions(Json.Options){WriteIndented=true}));
        return 0;
    }
}
