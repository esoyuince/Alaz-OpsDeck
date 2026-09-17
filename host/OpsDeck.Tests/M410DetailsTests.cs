using OpsDeck.Core;
using System.Text;
using System.Text.Json;
using System.Reflection;
namespace OpsDeck.Tests;
public static class M410DetailsTests
{
    public static async Task Run(Action<string,bool> check)
    {
        const string A="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",B="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var now=DateTimeOffset.Parse("2026-09-14T18:00:00Z");
        void C(string name,bool ok)=>check("m410-"+name,ok);
        void Reject(string name,Action action){try{action();C(name,false);}catch(Exception e)when(e is ArgumentException or InvalidOperationException or SourceFailure){C(name,true);}}
        var q=new PanelDetailsRequest(RequestId:2);
        var card=new PanelDetailCard("0123456789abcdef","Fixture resource","09-14 12:00 to 09-14 13:00 UTC","Four metrics; missing is not zero",SourceState.Partial,now.AddSeconds(-12),
            [new("Real zero","count",0),new("Missing","bytes",null),new("Fraction","ms",42.5),new("Large","count",1e16)]);
        var p=PanelDetails.Build(q,"VetaKeep",[card],now);
        string wire=p.Wire("00000001",true);
        using(var d=JsonDocument.Parse(wire)){
            var x=d.RootElement;C("wire-type",x.GetProperty("type").GetString()=="opsdeck.details.v1");C("test-marked",x.GetProperty("test").GetBoolean());
            C("explicit-null",x.GetProperty("rows")[1].GetProperty("value").ValueKind==JsonValueKind.Null);
            C("real-zero",x.GetProperty("rows")[0].GetProperty("value").GetDouble()==0);
            C("exact-age",x.GetProperty("age_s").GetInt32()==12);
        }
        C("bounded-wire",Encoding.UTF8.GetByteCount(wire)<=3000);
        C("request-accepted",PanelDetailsRequest.TryParse("I (123) opsdeck.ui: DETAILS_REQUEST slot=1 kind=5 page=1023 request=2147483647",out var parsed)&&parsed==new PanelDetailsRequest(1,5,1023,int.MaxValue));
        foreach(var line in new[]{"slot=2 kind=0 page=0 request=1","slot=0 kind=6 page=0 request=1","slot=0 kind=0 page=1024 request=1","slot=0 kind=0 page=0 request=0","slot=0 kind=0 page=0 request=2147483648","slot=0 kind=0 page=-1 request=1","slot=0 kind=0 page=0 request=1\n","slot=0 kind=0 page=0 request=1 extra"})
            C("invalid-request-"+line.Trim(),!PanelDetailsRequest.TryParse("opsdeck.ui: DETAILS_REQUEST "+line,out _));
        C("long-request-rejected",!PanelDetailsRequest.TryParse(new string(' ',330)+"opsdeck.ui: DETAILS_REQUEST slot=0 kind=0 page=0 request=1",out _));
        C("wrong-command-rejected",!PanelDetailsRequest.TryParse("opsdeck.ui: INVENTORY_REQUEST slot=0 kind=0 page=0 request=1",out _));
        foreach(var bad in new[]{new PanelDetailsRequest(-1),new PanelDetailsRequest(2),new PanelDetailsRequest(Kind:6),new PanelDetailsRequest(Page:1024),new PanelDetailsRequest(RequestId:0)})Reject("invalid-query-"+bad,()=>bad.Validate());
        C("expired-source-stale",PanelDetails.Build(q,"A",[card with{State=SourceState.Ok,CollectedAt=now.AddSeconds(-181)}],now).State==SourceState.Stale);
        C("denial-not-masked-by-age",PanelDetails.Build(q,"A",[card with{State=SourceState.Denied,CollectedAt=now.AddHours(-1)}],now).State==SourceState.Denied);
        Reject("future-source",()=>PanelDetails.Build(q,"A",[card with{CollectedAt=now.AddMinutes(1)}],now));
        Reject("unknown-source-date",()=>PanelDetails.Build(q,"A",[card with{CollectedAt=default}],now));
        Reject("reversed-window",()=>PanelDetails.Window(now,now.AddSeconds(-1)));
        Reject("duplicate-card",()=>PanelDetails.Build(q,"A",[card,card],now));
        Reject("card-budget",()=>PanelDetails.Build(q,"A",Enumerable.Range(0,1025).Select(i=>card with{Key=i.ToString("x16")}),now));
        var c2=card with{Key="ffffffffffffffff"};C("page-clamp",PanelDetails.Build(q with{Page=1023},"A",[c2,card],now).Page==1);
        C("stable-order",PanelDetails.Build(q,"A",[c2,card],now).Key==card.Key);
        var noDataFirst=card with{Key="0000000000000001",State=SourceState.NoData};var okLater=card with{Key="fffffffffffffffe",State=SourceState.Ok};
        C("state-priority-order",PanelDetails.Build(q,"A",[noDataFirst,okLater],now).Key==okLater.Key);
        var empty=PanelDetails.Build(q,"A",[],now);C("empty-not-zero",empty.TotalPages==0&&empty.Rows.Length==0&&empty.AgeS==-1&&empty.State==SourceState.NoData);
        C("empty-message",empty.Note.Contains("never refreshes")&&empty.Reason.Contains("no validated card"));
        foreach(var n in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,-1d,1e17})Reject("invalid-number-"+n,()=> (p with{Rows=[new("Value","count",n)]}).Wire("00000001"));
        foreach(var text in new[]{"", " ","bad\nlabel",new string('a',29),"ş"})Reject("invalid-label-"+text,()=> (p with{Rows=[new(text,"count",1)]}).Wire("00000001"));
        Reject("wrong-generation",()=>p.Wire("zzzzzzzz"));Reject("wrong-key",()=> (p with{Key="wrong"}).Wire("00000001"));
        Reject("five-metrics",()=> (p with{Rows=Enumerable.Range(0,5).Select(i=>new PanelDetailMetric("m"+i,"n",i)).ToArray()}).Wire("00000001"));
        Reject("duplicate-metric",()=> (p with{Rows=[new("x","n",1),new("x","n",2)]}).Wire("00000001"));
        Reject("false-empty-ok",()=> (empty with{State=SourceState.Ok}).Wire("00000001"));
        Reject("missing-page-age",()=> (p with{AgeS=-1}).Wire("00000001"));
        Reject("false-fresh-ok",()=> (p with{AgeS=181,State=SourceState.Ok}).Wire("00000001"));
        C("ascii-transliteration",PanelDetails.Build(q,"Diğer Projeler",[card with{Title="Ölçüm şİığ"}],now).Title=="Olcum sIig");
        var wkey=new ResourceKey(AccountId:A,Kind:ResourceKind.Worker,Scope:"account",Id:"worker-a");
        var wkeyB=wkey with{AccountId=B};var dkey=new ResourceKey(AccountId:A,Kind:ResourceKind.D1,Scope:"account",Id:"12345678-1234-1234-1234-123456789abc");
        var rkey=new ResourceKey(AccountId:A,Kind:ResourceKind.R2,Scope:"default",Id:"bucket-a");
        var queue=new QueueInfo(A,new string('c',32),"Queue A");var window=AnalyticsWindow.Last24Hours(now);
        var worker=new WorkerAnalytics(wkey,SourceState.Ok,now,now.AddMinutes(-8),now.AddMinutes(-3),10,1,2,3,4,5,Detail:"SECRET_TOKEN_NEVER_DISPLAY");
        var d1=new D1Analytics(dkey,SourceState.NoData,now,now.AddHours(-24),now,DatabaseSizeBytes:100,TableCount:2);
        var r2=new R2Analytics(rkey,SourceState.Partial,now,now.AddHours(-24),now,PayloadBytes:123,ObjectCount:2,StorageAt:now.AddDays(-20));
        var ai=new AiHistory(A,window,now,new(SourceState.Partial,[new(window.Start,"model-a","api",0,4,10,20,30,40)]),
            new(SourceState.Ok,[new(window.Start,"g","p","m",false,99,0,1,100,200)]));
        var snapshot=new PanelDetailsSnapshot(new Dictionary<ResourceKey,string>{{wkey,"Same resource"},{wkeyB,"Same resource"},{dkey,"Database A"},{rkey,"Bucket A"}},
            [worker,worker with{Key=wkeyB,Requests=999}], [d1], [r2], [new(queue,SourceState.Partial,now,0,0)],
            [new(queue,window,now,new(SourceState.NoData,[]),new(SourceState.Ok,[new(window.Start,2d)]),new(SourceState.Partial,[new(window.Start,"ReadMessage","none",3,null,null,4,1)]))],[ai]);
        var aCards=PanelDetailsProjection.Cards(A,0,snapshot);var bCards=PanelDetailsProjection.Cards(B,0,snapshot);
        C("account-filter",aCards.Length==2&&aCards.Single(x=>x.Rows.Any(y=>y.Label=="Requests")).Rows.First().Value==10);
        C("same-name-different-account-key",!aCards.Select(x=>x.Key).Intersect(bCards.Select(x=>x.Key)).Any());
        C("case-insensitive-account",PanelDetailsProjection.Cards(A.ToUpperInvariant(),0,snapshot).Length==2);
        C("r2-source-date-preserved",PanelDetailsProjection.Cards(A,2,snapshot).Any(x=>x.Scope==PanelDetails.Snapshot(now.AddDays(-20))));
        C("d1-metadata-with-missing-traffic",PanelDetailsProjection.Cards(A,1,snapshot).Any(x=>x.Rows.Any(y=>y.Label=="Database size"&&y.Value==100)));
        C("unknown-oldest-not-zero",PanelDetailsProjection.Cards(A,3,snapshot).Single(x=>x.Rows.Any(y=>y.Label=="Oldest at collection")).Rows.Single(y=>y.Label=="Oldest at collection").Value==null);
        C("queue-consumer-visible",PanelDetailsProjection.Cards(A,3,snapshot).Any(x=>x.Rows.Any(y=>y.Label=="Peak concurrency"&&y.Value==2)));
        C("ai-separated-from-gateway",PanelDetailsProjection.Cards(A,4,snapshot).Single().Rows.First().Value==4&&PanelDetailsProjection.Cards(A,5,snapshot).Any(x=>x.Rows.Any(y=>y.Label=="Gateway requests observed"&&y.Value==99)));
        C("sparse-hours-explicit",PanelDetailsProjection.Cards(A,4,snapshot).Single().Note.Contains("1/24"));
        C("missing-history-not-zero",PanelDetailsProjection.Cards(A,4,snapshot with{Ai=[ai with{Inference=new(SourceState.NoData,[])}]}).Single().Rows.All(x=>x.Value==null));
        string fixtureDir=Path.Combine(AppContext.BaseDirectory,"details-fixtures");Directory.CreateDirectory(fixtureDir);
        File.WriteAllText(Path.Combine(fixtureDir,"details-valid-test.json"),wire,new UTF8Encoding(false));
        for(int kind=0;kind<6;kind++){
            var cards=PanelDetailsProjection.Cards(A,kind,snapshot);C("category-present-"+kind,cards.Length>0);
            for(int page=0;page<cards.Length;page++){
                var f=PanelDetails.Build(new(0,kind,page,2),"VetaKeep",cards,now);string json=f.Wire("00000001",true);
                C("safe-category-frame-"+kind+"-"+page,!json.Contains(A)&&!json.Contains(B)&&!json.Contains("SECRET_TOKEN")&&Encoding.UTF8.GetByteCount(json)<=3000);
                File.WriteAllText(Path.Combine(fixtureDir,$"category-{kind}-{page}.json"),json,new UTF8Encoding(false));
            }
        }
        File.WriteAllText(Path.Combine(fixtureDir,"empty-test.json"),empty.Wire("00000001",true),new UTF8Encoding(false));
        int network=0;string temp=Path.Combine(AppContext.BaseDirectory,"m410-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try{
            var config=new HostConfig{Accounts=[new(){ProfileId="a",Name="A",AccountId=A,Enabled=true},new(){ProfileId="b",Name="B",AccountId=B,Enabled=true}]};
            await using var engine=new AppEngine(config,new LocalSettings(temp),(_,_)=>{network++;throw new InvalidOperationException("Panel attempted network");});
            var cache=(Dictionary<ResourceKey,(WorkerAnalytics Value,DateTimeOffset Next)>)typeof(AppEngine).GetField("workerDetails",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(engine)!;
            cache[wkey]=(worker,now.AddMinutes(1));cache[wkeyB]=(worker with{Key=wkeyB,Requests=999},now.AddMinutes(1));
            C("engine-cache-wired",engine.ReadPanelDetails(new(0,0),now).TotalPages==2);
            for(int i=0;i<300;i++){var page=engine.ReadPanelDetails(new(i%2,i%6,i%9,i+1),now);_=page.Wire("00000001");}
            C("navigation-no-api",network==0&&engine.Samples==0&&engine.PanelReceipts.Details==0);
            engine.Locked=true;var hidden=engine.ReadPanelDetails(new(),now);C("lock-hides-names-and-values",hidden.AccountName=="Private"&&hidden.Rows.Length==0&&!hidden.Wire("00000001").Contains("Same resource"));
            engine.Locked=false;C("unlock-cached-data",engine.ReadPanelDetails(new(),now).TotalPages==2);
        }finally{Directory.Delete(temp,true);}
        var ack=new PanelReceipt().Observe("DETAILS_RX request=2 slot=0 kind=0 page=0 rows=4 state=5 test=1");
        C("details-ack-isolated",ack.Details==1&&ack.Pc==0&&ack.Inventory==0);
        C("ack-control-char-reject",ack.Observe("DETAILS_RX bad\n")==ack);
    }
}
