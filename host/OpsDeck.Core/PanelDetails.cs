using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;

// Display navigation only. Never initiates an API request or OS action.
public sealed record PanelDetailsRequest(int Slot=0,int Kind=0,int Page=0,int RequestId=1,string Group="all")
{
    public void Validate()
    {
        if(Slot is <0 or >1||Kind is <0 or >5||Page is <0 or >1023||RequestId<1||
            !Regex.IsMatch(Group,@"\A(all|[a-f0-9]{16})\z")||(Group!="all"&&Kind>2))
            throw new ArgumentException("Invalid panel details request.");
    }
    public static bool TryParse(string line,out PanelDetailsRequest? request)
    {
        request=null;if(line.Length>320)return false;
        var m=Regex.Match(line,@"(?:\A| )opsdeck\.ui: DETAILS_REQUEST slot=([01]) kind=([0-5]) page=([0-9]{1,4})(?: group=(all|[a-f0-9]{16}))? request=([0-9]{1,10})\z",RegexOptions.CultureInvariant);
        if(!m.Success||!int.TryParse(m.Groups[5].Value,out int id))return false;
        string group=m.Groups[4].Success?m.Groups[4].Value:"all";
        var q=new PanelDetailsRequest(int.Parse(m.Groups[1].Value),int.Parse(m.Groups[2].Value),int.Parse(m.Groups[3].Value),id,group);
        try{q.Validate();request=q;return true;}catch(ArgumentException){return false;}
    }
}
public sealed record PanelDetailMetric(string Label,string Unit,double? Value);
public sealed record PanelDetailCard(string Key,string Title,string Scope,string Note,SourceState State,
    DateTimeOffset CollectedAt,PanelDetailMetric[] Rows,string Reason="");
public sealed record PanelDetailPage(PanelDetailsRequest Request,int Page,int TotalPages,string AccountName,
    string Key,string Title,string Scope,string Note,SourceState State,int AgeS,PanelDetailMetric[] Rows,string Reason="")
{
    private static readonly JsonSerializerOptions Options=new(Json.Options){DefaultIgnoreCondition=JsonIgnoreCondition.Never};
    public string Wire(string generation,bool test=false)
    {
        Request.Validate();
        if(!Regex.IsMatch(generation,@"\A[a-f0-9]{8}\z")||!Regex.IsMatch(Key,@"\A[a-f0-9]{16}\z")||
            TotalPages is <0 or >1024||Page!=(TotalPages==0?0:Math.Min(Request.Page,TotalPages-1))||
            !Enum.IsDefined(State)||AgeS< -1||Rows.Length>4||
            (TotalPages==0&&(Rows.Length!=0||AgeS!=-1||State is SourceState.Ok or SourceState.Partial or SourceState.Stale))||
            (TotalPages>0&&(Rows.Length==0||AgeS<0))||
            (State==SourceState.Ok&&AgeS>180))throw new ArgumentException("Invalid details page.");
        PanelDetails.Text(AccountName,22);PanelDetails.Text(Title,48);PanelDetails.Text(Scope,80);PanelDetails.Text(Note,80);
        if(Reason.Length>0)PanelDetails.Text(Reason,80);
        foreach(var row in Rows){PanelDetails.Text(row.Label,28);PanelDetails.Text(row.Unit,12);PanelDetails.Number(row.Value);}
        if(Rows.Select(x=>x.Label).Distinct(StringComparer.Ordinal).Count()!=Rows.Length)throw new ArgumentException("Duplicate metric labels.");
        string text=JsonSerializer.Serialize(new{type="opsdeck.details.v1",generation,test,slot=Request.Slot,kind=Request.Kind,
            requested_page=Request.Page,page=Page,total_pages=TotalPages,request_id=Request.RequestId,group=Request.Group,
            account_name=AccountName,key=Key,title=Title,scope=Scope,note=Note,reason=Reason.Length>0?Reason:null,state=(int)State,age_s=AgeS,rows=Rows},Options);
        if(Encoding.UTF8.GetByteCount(text)>3000)throw new InvalidOperationException("Details UART budget exceeded.");
        return text;
    }
}
public static class PanelDetails
{
    public static readonly string[] KindNames=["Worker","D1","R2","Queues","Workers AI","AI Gateway"];
    private static int StateRank(SourceState state)=>state switch{SourceState.Ok=>0,SourceState.Partial=>1,SourceState.Stale=>2,SourceState.NoData=>3,SourceState.Setup=>4,SourceState.Denied=>5,SourceState.Error=>6,_=>7};
    public static void Text(string text,int max){if(string.IsNullOrWhiteSpace(text)||text.Length>max||text.Any(c=>c<32||c>126))throw new ArgumentException("Invalid display text.");}
    public static void Number(double? value){if(value.HasValue&&(!double.IsFinite(value.Value)||value<0||value>1e16))throw new ArgumentException("Invalid display number.");}
    public static string Key(string identity)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
    public static PanelDetailPage Empty(PanelDetailsRequest q,string name,string reason,SourceState state=SourceState.NoData)
    {q.Validate();return new(q,0,0,InventoryPaging.Label(name,22),new string('0',16),KindNames[q.Kind],"Read-only host cache","Panel navigation never refreshes APIs",state,-1,[],InventoryPaging.Label(reason,80));}
    public static PanelDetailPage Build(PanelDetailsRequest q,string name,IEnumerable<PanelDetailCard> cards,DateTimeOffset now)
    {
        q.Validate();var all=cards.Take(1025).OrderBy(c=>StateRank(c.State)).ThenByDescending(c=>c.CollectedAt).ThenBy(c=>c.Key,StringComparer.Ordinal).ToArray();
        if(all.Length>1024||all.Select(c=>c.Key).Distinct(StringComparer.Ordinal).Count()!=all.Length)throw new ArgumentException("Details card bound/identity.");
        if(all.Length==0)return Empty(q,name,"Host cache has no validated card for this category yet");
        int page=Math.Min(q.Page,all.Length-1);var c=all[page];
        if(c.CollectedAt==default||c.CollectedAt>now.AddSeconds(5))throw new ArgumentException("Invalid source collection time.");
        int age=(int)Math.Clamp(Math.Floor((now-c.CollectedAt).TotalSeconds),0,int.MaxValue);
        var state=age>180&&c.State is SourceState.Ok or SourceState.Partial?SourceState.Stale:c.State;
        string reason=state==SourceState.Stale&&c.State is SourceState.Ok or SourceState.Partial
            ?"Cached sample exceeded 180s source freshness TTL":c.Reason;
        return new(q,page,all.Length,InventoryPaging.Label(name,22),c.Key,InventoryPaging.Label(c.Title),
            InventoryPaging.Label(c.Scope,80),InventoryPaging.Label(c.Note,80),state,age,c.Rows,
            string.IsNullOrWhiteSpace(reason)?"":InventoryPaging.Label(reason,80));
    }
    public static string Window(DateTimeOffset start,DateTimeOffset end)
    {
        if(end<start||start==default||end==default)throw new ArgumentException("Invalid source window.");
        return start.UtcDateTime.ToString("MM-dd HH:mm",CultureInfo.InvariantCulture)+" to "+end.UtcDateTime.ToString("MM-dd HH:mm",CultureInfo.InvariantCulture)+" UTC";
    }
    public static string Snapshot(DateTimeOffset? at)=>at.HasValue?"Snapshot "+at.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture)+" UTC":"Snapshot time unknown";
}
