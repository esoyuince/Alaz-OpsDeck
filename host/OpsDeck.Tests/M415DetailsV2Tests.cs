using System.Text;
using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;

public static class M415DetailsV2Tests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string name,bool ok)=>check("m415-"+name,ok);
        const string A="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var now=DateTimeOffset.Parse("2026-09-15T08:00:00Z");
        var q=new PanelDetailsRequest(0,0,0,15);
        var empty=PanelDetails.Build(q,"VetaKeep",[],now);
        C("empty-read-only-copy",empty.Note.Contains("never refreshes")&&empty.Scope=="Read-only host cache");
        C("empty-reason",empty.Reason.Contains("no validated card"));
        var ok=new PanelDetailCard("0123456789abcdef","Worker A","09-15 07:00 to 09-15 07:05 UTC","Observed window",SourceState.Ok,now.AddSeconds(-10),[new("Requests","count",1)]);
        var okPage=PanelDetails.Build(q,"VetaKeep",[ok],now);using(var doc=JsonDocument.Parse(okPage.Wire("00000001")))C("ok-reason-null",doc.RootElement.GetProperty("reason").ValueKind==JsonValueKind.Null);
        var stale=PanelDetails.Build(q,"VetaKeep",[ok with{CollectedAt=now.AddSeconds(-181)}],now);
        C("stale-reason",stale.State==SourceState.Stale&&stale.Reason.Contains("180s"));
        var translated=PanelDetails.Build(q,"A",[ok with{Reason="Ölçüm şİığ"}],now);
        C("reason-ascii",translated.Reason=="Olcum sIig");
        var wkey=new ResourceKey(A,ResourceKind.Worker,"account","worker-a");
        var dkey=new ResourceKey(A,ResourceKind.D1,"account","12345678-1234-1234-1234-123456789abc");
        var rkey=new ResourceKey(A,ResourceKind.R2,"default","bucket-a");
        var workerNoData=new WorkerAnalytics(wkey,SourceState.NoData,now,now.AddMinutes(-8),now.AddMinutes(-3),Detail:"SECRET_DETAIL_MUST_NOT_LEAK");
        var workerOk=workerNoData with{State=SourceState.Ok,Requests=1,Errors=0,Detail="SECRET_DETAIL_MUST_NOT_LEAK"};
        var d1=new D1Analytics(dkey,SourceState.NoData,now,now.AddHours(-24),now,DatabaseSizeBytes:100,TableCount:2,Detail:"SECRET_D1");
        var r2=new R2Analytics(rkey,SourceState.Partial,now,now.AddHours(-24),now,PayloadBytes:20,ObjectCount:2,StorageAt:now.AddHours(-1),Detail:"SECRET_R2");
        var restrictedQueue=new QueueInfo(A,new string('c',32),"Queue A",Jurisdiction:"eu");
        var queue=new QueueBacklog(restrictedQueue,SourceState.NoData,now,Detail:"SECRET_QUEUE");
        var window=AnalyticsWindow.Last24Hours(now);
        var ai=new AiHistory(A,window,now,new(SourceState.NoData,[],"SECRET_AI"),new(SourceState.NoData,[],"SECRET_GATEWAY"));
        var names=new Dictionary<ResourceKey,string>{{wkey,"Worker A"},{dkey,"Database A"},{rkey,"Bucket A"}};
        var snapshot=new PanelDetailsSnapshot(names,[workerNoData],[d1],[r2],[queue],[],[ai]);
        var workerCards=PanelDetailsProjection.Cards(A,0,snapshot);
        C("worker-nodata-reason",workerCards.All(x=>x.Reason.Contains("5m"))&&workerCards.All(x=>!x.Reason.Contains("SECRET")));
        var okCards=PanelDetailsProjection.Cards(A,0,snapshot with{Workers=[workerOk]});
        C("ok-does-not-leak-detail",okCards.All(x=>x.Reason=="")&&okCards.All(x=>!x.Note.Contains("SECRET")));
        C("d1-metadata-reason",PanelDetailsProjection.Cards(A,1,snapshot).All(x=>x.Reason.StartsWith("Metadata observed")));
        C("r2-partial-reason",PanelDetailsProjection.Cards(A,2,snapshot).All(x=>x.Reason.Contains("Storage snapshot observed")));
        C("queue-restricted-reason",PanelDetailsProjection.Cards(A,3,snapshot).Single().Reason.Contains("Restricted jurisdiction"));
        C("ai-nodata-reason",PanelDetailsProjection.Cards(A,4,snapshot).Single().Reason.Contains("No Workers AI groups"));
        var d1Page=PanelDetails.Build(new(0,1,0,15),"VetaKeep",PanelDetailsProjection.Cards(A,1,snapshot),now);
        string wire=d1Page.Wire("00000001");using(var doc=JsonDocument.Parse(wire))C("reason-on-wire",doc.RootElement.GetProperty("reason").GetString()!.StartsWith("Metadata observed"));
        C("no-raw-detail-on-wire",!wire.Contains("SECRET")&&!workerCards.Any(x=>x.Reason.Contains("SECRET")));
        C("wire-budget",Encoding.UTF8.GetByteCount(wire)<=3000);
    }
}
