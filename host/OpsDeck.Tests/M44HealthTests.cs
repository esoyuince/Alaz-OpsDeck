using System.Net;
using OpsDeck.Core;
namespace OpsDeck.Tests;
internal static class M44HealthTests
{
 public static async Task Run(Action<string,bool> check)
 {
  async Task<(SiteResult,Handler)> Read(Func<int,Uri,(int,string?)> response){var h=new Handler(response);using var c=new HealthChecks(h);return(await c.Check("https://example.com/",CancellationToken.None),h);}
  var (direct,_)=await Read((n,u)=>(200,null));check("m44-health-direct",direct.Healthy&&direct.Redirects==0&&direct.FirstHttpCode==200);
  var (redirect,h)=await Read((n,u)=>n==1?(301,"https://target.example/"):(200,null));
  check("m44-health-301-followed",redirect.Healthy&&redirect.HttpCode==200&&redirect.FirstHttpCode==301&&redirect.Redirects==1);
  check("m44-health-original-identity-kept",redirect.Host=="example.com"&&redirect.FinalHost=="target.example"&&redirect.Url=="https://example.com/");
  check("m44-health-redirect-no-auth-no-post",h.Requests.All(x=>x.Method==HttpMethod.Get&&!x.HasAuth));
  var (relative,rel)=await Read((n,u)=>n==1?(308,"/landing"):(204,null));check("m44-health-relative-redirect",relative.Healthy&&rel.Requests.Last().Uri.AbsolutePath=="/landing");
  var (missing,_m)=await Read((n,u)=>(302,null));check("m44-health-no-location-not-up",!missing.Healthy&&missing.HttpCode==302);
  foreach(var unsafeUrl in new[]{"http://target.example/","https://127.0.0.1/","https://10.0.0.1/","https://169.254.169.254/","https://[::1]/","https://[fd00::1]/","https://u:p@target.example/","https://target.example:8443/","https://target.example/?key=x","https://localhost/"}){
   var (bad,b)=await Read((n,u)=>(301,unsafeUrl));check("m44-health-unsafe-redirect-"+unsafeUrl,!bad.Healthy&&b.Requests.Count==1);
  }
  var (loop,l)=await Read((n,u)=>(301,"https://example.com/"));check("m44-health-redirect-loop-bounded",!loop.Healthy&&l.Requests.Count==1&&loop.State.Contains("loop"));
  var (max,m)=await Read((n,u)=>(301,"/page"+n));check("m44-health-hop-budget",!max.Healthy&&m.Requests.Count==HealthChecks.MaxRedirects+1);
  var (fail,f)=await Read((n,u)=>n==1?(301,"/target"):(503,null));check("m44-health-target-failure-not-up",!fail.Healthy&&fail.HttpCode==503&&f.Requests.Count==2);
  var (notredirect,_n)=await Read((n,u)=>(304,"/target"));check("m44-health-304-not-followed",!notredirect.Healthy&&notredirect.Redirects==0);
  using var cts=new CancellationTokenSource();cts.Cancel();using var hc=new HealthChecks(new Handler((n,u)=>(200,null)));
  try{await hc.Check("https://example.com/",cts.Token);check("m44-health-caller-cancellation",false);}catch(OperationCanceledException){check("m44-health-caller-cancellation",true);}
 }
 private sealed class Handler(Func<int,Uri,(int Code,string? Location)> response):HttpMessageHandler
 {
  public readonly List<(Uri Uri,HttpMethod Method,bool HasAuth)> Requests=[];
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
   ct.ThrowIfCancellationRequested();Requests.Add((request.RequestUri!,request.Method,request.Headers.Authorization!=null));
   var x=response(Requests.Count,request.RequestUri!);var r=new HttpResponseMessage((HttpStatusCode)x.Code);
   if(x.Location!=null)r.Headers.Location=new Uri(x.Location,UriKind.RelativeOrAbsolute);return Task.FromResult(r);
  }
 }
}
