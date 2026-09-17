using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace OpsDeck.Core;

public sealed record ProjectAssignment(ResourceKey Key,string Project);
public sealed record ProjectMapSnapshot(ProjectAssignment[] Assignments,string Revision);
public sealed class ProjectMap
{
    private readonly string path; private readonly object gate=new();
    public ProjectMap(string directory) {Directory.CreateDirectory(directory);path=Path.Combine(directory,"project-map.json");}
    public ProjectMapSnapshot Load()
    {
        lock(gate) {
            if(!File.Exists(path)) return new([],"absent");
            if(new FileInfo(path).Length>1024*1024) throw new InvalidDataException("Project map exceeds size limit.");
            var bytes=File.ReadAllBytes(path); var data=JsonSerializer.Deserialize<ProjectAssignment[]>(bytes,Json.Options)??throw new InvalidDataException("Empty project map.");
            Validate(data);return new(data,Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }
    public static void Validate(ProjectAssignment[] rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if(rows.Length>4000)throw new ArgumentException("Too many project mappings.");var seen=new HashSet<ResourceKey>();
        foreach(var row in rows) {
            if(row is null||row.Key is null)throw new ArgumentException("Missing resource mapping identity.");
            row.Key.Validate();if(!seen.Add(row.Key))throw new ArgumentException("Duplicate resource mapping.");
            if(string.IsNullOrWhiteSpace(row.Project)||row.Project.Length>48||row.Project.Any(char.IsControl))throw new ArgumentException("Project name must have 1-48 printable characters.");
        }
    }
    public ProjectMapSnapshot Save(IEnumerable<ProjectAssignment> rows,string expectedRevision)
    {
        lock(gate) {
            using var lease=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var values=rows.OrderBy(x=>x.Key.AccountId,StringComparer.Ordinal).ThenBy(x=>x.Key.Kind).ThenBy(x=>x.Key.Id,StringComparer.Ordinal).ToArray();Validate(values);
            if(Load().Revision!=expectedRevision)throw new InvalidOperationException("Project map changed; reopen before saving.");
            byte[] bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values,Json.Options));
            if(bytes.Length>1024*1024)throw new ArgumentException("Project map exceeds size limit.");
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {File.WriteAllBytes(temp,bytes);if(File.Exists(path))File.Copy(path,path+".previous",true);File.Move(temp,path,true);}
            finally {if(File.Exists(temp))File.Delete(temp);}
            return new(values,Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }
    public static string Label(ResourceKey key,IReadOnlyDictionary<ResourceKey,string> assignments) => assignments.TryGetValue(key,out string? name)?name:"Unassigned";
}
