using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
namespace OpsDeck.Core;
public sealed class LocalSettings
{
    public string DirectoryPath{get;}
    public string ConfigPath=>Path.Combine(DirectoryPath,"settings.json");
    private string SecretPath=>Path.Combine(DirectoryPath,"cloudflare-token.dpapi");
    private string WifiPairingSecretPath=>Path.Combine(DirectoryPath,"wifi-pairing.dpapi");
    public LocalSettings(string? path=null){DirectoryPath=path??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpsDeck");Directory.CreateDirectory(DirectoryPath);}
    public HostConfig Load()
    {
        if(!File.Exists(ConfigPath))return new(){Accounts=CloudAccountConfig.Defaults};
        var c=JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(ConfigPath),Json.Options)??throw new InvalidDataException("Empty configuration");c.Validate();
        if(c.Accounts.Length==0){
            var backup=ConfigPath+".before-m3";if(!File.Exists(backup))File.Copy(ConfigPath,backup);
            if(c.CloudflareAccountId.Length>0&&HasToken&&!HasTokenForAccount(c.CloudflareAccountId))Atomic(AccountSecretPath(c.CloudflareAccountId),File.ReadAllBytes(SecretPath));
            c=c with{SchemaVersion=3,Accounts=c.EffectiveAccounts(),CloudflareAccountId="",CloudflareEnabled=false,BillingEnabled=false};Save(c);
        }
        return c;
    }
    public void Save(HostConfig config){config.Validate();Atomic(ConfigPath,Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config,Json.Options)));}
    private string AccountSecretPath(string account)
    {
        if(!Regex.IsMatch(account,"^[a-fA-F0-9]{32}$"))throw new ArgumentException("Invalid account ID for secret storage.");
        return Path.Combine(DirectoryPath,"cloudflare-"+account.ToLowerInvariant()+".dpapi");
    }
    public bool HasTokenForAccount(string account)=>account.Length>0&&File.Exists(AccountSecretPath(account));
    public string? ReadTokenForAccount(string account)=>ReadSecret(AccountSecretPath(account));
    public void SaveTokenForAccount(string account,string token)=>WriteSecret(AccountSecretPath(account),token);
    public void DeleteTokenForAccount(string account){string path=AccountSecretPath(account);if(File.Exists(path))File.Delete(path);}
    public bool HasToken=>File.Exists(SecretPath);
    public string? ReadToken()=>ReadSecret(SecretPath);
    public void SaveToken(string token)=>WriteSecret(SecretPath,token);
    public void DeleteToken(){if(File.Exists(SecretPath))File.Delete(SecretPath);}
    public string GetOrCreateWifiPairingKeyHex()
    {
        string? current=ReadSecret(WifiPairingSecretPath);
        if(current!=null&&Regex.IsMatch(current,"^[A-F0-9]{64}$"))return current;
        byte[] key=RandomNumberGenerator.GetBytes(32);
        try{string hex=Convert.ToHexString(key);WriteSecret(WifiPairingSecretPath,hex);return hex;}
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    public static void ValidateToken(string token){if(token.Length<16||token.Length>4096||token.Any(char.IsWhiteSpace))throw new ArgumentException("Invalid token format.");}
    private static string? ReadSecret(string path)
    {
        if(!File.Exists(path))return null;var bytes=ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser);
        try{return Encoding.UTF8.GetString(bytes);}finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    private static void WriteSecret(string path,string token)
    {
        ValidateToken(token);byte[] plain=Encoding.UTF8.GetBytes(token);
        try{Atomic(path,ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser));}finally{CryptographicOperations.ZeroMemory(plain);}
    }
    private static void Atomic(string path,byte[] bytes){string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}}
}
public sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection db;
    public HistoryStore(string path)
    {
        db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,DefaultTimeout=1}.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-1024; CREATE TABLE IF NOT EXISTS pc_minute (stamp INTEGER PRIMARY KEY, samples INTEGER NOT NULL,cpu_avg REAL,cpu_min REAL,cpu_max REAL,ram_avg REAL); PRAGMA user_version=1;";cmd.ExecuteNonQuery();
    }
    public void Add(DateTimeOffset stamp,IReadOnlyList<PcSample> samples)
    {
        var cpu=samples.Where(s=>s.Cpu.HasValue).Select(s=>s.Cpu!.Value).ToArray();var ram=samples.Where(s=>s.RamUsedGib.HasValue).Select(s=>s.RamUsedGib!.Value).ToArray();
        using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="INSERT OR REPLACE INTO pc_minute VALUES ($t,$n,$a,$l,$h,$r); DELETE FROM pc_minute WHERE stamp < $cutoff;";
        cmd.Parameters.AddWithValue("$t",stamp.ToUnixTimeSeconds()/60*60);cmd.Parameters.AddWithValue("$n",samples.Count);
        cmd.Parameters.AddWithValue("$a",cpu.Length>0?(object)cpu.Average():DBNull.Value);cmd.Parameters.AddWithValue("$l",cpu.Length>0?(object)cpu.Min():DBNull.Value);cmd.Parameters.AddWithValue("$h",cpu.Length>0?(object)cpu.Max():DBNull.Value);cmd.Parameters.AddWithValue("$r",ram.Length>0?(object)ram.Average():DBNull.Value);
        cmd.Parameters.AddWithValue("$cutoff",stamp.AddDays(-7).ToUnixTimeSeconds());cmd.ExecuteNonQuery();tx.Commit();
    }
    public long Count(){using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM pc_minute";return (long)(cmd.ExecuteScalar()??0L);}
    public void Dispose()=>db.Dispose();
}
public sealed class SafeLog(string directory)
{
    private readonly object gate=new();private DateTimeOffset nextRotationAttempt=DateTimeOffset.MinValue;
    private bool RotationFilesAvailable(string path)
    {
        foreach(string candidate in new[]{path,Path.Combine(directory,"host.1.log"),Path.Combine(directory,"host.2.log"),Path.Combine(directory,"host.3.log"),Path.Combine(directory,"host.4.log")})
        {
            if(!File.Exists(candidate))continue;
            try{using var probe=new FileStream(candidate,FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
        }
        return true;
    }
    private void TryRotate(string path)
    {
        if(DateTimeOffset.UtcNow<nextRotationAttempt||!File.Exists(path)||new FileInfo(path).Length<=1024*1024)return;
        if(!RotationFilesAvailable(path)){nextRotationAttempt=DateTimeOffset.UtcNow.AddMinutes(1);return;}
        try
        {
            for(int i=3;i>=1;i--){string old=Path.Combine(directory,"host."+i+".log");if(File.Exists(old))File.Move(old,Path.Combine(directory,"host."+(i+1)+".log"),true);}
            File.Move(path,Path.Combine(directory,"host.1.log"),true);nextRotationAttempt=DateTimeOffset.MinValue;
        }
        catch(IOException){nextRotationAttempt=DateTimeOffset.UtcNow.AddMinutes(1);}catch(UnauthorizedAccessException){nextRotationAttempt=DateTimeOffset.UtcNow.AddMinutes(1);}
    }
    public void Event(string code,object? fields=null)
    {
        lock(gate)
        {
            string path=Path.Combine(directory,"host.log");
            try{Directory.CreateDirectory(directory);TryRotate(path);File.AppendAllText(path,JsonSerializer.Serialize(new {time=DateTimeOffset.UtcNow,code,fields},Json.Options)+Environment.NewLine);}
            catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
}
