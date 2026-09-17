using OpsDeck.Core;
namespace OpsDeck.Host;
public sealed class CloudResourceDetailsForm : Form
{
    private readonly AppEngine engine;private readonly ResourceKey key;private readonly CancellationTokenSource stop=new();
    private readonly TextBox content=new(){Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical};
    private readonly Button refresh=new(){Text="Ölçümü yenile (en az 60 sn)",AutoSize=true,Dock=DockStyle.Bottom};private bool loading,closing;
    public CloudResourceDetailsForm(AppEngine engine,ResourceKey key)
    {
        this.engine=engine;this.key=key;Text=$"ALAZ OPSDECK — {key.Kind} ölçümleri";ClientSize=new Size(820,500);MinimumSize=new Size(670,420);Font=new Font("Segoe UI",11);StartPosition=FormStartPosition.CenterParent;
        Controls.Add(content);Controls.Add(refresh);content.Text="Salt-okunur kaynak ölçümü bekleniyor.";Shown+=async(_,_)=>await LoadReading();refresh.Click+=async(_,_)=>await LoadReading();FormClosing+=(_,e)=>{closing=true;stop.Cancel();if(loading)e.Cancel=true;};FormClosed+=(_,_)=>stop.Dispose();
    }
    private static string N(double? v,string unit="",string format="0.###")=>v.HasValue?v.Value.ToString(format)+unit:"—";
    private async Task LoadReading()
    {
        if(loading||closing)return;loading=true;refresh.Enabled=false;
        try
        {
            content.Text=key.Kind switch
            {
                ResourceKind.D1=>D1(await engine.ReadD1Detail(key,stop.Token)),
                ResourceKind.R2=>R2(await engine.ReadR2Detail(key,stop.Token)),
                _=>"Bu kaynak türü bu ayrıntı penceresinde desteklenmiyor."
            };
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception e)when(e is ArgumentException or InvalidOperationException){if(!closing)content.Text="Ölçüm açılamadı: "+e.Message;}
        finally{loading=false;if(closing)Close();else refresh.Enabled=true;}
    }
    private static string D1(D1Analytics s)
    {
        string state=!s.IsCurrent(DateTimeOffset.UtcNow)?"Eski okuma":s.State.ToString();
        return $"D1: {s.Key.Id}\r\nDurum: {state}\r\nPencere (UTC): {s.Start:yyyy-MM-dd HH:mm} – {s.End:yyyy-MM-dd HH:mm}\r\nOkuma (yerel): {s.CollectedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n\r\nRead queries: {N(s.ReadQueries,format:"0")}\r\nWrite queries: {N(s.WriteQueries,format:"0")}\r\nRows read: {N(s.RowsRead,format:"0")}\r\nRows written: {N(s.RowsWritten,format:"0")}\r\nResponse: {N(s.ResponseBytes.HasValue?s.ResponseBytes/1048576d:null," MiB")}\r\nQuery latency P90: {N(s.QueryP90Ms," ms")}\r\n\r\nCurrent D1 file size: {N(s.DatabaseSizeBytes.HasValue?s.DatabaseSizeBytes/1048576d:null," MiB")}\r\nTables: {N(s.TableCount,format:"0")}\r\nJurisdiction: {(s.Jurisdiction.Length>0?s.Jurisdiction:"default / unspecified")}\r\nRead replication: {(s.ReplicationMode.Length>0?s.ReplicationMode:"unspecified")}\r\n\r\n{s.Detail}\r\n\r\nHam SQL/query metinleri toplanmaz. Aynı kaynak 60 saniye içinde önbellekten karşılanır.";
    }
    private static string R2(R2Analytics s)
    {
        string state=!s.IsCurrent(DateTimeOffset.UtcNow)?"Eski okuma":s.State.ToString();string top=s.TopOperations is{Length:>0}?string.Join("\r\n",s.TopOperations.Select(x=>$"  {x.ActionType} / {x.Status}: {x.Requests:0}")):"  —";
        return $"R2 bucket: {s.Key.Id}\r\nKapsam: default jurisdiction\r\nDurum: {state}\r\nOperasyon penceresi (UTC): {s.OperationsStart:yyyy-MM-dd HH:mm} – {s.OperationsEnd:yyyy-MM-dd HH:mm}\r\nOkuma (yerel): {s.CollectedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n\r\nRequests: {N(s.TotalRequests,format:"0")}\r\nSuccess: {N(s.SuccessRequests,format:"0")}\r\nUser errors: {N(s.UserErrors,format:"0")}\r\nInternal errors: {N(s.InternalErrors,format:"0")}\r\n\r\nPayload: {N(s.PayloadBytes.HasValue?s.PayloadBytes/1073741824d:null," GiB")}\r\nMetadata: {N(s.MetadataBytes.HasValue?s.MetadataBytes/1048576d:null," MiB")}\r\nObjects: {N(s.ObjectCount,format:"0")}\r\nPending multipart uploads: {N(s.UploadCount,format:"0")}\r\nStorage snapshot: {(s.StorageAt.HasValue?s.StorageAt.Value.ToString("yyyy-MM-dd HH:mm 'UTC'"):"—")}\r\n\r\nTop operation/status groups:\r\n{top}\r\n\r\n{s.Detail}\r\n\r\nObject adları toplanmaz. Restricted-jurisdiction bucket'lar bu görünümün kapsamı değildir.";
    }
}
