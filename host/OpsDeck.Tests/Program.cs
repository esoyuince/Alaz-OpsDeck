using System.Net;
using System.Text;
using System.Text.Json;
using OpsDeck.Core;
if(args.FirstOrDefault(a=>a.StartsWith("--m49-ui-",StringComparison.Ordinal)) is string uiMode)return OpsDeck.Tests.M49UiAcceptance.Run(uiMode);
if(args.Contains("--hardware-lifecycle"))return await M41Hardware.Run();
int passed=0,failed=0;
void Check(string name,bool value){if(value){passed++;Console.WriteLine("PASS "+name);}else{failed++;Console.WriteLine("FAIL "+name);}}
void Throws(string name,Action a){try{a();Check(name,false);}catch(Exception e)when(e is ArgumentException or SourceFailure or InvalidOperationException){Check(name,true);}}
JsonElement Parse(string s)=>JsonDocument.Parse(s).RootElement.Clone();
if(args.Contains("--m410-details-only")){await OpsDeck.Tests.M410DetailsTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m411-bridge-tasks-only")){OpsDeck.Tests.M411BridgeTaskTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m412-recovery-only")){OpsDeck.Tests.M412RecoveryTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m413-agent-only")){OpsDeck.Tests.M413AgentTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m414-link-only")){OpsDeck.Tests.M414LinkHealthTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m415-details-v2-only")){OpsDeck.Tests.M415DetailsV2Tests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m416-projects-v2-only")){OpsDeck.Tests.M416ProjectsTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m50-ops-only")){OpsDeck.Tests.M50OperationalTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m51-assessment-only")){OpsDeck.Tests.M51AssessmentTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m52-correlation-only")){OpsDeck.Tests.M52CorrelationTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m54-lifecycle-only")){OpsDeck.Tests.M54LifecycleTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m55-alerts-only")){OpsDeck.Tests.M55AlertTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m56-trend-events-only")){OpsDeck.Tests.M56TrendEventTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m57-brief-only")){OpsDeck.Tests.M57BriefTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m58-deviation-only")){OpsDeck.Tests.M58DeviationTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m59-project-health-only")){OpsDeck.Tests.M59ProjectHealthTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m511-reliability-only")){OpsDeck.Tests.M511ReliabilityTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m511-wifi-only")){OpsDeck.Tests.M511WifiFoundationTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m512-overview-only")){OpsDeck.Tests.M512OverviewTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m61-agent-control-only")){await OpsDeck.Tests.M61AgentControlTests.Run(Check);
OpsDeck.Tests.M62CodexTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m62-codex-only")){OpsDeck.Tests.M62CodexTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m62-codex-live"))return await OpsDeck.Tests.M62CodexTests.Live();
if(args.Contains("--m66-agent-integration-only")){await OpsDeck.Tests.M66AgentIntegrationTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m66-codex-control-live"))return await OpsDeck.Tests.M66AgentIntegrationTests.LiveCodexControl();
if(args.Contains("--m66-rdc-live"))return await OpsDeck.Tests.M66AgentIntegrationTests.LiveRdc();
if(args.Contains("--m68-session-center-only")){await OpsDeck.Tests.M68SessionCenterTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m68-session-center-live"))return await OpsDeck.Tests.M68SessionCenterTests.LiveSessionCenter();
if(args.Contains("--m68-liveops-only")){await OpsDeck.Tests.M68LiveOpsTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m68-screen-only")){await OpsDeck.Tests.M68ScreenCloseoutTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m69-autostart-only")){OpsDeck.Tests.M69AutostartTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m613-edgenode-only")){await OpsDeck.Tests.M613EdgeNodeTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m613-edgenode-live"))return await OpsDeck.Tests.M613EdgeNodeTests.Live();
if(args.Contains("--m614-fan-only")){OpsDeck.Tests.M614FanControlTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m615-perf-only")){OpsDeck.Tests.M615ResponsivenessTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m616-cloud-only")){OpsDeck.Tests.M616CloudSummaryTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m617-project-details-only")){OpsDeck.Tests.M617ProjectDetailsTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m68-actions-live"))return await OpsDeck.Tests.M68SessionCenterTests.LiveActions();
if(args.Contains("--m67-deck-control-only")){await OpsDeck.Tests.M67DeckControlTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m53-history-only")){OpsDeck.Tests.M53HistoryTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m411-live-bridge-tasks"))return OpsDeck.Tests.M411BridgeTaskProbe.Run(args);
if(args.Contains("--inventory-only")){OpsDeck.Tests.M43Tests.Run(Check,Throws);OpsDeck.Tests.M43Tests.Export(Path.Combine(AppContext.BaseDirectory,"inventory-fixtures"));Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--pages-live-diagnostic"))return await OpsDeck.Tests.M43CloudProbe.Run();
if(args.Contains("--m44-pages-only")){await OpsDeck.Tests.M44PagesTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m44-live-diagnostic"))return await OpsDeck.Tests.M44Diagnostics.Run();
if(args.Contains("--m44-billing-probe"))return await OpsDeck.Tests.M44BillingProbe.Run();
if(args.Contains("--m46-live-billing"))return await OpsDeck.Tests.M46LiveProbe.Run();
if(args.Contains("--m46-live-worker"))return await OpsDeck.Tests.M46WorkerLiveProbe.Run();
if(args.Contains("--m47-live-resources"))return await OpsDeck.Tests.M47LiveProbe.Run();
if(args.Contains("--m48-live-queues"))return await OpsDeck.Tests.M48QueueLiveProbe.Run();
if(args.Contains("--m49-schema"))return await OpsDeck.Tests.M49SchemaProbe.Run(args);
if(args.Contains("--m49-live-history"))return await OpsDeck.Tests.M49LiveProbe.Run();
if(args.Contains("--m49-boundaries-only")){OpsDeck.Tests.M49BoundaryTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m49-http-only")){await OpsDeck.Tests.M49HistoryTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m44-health-only")){await OpsDeck.Tests.M44HealthTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
if(args.Contains("--m44-pc-probe"))return await OpsDeck.Tests.M44TelemetryTests.Probe();
if(args.Contains("--m44-telemetry-only")){OpsDeck.Tests.M44TelemetryTests.Run(Check);Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
const string account="0123456789abcdef0123456789abcdef";
new HostConfig().Validate();Check("default-config",true);
foreach(string p in new[]{"COM0","COM-1","COM9;erase","COM10000","../x"})Throws("invalid-port-"+p,()=>new HostConfig{Port=p}.Validate());
foreach(string url in new[]{"https://127.0.0.1/health","http://localhost:42/health","http://127.0.0.1:42/delete","http://127.0.0.1:42/health?token=x","http://u:p@127.0.0.1:42/health"})Throws("invalid-bridge-"+url,()=>HostConfig.ValidateBridge(url));
Check("valid-bridge",HostConfig.ValidateBridge("http://127.0.0.1:42/health").Port==42);
foreach(string url in new[]{"http://example.com/","https://example.com/?token=x","https://u:p@example.com/","https://example.com:444/","https://localhost/"})Throws("invalid-site-"+url,()=>HostConfig.ValidateSite(url));
Check("valid-site",HostConfig.ValidateSite("https://example.com/health").Scheme=="https");
foreach(string ip in new[]{"127.0.0.1","10.1.1.1","172.16.0.1","192.168.1.2","169.254.169.254","100.64.0.1","0.0.0.0","224.0.0.1","::1","fe80::1","fc00::1","::ffff:127.0.0.1","2001:db8::1"})Check("private-address-"+ip,!HealthChecks.IsPublicAddress(IPAddress.Parse(ip)));
Check("public-ipv4",HealthChecks.IsPublicAddress(IPAddress.Parse("1.1.1.1")));Check("public-ipv6",HealthChecks.IsPublicAddress(IPAddress.Parse("2606:4700:4700::1111")));
Check("bridge-health-identity",AgentSampler.IsBridgeHealth(Parse("{\"ok\":true,\"name\":\"chatgpt-codex-mcp-bridge\"}")));
Check("foreign-health-not-bridge",!AgentSampler.IsBridgeHealth(Parse("{\"ok\":true,\"name\":\"other\"}")));
Check("health-false",!AgentSampler.IsBridgeHealth(Parse("{\"ok\":false,\"name\":\"chatgpt-codex-mcp-bridge\"}")));
var now=DateTimeOffset.UtcNow;var old=new Metric(SourceState.Ok,42,CollectedAt:now.AddMinutes(-10));
var wire=JsonSerializer.Serialize(old.Wire(now,180));Check("old-metric-stale",Parse(wire).GetProperty("state").GetInt32()==3);
Check("zero-not-missing",Parse(new PcSample(Cpu:0).Wire()).GetProperty("cpu").GetDouble()==0);
Check("missing-not-zero",!Parse(new PcSample().Wire()).TryGetProperty("cpu",out _));
var fleet=FleetState.Empty with{Agents=new(3,SourceState.Ok,SourceState.Ok,now)};
Check("status-size-bounded",Encoding.UTF8.GetByteCount(fleet.Wire(now,false))<3000);
Check("locked-hides-agent-count",Parse(fleet.Wire(now,true)).GetProperty("agents").GetProperty("codex_count").GetInt32()==-1);
Check("agent-old-is-stale",Parse(fleet.Wire(now.AddMinutes(1),false)).GetProperty("agents").GetProperty("codex_state").GetInt32()==3);
string R2(double n)=>"{\"result\":{\"standard\":{\"published\":{\"payloadSize\":"+n.ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"objects\":2}}}}";
Check("r2-gib",CloudflareClient.ParseR2(Parse(R2(1073741824))).Value==1);
Check("empty-r2-no-data",CloudflareClient.ParseR2(Parse("{\"result\":{}}" )).State==SourceState.NoData);
Check("zero-r2-valid",CloudflareClient.ParseR2(Parse(R2(0))).Value==0);
Throws("negative-r2-rejected",()=>CloudflareClient.ParseR2(Parse(R2(-1))));
string Charge(string date,string currency="USD",string cost="0.75",string period="2026-09-01T00:00:00Z")=>"{\"BillingAccountId\":\""+account+"\",\"ChargeCategory\":\"Usage\",\"BillingCurrency\":\""+currency+"\",\"ServiceName\":\"Workers\",\"BillingPeriodStart\":\""+period+"\",\"ChargePeriodStart\":\""+date+"\",\"ChargePeriodEnd\":\""+date+"\",\"PricingUnit\":\"requests\",\"ContractedCost\":"+cost+",\"CumulatedContractedCost\":999}";
string Bill(params string[] rows)=>"{\"result\":["+string.Join(",",rows)+"]}";
var billing=CloudflareClient.ParseCost(Parse(Bill(Charge("2026-09-01T00:00:00Z"),Charge("2026-09-02T00:00:00Z"))),account);
Check("cost-sums-intervals-not-cumulative",billing.Value==1.5&&billing.Unit=="USD");
var mixedBilling=CloudflareClient.ParseCost(Parse(Bill(
    Charge("2026-08-12T00:00:00Z",cost:"9.00",period:"2026-08-11T00:00:00Z"),
    Charge("2026-09-12T00:00:00Z",cost:"0.25",period:"2026-09-11T00:00:00Z"),
    Charge("2026-09-13T00:00:00Z",cost:"0.35",period:"2026-09-11T00:00:00Z"),
    Charge("2026-10-12T00:00:00Z",cost:"99.00",period:"2026-10-11T00:00:00Z"))),account,new DateTimeOffset(2026,9,18,0,0,0,TimeSpan.Zero));
Check("cost-selects-latest-started-period",mixedBilling.Value==0.60&&mixedBilling.PeriodStart==new DateTimeOffset(2026,9,11,0,0,0,TimeSpan.Zero)&&mixedBilling.Note=="CURRENT PERIOD / USAGE");
Check("cost-excludes-old-and-future-periods",mixedBilling.Detail.Contains("other billing period(s) returned by API were excluded"));
Check("empty-billing-not-zero",CloudflareClient.ParseCost(Parse(Bill()),account).State==SourceState.NoData);
Throws("mixed-currency-rejected",()=>CloudflareClient.ParseCost(Parse(Bill(Charge("a"),Charge("b","EUR"))),account));
Throws("duplicate-billing-rejected",()=>CloudflareClient.ParseCost(Parse(Bill(Charge("a"),Charge("a"))),account));
Throws("wrong-account-rejected",()=>CloudflareClient.ParseCost(Parse(Bill(Charge("a"))),new string('0',32)));
Throws("negative-charge-rejected",()=>CloudflareClient.ParseCost(Parse(Bill(Charge("a","USD","-1"))),account));
foreach(int status in new[]{401,403,429,500}){
    var handler=new FakeHandler(status,"{}");using var client=new CloudflareClient(account,"fake-token-for-offline-test",handler);
    try{await client.R2(CancellationToken.None);Check("http-"+status,false);}catch(SourceFailure e){Check("http-"+status,e.State==(status is 401 or 403?SourceState.Denied:SourceState.Error));if(status==429)Check("retry-after-respected",e.RetrySeconds>=300);}
    Check("auth-fixed-cloudflare-host-"+status,handler.LastUri?.Host=="api.cloudflare.com"&&handler.LastMethod==HttpMethod.Get);
}
var wh=new FakeHandler(200,"{\"data\":{\"viewer\":{\"accounts\":[{\"workersInvocationsAdaptive\":[{\"sum\":{\"requests\":250,\"errors\":2}}]}]}}}");
using(var client=new CloudflareClient(account,"fake-token-for-offline-test",wh)){
    var m=await client.Workers(CancellationToken.None);Check("workers-rpm",m.Value==50&&m.Secondary==2);Check("workers-lag-window",m.SourceEnd<DateTimeOffset.UtcNow.AddMinutes(-2));Check("graphql-is-read-query",wh.Body.Contains("query OpsDeckWorkers")&&!wh.Body.Contains("mutation"));
}
var dh=new FakeHandler(200,"{\"data\":{\"viewer\":{\"accounts\":[{\"d1AnalyticsAdaptiveGroups\":[{\"sum\":{\"rowsRead\":120,\"rowsWritten\":4}}]}]}}}");
using(var client=new CloudflareClient(account,"fake-token-for-offline-test",dh)){var m=await client.D1(CancellationToken.None);Check("d1-row-not-query-count",m.Value==120&&m.Secondary==4);}
using(var health=new HealthChecks(new FakeHandler(302,""))){var result=await health.Check("https://example.com/",CancellationToken.None);Check("redirect-not-up",!result.Healthy&&result.HttpCode==302);}
string temp=Path.Combine(Path.GetTempPath(),"opsdeck-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
try {
    var settings=new LocalSettings(temp);settings.Save(new());Check("config-roundtrip",settings.Load().Port=="COM9");
    settings.SaveToken("only-a-test-value-123456");Check("dpapi-roundtrip",settings.ReadToken()=="only-a-test-value-123456");Check("secret-not-in-settings",!File.ReadAllText(settings.ConfigPath).Contains("only-a-test-value"));settings.DeleteToken();Check("token-revoked",!settings.HasToken);
    using(var db=new HistoryStore(Path.Combine(temp,"test.db"))){db.Add(now.AddDays(-9),[new(Cpu:10)]);db.Add(now,[new(Cpu:20),new(Cpu:40)]);Check("history-retention",db.Count()==1);db.Add(now,[new(Cpu:50)]);Check("history-idempotency",db.Count()==1);}
    using var sampler=new PcSampler(new());sampler.Sample();await Task.Delay(1100);var pc=sampler.Sample();Check("real-cpu-range",pc.Cpu is >=0 and <=100);Check("real-ram",pc.RamTotalGib>0&&pc.RamUsedGib<=pc.RamTotalGib);
}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(temp,true);}
await M3Tests.Run(Check,Throws);
await M4Tests.Run(Check,Throws);
await M41Tests.Run(Check,Throws);
OpsDeck.Tests.M42Tests.Run(Check,Throws);
OpsDeck.Tests.M43Tests.Run(Check,Throws);
await OpsDeck.Tests.M44PagesTests.Run(Check);
await OpsDeck.Tests.M44HealthTests.Run(Check);
OpsDeck.Tests.M44TelemetryTests.Run(Check);
await OpsDeck.Tests.M46Tests.Run(Check);
await OpsDeck.Tests.M46WorkerTests.Run(Check);
await OpsDeck.Tests.M47ResourceTests.Run(Check);
await OpsDeck.Tests.M48QueueTests.Run(Check);
OpsDeck.Tests.M49BoundaryTests.Run(Check);
await OpsDeck.Tests.M49HistoryTests.Run(Check);
await OpsDeck.Tests.M410DetailsTests.Run(Check);
OpsDeck.Tests.M411BridgeTaskTests.Run(Check);
OpsDeck.Tests.M412RecoveryTests.Run(Check);
OpsDeck.Tests.M413AgentTests.Run(Check);
OpsDeck.Tests.M414LinkHealthTests.Run(Check);
OpsDeck.Tests.M415DetailsV2Tests.Run(Check);
OpsDeck.Tests.M416ProjectsTests.Run(Check);
OpsDeck.Tests.M50OperationalTests.Run(Check);
OpsDeck.Tests.M51AssessmentTests.Run(Check);
OpsDeck.Tests.M52CorrelationTests.Run(Check);
OpsDeck.Tests.M53HistoryTests.Run(Check);
OpsDeck.Tests.M54LifecycleTests.Run(Check);
OpsDeck.Tests.M55AlertTests.Run(Check);
OpsDeck.Tests.M56TrendEventTests.Run(Check);
OpsDeck.Tests.M57BriefTests.Run(Check);
OpsDeck.Tests.M58DeviationTests.Run(Check);
OpsDeck.Tests.M59ProjectHealthTests.Run(Check);
OpsDeck.Tests.M511ReliabilityTests.Run(Check);
OpsDeck.Tests.M511WifiFoundationTests.Run(Check);
OpsDeck.Tests.M512OverviewTests.Run(Check);
await OpsDeck.Tests.M61AgentControlTests.Run(Check);
OpsDeck.Tests.M62CodexTests.Run(Check);
await OpsDeck.Tests.M66AgentIntegrationTests.Run(Check);
await OpsDeck.Tests.M67DeckControlTests.Run(Check);
await OpsDeck.Tests.M68SessionCenterTests.Run(Check);
await OpsDeck.Tests.M68ScreenCloseoutTests.Run(Check);
OpsDeck.Tests.M69AutostartTests.Run(Check);
await OpsDeck.Tests.M613EdgeNodeTests.Run(Check);
OpsDeck.Tests.M614FanControlTests.Run(Check);
OpsDeck.Tests.M615ResponsivenessTests.Run(Check);
OpsDeck.Tests.M616CloudSummaryTests.Run(Check);
OpsDeck.Tests.M617ProjectDetailsTests.Run(Check);
Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;
sealed class FakeHandler(int code,string response):HttpMessageHandler
{
    public Uri? LastUri;public HttpMethod? LastMethod;public string Body="";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){LastUri=request.RequestUri;LastMethod=request.Method;Body=request.Content!=null?await request.Content.ReadAsStringAsync(ct):"";var r=new HttpResponseMessage((HttpStatusCode)code){Content=new StringContent(response)};if(code==429)r.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(300));return r;}
}
