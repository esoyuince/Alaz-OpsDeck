using Microsoft.Data.Sqlite;
using OpsDeck.Core;
using System.Security.Cryptography;
using System.Text.Json;
namespace OpsDeck.Tests;
public static class M411BridgeTaskTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string name,bool ok)=>check("m411-"+name,ok);
        var now=DateTimeOffset.Parse("2026-09-14T21:00:00Z");
        C("setup-empty-path",new BridgeTaskReader("").Read(now).State==SourceState.Setup);
        string missing=Path.Combine(Path.GetTempPath(),"opsdeck-no-such-"+Guid.NewGuid().ToString("N")+".sqlite");
        C("missing-db-error",new BridgeTaskReader(missing).Read(now).State==SourceState.Error);
        string dir=Path.Combine(Path.GetTempPath(),"opsdeck-m411-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,"bridge.sqlite");
        try {
            Create(path);
            var empty=new BridgeTaskReader(path).Read(now);
            C("empty-nodata",empty.State==SourceState.NoData&&empty.Total==0&&empty.ExpiredClaims==0);
            C("minimum-schema-no-sensitive-columns",Columns(path,"tasks").SequenceEqual(new[]{"status","claim_expires_at","created_at","updated_at"}));
            InsertTask(path,"open",now.AddMinutes(-1),null);
            InsertTask(path,"claimed",now.AddMinutes(-2),now.AddMinutes(-1));
            InsertTask(path,"in_progress",now.AddMinutes(-3),null);
            InsertTask(path,"needs_approval",now.AddMinutes(-4),null);
            InsertTask(path,"blocked",now.AddMinutes(-5),null);
            InsertTask(path,"completed",now.AddMinutes(-6),null);
            InsertTask(path,"cancelled",now.AddMinutes(-7),null);
            InsertEvent(path,"runner_claimed",now.AddSeconds(-30));
            var live=new BridgeTaskReader(path).Read(now);
            C("fresh-ok",live.State==SourceState.Ok);
            C("counts",live.Open==1&&live.Claimed==1&&live.InProgress==1&&live.NeedsApproval==1&&live.Blocked==1&&live.Completed==1&&live.Cancelled==1);
            C("derived-counts",live.Active==3&&live.Total==7);
            C("expired-claim",live.ExpiredClaims==1);
            C("latest-event",live.LatestEvent=="runner_claimed"&&live.LatestEventAt==now.AddSeconds(-30));
            C("no-structured-test-invention",live.Detail.Contains("Yapılandırılmış test sayacı",StringComparison.Ordinal));
            byte[] before=SHA256.HashData(File.ReadAllBytes(path));_ = new BridgeTaskReader(path).Read(now);SqliteConnection.ClearAllPools();
            byte[] after=SHA256.HashData(File.ReadAllBytes(path));C("read-only-file-unchanged",before.SequenceEqual(after));
            Exec(path,"UPDATE tasks SET updated_at=$t",("$t",now.AddMinutes(-16).ToString("O")));
            C("stale-real-record",new BridgeTaskReader(path).Read(now).State==SourceState.Stale);
            Exec(path,"UPDATE tasks SET updated_at=$t",("$t",now.AddMinutes(6).ToString("O")));
            C("future-task-rejected",new BridgeTaskReader(path).Read(now).State==SourceState.Error);
            Exec(path,"UPDATE tasks SET updated_at=$t",("$t",now.AddMinutes(-1).ToString("O")));
            InsertEvent(path,"SECRET TEXT",now);
            C("unknown-event-rejected",new BridgeTaskReader(path).Read(now).State==SourceState.Error);
            Exec(path,"DELETE FROM task_events WHERE event_type='SECRET TEXT'");
            InsertTask(path,"mystery",now.AddMinutes(-1),null);
            C("unknown-status-rejected",new BridgeTaskReader(path).Read(now).State==SourceState.Error);
        } finally {SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
        var sample=new BridgeTaskSample(SourceState.Ok,1,2,3,4,5,6,7,1,now,now.AddSeconds(-5),"status",now.AddSeconds(-4));
        var fleet=FleetState.Empty with{Agents=new(2,SourceState.Ok,SourceState.Ok,now,Tasks:sample)};
        using(var doc=JsonDocument.Parse(fleet.Wire(now,false))){var a=doc.RootElement.GetProperty("agents");
            C("wire-metadata",a.GetProperty("task_open").GetInt32()==1&&a.GetProperty("task_in_progress").GetInt32()==3&&a.GetProperty("task_latest_event").GetString()=="status");}
        using(var doc=JsonDocument.Parse(fleet.Wire(now,true))){var a=doc.RootElement.GetProperty("agents");
            C("wire-lock-hides",a.GetProperty("task_open").GetInt32()==-1&&a.GetProperty("task_latest_age_s").GetInt32()==-1&&!a.TryGetProperty("task_latest_event",out _));}
        C("wire-budget",System.Text.Encoding.UTF8.GetByteCount(fleet.Wire(now,false))<3000);
        foreach(string bad in new[]{"relative.sqlite",@"\\server\share\bridge.sqlite","C:\\tmp\\bridge.txt"}){
            try{HostConfig.ValidateBridgeTaskDatabase(bad);C("bad-path-"+bad,false);}catch(ArgumentException){C("bad-path-"+bad,true);}
        }
    }
    private static void Create(string path)
    {using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();using var c=db.CreateCommand();c.CommandText="CREATE TABLE tasks(status TEXT NOT NULL,claim_expires_at TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL); CREATE TABLE task_events(event_type TEXT NOT NULL,created_at TEXT NOT NULL);";c.ExecuteNonQuery();}
    private static string[] Columns(string path,string table)
    {using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();using var c=db.CreateCommand();c.CommandText="PRAGMA table_info("+table+")";using var r=c.ExecuteReader();var x=new List<string>();while(r.Read())x.Add(r.GetString(1));return x.ToArray();}
    private static void InsertTask(string path,string status,DateTimeOffset updated,DateTimeOffset? claim)
    {using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();using var c=db.CreateCommand();c.CommandText="INSERT INTO tasks(status,claim_expires_at,created_at,updated_at) VALUES($s,$e,$c,$u)";c.Parameters.AddWithValue("$s",status);c.Parameters.AddWithValue("$e",claim?.ToString("O")??(object)DBNull.Value);c.Parameters.AddWithValue("$c",updated.AddMinutes(-1).ToString("O"));c.Parameters.AddWithValue("$u",updated.ToString("O"));c.ExecuteNonQuery();}
    private static void InsertEvent(string path,string type,DateTimeOffset at)
    {using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();using var c=db.CreateCommand();c.CommandText="INSERT INTO task_events(event_type,created_at) VALUES($t,$c)";c.Parameters.AddWithValue("$t",type);c.Parameters.AddWithValue("$c",at.ToString("O"));c.ExecuteNonQuery();}
    private static void Exec(string path,string sql,params (string Name,object Value)[] args)
    {using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();using var c=db.CreateCommand();c.CommandText=sql;foreach(var a in args)c.Parameters.AddWithValue(a.Name,a.Value);c.ExecuteNonQuery();}
}
