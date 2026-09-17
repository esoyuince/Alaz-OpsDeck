using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public enum ResourceKind { Worker, D1, R2, Pages }
public sealed record ResourceKey(string AccountId, ResourceKind Kind, string Scope, string Id)
{
    public void Validate()
    {
        if(!Regex.IsMatch(AccountId,"^[a-f0-9]{32}$") || !Enum.IsDefined(Kind)) throw new ArgumentException("Invalid resource identity.");
        if(Scope.Length>32 || !Regex.IsMatch(Scope,"^[a-z0-9_-]+$")) throw new ArgumentException("Invalid resource scope.");
        if(string.IsNullOrWhiteSpace(Id) || Id.Length>256 || Id.Any(char.IsControl)) throw new ArgumentException("Invalid resource ID.");
    }
}
public sealed record CloudResource(ResourceKey Key, string Name, string Deployment="Not queried");
public sealed record ResourceSet(string AccountId, string ProfileName, ResourceKind Kind,
    SourceState State, CloudResource[] Items, bool Complete, DateTimeOffset CollectedAt,
    string Detail, int RetrySeconds=0)
{
    public static ResourceSet Setup(CloudAccountConfig p, ResourceKind kind) =>
        new(p.AccountId.ToLowerInvariant(),p.Name,kind,SourceState.Setup,[],false,DateTimeOffset.UtcNow,"Account is not configured/enabled");
}
public sealed record ResourceInventory(ResourceSet[] Sets)
{
    public static ResourceInventory Empty => new([]);
    public IEnumerable<CloudResource> Items => Sets.SelectMany(s=>s.Items);
    public bool Complete => Sets.Length>0 && Sets.All(s=>s.Complete);
}
