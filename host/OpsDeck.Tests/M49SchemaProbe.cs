using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpsDeck.Core;
namespace OpsDeck.Tests;
internal static class M49SchemaProbe
{
    public static async Task<int> Run(string[] args)
    {
        int pos=Array.IndexOf(args,"--schema-type");string? name=pos>=0&&pos+1<args.Length?args[pos+1]:null;
        if(name!=null&&!Regex.IsMatch(name,@"\A[_A-Za-z][_A-Za-z0-9]{0,200}\z"))return 2;
        var local=new LocalSettings();var profile=local.Load().EffectiveAccounts().First(p=>p.Enabled);
        string? token=local.ReadTokenForAccount(profile.AccountId);if(string.IsNullOrEmpty(token))return 2;
        using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}){Timeout=TimeSpan.FromSeconds(15),MaxResponseContentBufferSize=2*1024*1024};
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        string query=name==null?"query OpsDeckM49Types { __schema { types { name kind } } }":
            "query OpsDeckM49Fields { __type(name: "+JsonSerializer.Serialize(name)+") { name fields { name description type { name kind ofType { name kind ofType { name kind } } } args { name type { name kind ofType { name kind } } } } inputFields { name type {name kind ofType {name kind}} } } }";
        using var response=await http.PostAsync("https://api.cloudflare.com/client/v4/graphql",new StringContent(JsonSerializer.Serialize(new{query}),Encoding.UTF8,"application/json"));
        if(!response.IsSuccessStatusCode){Console.WriteLine("SCHEMA_HTTP_"+(int)response.StatusCode);return 1;}
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if(doc.RootElement.TryGetProperty("errors",out var errors)&&errors.ValueKind==JsonValueKind.Array&&errors.GetArrayLength()>0){Console.WriteLine("SCHEMA_API_ERROR");return 1;}
        var data=doc.RootElement.GetProperty("data");object output=name==null?
            data.GetProperty("__schema").GetProperty("types").EnumerateArray().Where(t=>Regex.IsMatch(t.GetProperty("name").GetString()??"","queue|inference|workersai|aigateway|account$",RegexOptions.IgnoreCase)).Select(t=>t.Clone()).ToArray():data.GetProperty("__type").Clone();
        Console.WriteLine(JsonSerializer.Serialize(new{scope="SCHEMA ONLY; NO INFERENCE OR PRIVATE CONTENT",schema=output},new JsonSerializerOptions{WriteIndented=true}));return 0;
    }
}
