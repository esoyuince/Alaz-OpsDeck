using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;
internal static class M48QueueLiveProbe
{
    public static async Task<int> Run()
    {
        var local=new LocalSettings();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(120));var report=new List<object>();int faults=0;
        foreach(var p in local.Load().EffectiveAccounts().Where(p=>p.Enabled))
        {
            try
            {
                string? token=local.ReadTokenForAccount(p.AccountId);if(string.IsNullOrEmpty(token))throw new SourceFailure(SourceState.Denied,"Token unavailable");
                using var cf=new CloudflareClient(p.AccountId,token);var list=await cf.QueuesList(stop.Token);var readings=new List<object>();
                if(!list.Complete)faults++;
                foreach(var q in list.Items.Take(3))
                {
                    try{var m=await cf.QueueMetrics(q,stop.Token);readings.Add(new{name=q.Name,state=m.State.ToString(),m.Messages,m.Bytes,m.OldestAt,m.CollectedAt,m.Detail});}
                    catch(SourceFailure e){faults++;readings.Add(new{name=q.Name,state=e.State.ToString(),detail=e.Message});}
                }
                report.Add(new{profile=p.Name,state=list.State.ToString(),list.Complete,count=list.Items.Length,queues=list.Items.Select(q=>new{q.Name,q.DeliveryPaused,q.Consumers,q.RetentionSeconds,q.Jurisdiction}),readings});
            }
            catch(Exception e)when(e is SourceFailure or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {faults++;report.Add(new{profile=p.Name,state=e is SourceFailure f?f.State.ToString():"Error",error=e is SourceFailure s?s.Message:e.GetType().Name});}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{environment="READ-ONLY M4.8 QUEUE LIST/BACKLOG; NO MESSAGES ACCESSED",time=DateTimeOffset.UtcNow,requests_per_account="max 10 list pages + 3 metrics",faults,report},new JsonSerializerOptions(Json.Options){WriteIndented=true}));return faults==0?0:1;
    }
}
