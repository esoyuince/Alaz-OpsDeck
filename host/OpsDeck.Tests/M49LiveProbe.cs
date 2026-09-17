using System.Text.Json;
using System.Text.RegularExpressions;
using OpsDeck.Core;
namespace OpsDeck.Tests;
internal static class M49LiveProbe
{
    public static async Task<int> Run()
    {
        var local=new LocalSettings();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(150));var report=new List<object>();var traces=new List<object>();int faults=0;
        foreach(var p in local.Load().EffectiveAccounts().Where(p=>p.Enabled))
        {
            try
            {
                string? token=local.ReadTokenForAccount(p.AccountId);if(string.IsNullOrEmpty(token))throw new SourceFailure(SourceState.Denied,"Token unavailable");
                var trace=new ReadOnlyTrace();using var cf=new CloudflareClient(p.AccountId,token,trace);var list=await cf.QueuesList(stop.Token);var histories=new List<QueueHistory>();
                string[] preferred=p.ProfileId=="vetakeep"?["vetakeep-app-notification-push","vetakeep-owner-reminder-delivery"]:["astraldeck-ai-prod","astraldeck-ai-dead-letter-staging"];
                foreach(var q in list.Items.Where(q=>preferred.Contains(q.Name,StringComparer.Ordinal)).Take(2)){var h=await cf.QueueHistory24(q,stop.Token);histories.Add(h);if(h.State is SourceState.Error or SourceState.Denied)faults++;}
                var ai=await cf.AiHistory24(stop.Token);if(ai.State is SourceState.Error or SourceState.Denied)faults++;
                report.Add(new{profile=p.Name,queues=histories,workers_ai=ai});traces.Add(trace.Summary());
            }
            catch(Exception e)when(e is SourceFailure or HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
            {faults++;report.Add(new{profile=p.Name,state=e is SourceFailure f?f.State.ToString():"Error",error=e is SourceFailure s?s.Message:e.GetType().Name});}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{scope="READ-ONLY M4.9 ANALYTICS; NO MESSAGES, SQL, PROMPTS OR INFERENCE",time=DateTimeOffset.UtcNow,faults,traces,report},new JsonSerializerOptions(Json.Options){WriteIndented=true}));return faults==0?0:1;
    }

    private sealed class ReadOnlyTrace():DelegatingHandler(new HttpClientHandler{AllowAutoRedirect=false})
    {
        private int calls,get,post,unsafeCalls;private readonly Dictionary<string,int> datasets=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            string path=request.RequestUri?.PathAndQuery??"",body=request.Content==null?"":await request.Content.ReadAsStringAsync(ct);
            bool fixedHost=request.RequestUri is {Scheme:"https",Host:"api.cloudflare.com"};
            bool safeGet=request.Method==HttpMethod.Get&&Regex.IsMatch(path,@"\A/client/v4/accounts/[a-fA-F0-9]{32}/queues\?page=(?:[1-9]|10)&per_page=100\z");
            string? dataset=new[]{"queueBacklogAdaptiveGroups","queueConsumerMetricsAdaptiveGroups","queueMessageOperationsAdaptiveGroups","aiInferenceAdaptiveGroups","aiGatewayRequestsAdaptiveGroups"}.SingleOrDefault(body.Contains);
            bool safePost=request.Method==HttpMethod.Post&&path=="/client/v4/graphql"&&dataset!=null&&body.Contains("query",StringComparison.Ordinal)&&!body.Contains("mutation",StringComparison.OrdinalIgnoreCase)&&!body.Contains("messages/pull",StringComparison.OrdinalIgnoreCase)&&!body.Contains("/ai/run",StringComparison.OrdinalIgnoreCase);
            calls++;if(request.Method==HttpMethod.Get)get++;if(request.Method==HttpMethod.Post)post++;if(dataset!=null)datasets[dataset]=datasets.GetValueOrDefault(dataset)+1;
            if(!fixedHost||!(safeGet||safePost)){unsafeCalls++;throw new InvalidOperationException("Live smoke attempted a non-read-only Cloudflare request.");}
            return await base.SendAsync(request,ct);
        }
        public object Summary()=>new{calls,get,post,unsafe_calls=unsafeCalls,datasets};
    }
}
