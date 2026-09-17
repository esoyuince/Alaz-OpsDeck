using System.Text.Json;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public sealed record ManagedCodexOwnership(string Target,string ThreadId,string TurnId,string WorkingDirectory,ManagedCodexSandbox Sandbox,DateTimeOffset UpdatedAt)
{
    public void Validate()
    {
        if(!Regex.IsMatch(Target,"^managed:[A-Za-z0-9._:-]{1,88}$")||ThreadId.Length is <1 or >128||TurnId.Length>128||ThreadId.Any(char.IsControl)||TurnId.Any(char.IsControl))throw new InvalidDataException("Invalid managed Codex ownership metadata.");
        if(WorkingDirectory.Length is <1 or >1024||!Path.IsPathFullyQualified(WorkingDirectory)||WorkingDirectory.Any(char.IsControl)||!Enum.IsDefined(Sandbox))throw new InvalidDataException("Invalid managed Codex ownership path metadata.");
    }
}

public sealed class ManagedCodexOwnershipStore
{
    public const int MaxEntries=32;private readonly string path;private readonly object gate=new();private readonly Dictionary<string,ManagedCodexOwnership> rows=new(StringComparer.Ordinal);
    public ManagedCodexOwnershipStore(string path)
    {
        if(!Path.IsPathFullyQualified(path))throw new ArgumentException("Ownership path must be absolute.");this.path=path;
        foreach(var row in LoadFile(path)){row.Validate();if(!rows.TryAdd(row.Target,row))throw new InvalidDataException("Duplicate managed Codex ownership target.");}
    }
    public ManagedCodexOwnership[] Read()=>rows.Values.OrderByDescending(x=>x.UpdatedAt).ToArray();
    public void Put(ManagedCodexOwnership row)
    {
        row.Validate();lock(gate){rows[row.Target]=row;while(rows.Count>MaxEntries){var oldest=rows.Values.OrderBy(x=>x.UpdatedAt).First();rows.Remove(oldest.Target);}Save();}
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(rows.Values.OrderBy(x=>x.Target,StringComparer.Ordinal).ToArray(),Json.Options);if(bytes.Length>64*1024)throw new InvalidDataException("Managed Codex ownership metadata exceeds bound.");
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private static ManagedCodexOwnership[] LoadFile(string path)
    {
        if(!File.Exists(path))return [];var info=new FileInfo(path);if(info.Length>64*1024)throw new InvalidDataException("Managed Codex ownership metadata exceeds bound.");
        var rows=JsonSerializer.Deserialize<ManagedCodexOwnership[]>(File.ReadAllBytes(path),Json.Options)??[];if(rows.Length>MaxEntries)throw new InvalidDataException("Too many managed Codex ownership records.");return rows;
    }
}
