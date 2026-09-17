using System.Text;
using System.Text.Json;
using OpsDeck.Core;
namespace OpsDeck.Tests;

public static class M68ScreenCloseoutTests
{
    public static async Task Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m68-screen-"+n,ok);
        C("request-valid",PanelOpsViewRequest.TryParse("opsdeck.ui: OPSVIEW_REQUEST kind=0 window=3 metric=9 variant=0 request=42",out var parsed)&&parsed==new PanelOpsViewRequest(0,3,9,0,42));
        C("request-invalid-kind",!PanelOpsViewRequest.TryParse("opsdeck.ui: OPSVIEW_REQUEST kind=9 window=1 metric=0 variant=0 request=1",out _));
        C("request-invalid-metric",!PanelOpsViewRequest.TryParse("opsdeck.ui: OPSVIEW_REQUEST kind=0 window=1 metric=10 variant=0 request=1",out _));
        C("request-invalid-variant",!PanelOpsViewRequest.TryParse("opsdeck.ui: OPSVIEW_REQUEST kind=0 window=1 metric=0 variant=2 request=1",out _));
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-screen-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            var engine=new AppEngine(new HostConfig(),new LocalSettings(dir));engine.Start(false,false);
            await Task.Delay(100);
            var field=typeof(AppEngine).GetField("telemetryHistory",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            var store=(TelemetryHistoryStore?)field?.GetValue(engine);var now=DateTimeOffset.UtcNow;var link=LinkHealthSnapshot.Initial(now.AddHours(-1));
            for(int i=0;i<60&&store!=null;i++){
                double d=i+0.123456;var at=now.AddMinutes(-i);var vol=new VolumeReading("C:",100L*1024*1024*1024,(long)((20+i%10)*1024d*1024*1024),(long)((20+i%10)*1024d*1024*1024),at,"fixture");var vol2=new VolumeReading("D:",8L*1024*1024*1024,4L*1024*1024*1024,4L*1024*1024*1024,at,"fixture");
                store.Add(at,[new PcSample(Cpu:10+d%80,Gpu:20+d%70,GpuTemp:50+d%20,RamUsedGib:12+d%8,RamTotalGib:32,VramUsedGib:2+d%4,VramTotalGib:8,RxMbps:d%90,TxMbps:d%40,IntelGpu:15+d%75,IntelSharedGib:3+d%2,IntelSharedLimitGib:18,Fan1Rpm:2500+d*10,Fan2Rpm:2700+d*10,CpuTemp:55+d%25,ChassisTemp:38+d%10,Volumes:[vol,vol2])],link);
            }
            bool allBounded=true,allShape=true,h1Full=false,d7Partial=false,d7LeadingNull=false,sharedSeries=false,storageSeries=false,storageD=false,thermalTriple=false;
            for(int window=0;window<4;window++)for(int metric=0;metric<10;metric++)
            {
                string wire=engine.PanelOpsViewFrame(new PanelOpsViewRequest(0,window,metric,0,100+window*10+metric));
                allBounded&=Encoding.UTF8.GetByteCount(wire)<3000;
                using var doc=JsonDocument.Parse(wire);var root=doc.RootElement;
                int pointCount=root.GetProperty("points").GetArrayLength();
                allShape&=root.GetProperty("type").GetString()=="opsdeck.opsview.v1"&&root.GetProperty("kind").GetInt32()==0&&root.GetProperty("window").GetInt32()==window&&root.GetProperty("metric").GetInt32()==metric&&root.GetProperty("variant").GetInt32()==0&&pointCount==root.GetProperty("bucket_count").GetInt32()&&root.GetProperty("sample_rows").GetInt32()<=root.GetProperty("expected_minutes").GetInt32()&&pointCount<=48;
                if(window==1&&metric==0)h1Full=root.GetProperty("coverage_state").GetInt32()==2&&root.GetProperty("coverage_pct").GetInt32()==100&&root.GetProperty("sample_rows").GetInt32()==60&&root.GetProperty("expected_minutes").GetInt32()==60;
                if(window==3&&metric==0){d7Partial=root.GetProperty("coverage_state").GetInt32()==1&&root.GetProperty("sample_rows").GetInt32()==60&&root.GetProperty("expected_minutes").GetInt32()==10080&&root.GetProperty("coverage_pct").GetInt32()==1&&root.GetProperty("covered_buckets").GetInt32()<root.GetProperty("bucket_count").GetInt32();var pts=root.GetProperty("points");d7LeadingNull=pts.GetArrayLength()==48&&pts[0][0].ValueKind==JsonValueKind.Null&&pts[pts.GetArrayLength()-1][0].ValueKind!=JsonValueKind.Null;}
                if(window==1&&metric==9)sharedSeries=root.GetProperty("covered_buckets").GetInt32()>0&&root.GetProperty("state").GetInt32()==1;
                if(window==1&&metric==8)storageSeries=root.GetProperty("covered_buckets").GetInt32()>0&&root.GetProperty("volume_count").GetInt32()==2&&root.GetProperty("label").GetString()=="C:";
                if(window==1&&metric==5&&pointCount>0){var pnt=root.GetProperty("points").EnumerateArray().First(x=>x[0].ValueKind!=JsonValueKind.Null);thermalTriple=pnt.GetArrayLength()==3&&pnt[0].ValueKind!=JsonValueKind.Null&&pnt[1].ValueKind!=JsonValueKind.Null&&pnt[2].ValueKind!=JsonValueKind.Null;}
            }
            string diskD=engine.PanelOpsViewFrame(new PanelOpsViewRequest(0,1,8,1,490));using(var doc=JsonDocument.Parse(diskD)){var r=doc.RootElement;storageD=r.GetProperty("variant").GetInt32()==1&&r.GetProperty("label").GetString()=="D:"&&r.GetProperty("covered_buckets").GetInt32()>0;}
            C("history-all-windows",allShape);C("history-h1-full",h1Full);C("history-7d-partial",d7Partial);C("history-7d-leading-null",d7LeadingNull);C("history-budget",allBounded);C("shared-history",sharedSeries);C("storage-history",storageSeries);C("storage-d-variant",storageD);C("thermal-three-series",thermalTriple);
            string alerts=engine.PanelOpsViewFrame(new PanelOpsViewRequest(1,0,0,0,500));
            using(var doc=JsonDocument.Parse(alerts))
            {
                var root=doc.RootElement;C("alerts-shape",root.GetProperty("kind").GetInt32()==1&&root.GetProperty("items").GetArrayLength()<=6);
                C("alerts-budget",Encoding.UTF8.GetByteCount(alerts)<3000);
                C("alerts-no-secret-fields",!alerts.Contains("token",StringComparison.OrdinalIgnoreCase)&&!alerts.Contains("password",StringComparison.OrdinalIgnoreCase));
            }
            string timeline=engine.PanelOpsViewFrame(new PanelOpsViewRequest(2,0,0,0,501));
            using(var doc=JsonDocument.Parse(timeline))
            {
                var root=doc.RootElement;C("timeline-shape",root.GetProperty("kind").GetInt32()==2&&root.GetProperty("events").GetArrayLength()<=7);
                C("timeline-budget",Encoding.UTF8.GetByteCount(timeline)<3000);
                C("timeline-sanitized",!timeline.Contains("token",StringComparison.OrdinalIgnoreCase)&&!timeline.Contains("password",StringComparison.OrdinalIgnoreCase));
            }
            await engine.DisposeAsync();
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{Directory.Delete(dir,true);}catch(IOException){}}
    }
}

