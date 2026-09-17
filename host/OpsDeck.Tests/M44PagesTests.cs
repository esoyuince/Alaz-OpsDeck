using System.Net;
using System.Text;
using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;
internal static class M44PagesTests
{
    public static async Task Run(Action<string,bool> check)
    {
        var profile=new CloudAccountConfig {ProfileId="fixture",Name="OFFLINE TEST",AccountId=new string('1',32),Enabled=true};
        async Task<(ResourceSet,PagesHandler)> Read(int count,bool metadata=true,bool repeat=false,ResourceKind kind=ResourceKind.Pages)
        {
            var handler=new PagesHandler(count,metadata,repeat,kind);
            using var client=new InventoryClient(profile,"offline-test-placeholder",handler:handler);
            return (await client.Read(kind,CancellationToken.None),handler);
        }
        var (rows,pages)=await Read(21);
        check("m44-pages-request-size-10",pages.Uris.All(u=>u.Query.Contains("per_page=10")));
        check("m44-pages-all-21-across-3-pages",rows.Complete&&rows.Items.Length==21&&pages.Uris.Count==3);
        check("m44-pages-ordered-page-requests",pages.Uris.Select(u=>u.Query).SequenceEqual(new[]{"?page=1&per_page=10","?page=2&per_page=10","?page=3&per_page=10"}));
        check("m44-pages-get-only",pages.Methods.All(m=>m==HttpMethod.Get));
        var (drained,drain)=await Read(11,false);
        check("m44-pages-no-metadata-drain",drained.Complete&&drained.Items.Length==11&&drain.Uris.Count==3);
        var (empty,_)=await Read(0);
        check("m44-pages-confirmed-empty",empty.Complete&&empty.State==SourceState.Ok&&empty.Items.Length==0);
        var (repeated,_r)=await Read(21,false,true);
        check("m44-pages-repeated-page-not-complete",!repeated.Complete&&repeated.State==SourceState.Partial&&repeated.Items.Length==10);
        var (d1,d1Handler)=await Read(2,true,false,ResourceKind.D1);
        check("m44-d1-page-size-unchanged",d1.Complete&&d1.Items.Length==2&&d1Handler.Uris.All(u=>u.Query.Contains("per_page=50")));
    }
    private sealed class PagesHandler(int total,bool metadata,bool repeat,ResourceKind kind):HttpMessageHandler
    {
        public readonly List<Uri> Uris=[];
        public readonly List<HttpMethod> Methods=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();var uri=request.RequestUri!;Uris.Add(uri);Methods.Add(request.Method);
            int expected=kind==ResourceKind.Pages?10:50;
            if(!uri.Query.Contains("per_page="+expected))return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest){Content=new StringContent("{}")});
            int page=int.Parse(uri.Query.Split('&')[0].Split('=')[1]);
            int first=repeat?0:(page-1)*expected;
            var rows=Enumerable.Range(first,Math.Max(0,Math.Min(expected,total-first))).Select(i=>kind==ResourceKind.Pages
                ? (object)new {id="project-"+i,name="Project "+i}
                : new {uuid="database-"+i,name="Database "+i}).ToArray();
            var envelope=new Dictionary<string,object>{{"success",true},{"errors",Array.Empty<object>()},{"result",rows}};
            if(metadata)envelope["result_info"]=new {page,per_page=expected,total_count=total};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(envelope),Encoding.UTF8,"application/json")});
        }
    }
}
