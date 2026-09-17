using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpsDeck.Core;
namespace OpsDeck.Tests;
// Explicit diagnostic option only. Never used by the default offline test suite.
// Uses this app's own saved token in memory; outputs only status, sanitized errors and row counts.
internal static class M43CloudProbe
{
    public static async Task<int> Run()
    {
        var local=new LocalSettings();
        var config=JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(local.ConfigPath),Json.Options)??throw new InvalidDataException("Configuration missing");
        config.Validate();var results=new List<object>();
        foreach(var profile in config.EffectiveAccounts().Where(p=>p.Enabled&&p.AccountId.Length==32))
        {
            string? token=local.ReadTokenForAccount(profile.AccountId);if(token==null)continue;
            using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false})
                {Timeout=TimeSpan.FromSeconds(12),MaxResponseContentBufferSize=2*1024*1024};
            http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
            foreach(int perPage in new[]{50,10})
            {
                using var request=new HttpRequestMessage(HttpMethod.Get,$"https://api.cloudflare.com/client/v4/accounts/{profile.AccountId}/pages/projects?page=1&per_page={perPage}");
                using var response=await http.SendAsync(request);
                using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root=document.RootElement;var errors=new List<object>();
                if(root.TryGetProperty("errors",out var errorList)&&errorList.ValueKind==JsonValueKind.Array)
                    foreach(var error in errorList.EnumerateArray())
                    {
                        string message=error.TryGetProperty("message",out var m)?m.GetString()??"":"";
                        message=Regex.Replace(message.Replace(token,"<secret>").Replace(profile.AccountId,"<account>"),@"[A-Za-z0-9_-]{32,}","<redacted>");
                        errors.Add(new{code=error.TryGetProperty("code",out var c)?c.ToString():"",message=message[..Math.Min(message.Length,240)]});
                    }
                int? rowCount=root.TryGetProperty("result",out var rows)&&rows.ValueKind==JsonValueKind.Array?rows.GetArrayLength():null;
                results.Add(new{profile=profile.Name,method="GET",endpoint="pages/projects",page=1,per_page=perPage,http_status=(int)response.StatusCode,row_count=rowCount,errors});
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new{environment="REAL CLOUDFLARE READ-ONLY DIAGNOSTIC; NOT FIXTURE",observed_at=DateTimeOffset.UtcNow,results},Json.Options));
        return 0;
    }
}
