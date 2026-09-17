namespace OpsDeck.Core;

/// <summary>Logical volume capacity, not a physical HDD/SSD identification.</summary>
public sealed record VolumeReading(string Id,long? TotalBytes=null,long? FreeBytes=null,
    long? AvailableBytes=null,DateTimeOffset? ObservedAt=null,string Status="unavailable")
{
    public bool Valid=>Id.Length==2&&Id[0]>='A'&&Id[0]<='Z'&&Id[1]==':'&&
        TotalBytes is >0&&FreeBytes>=0&&FreeBytes<=TotalBytes&&AvailableBytes>=0&&AvailableBytes<=FreeBytes;
    public double? UsedPercent=>Valid?100d*(TotalBytes!.Value-FreeBytes!.Value)/TotalBytes.Value:null;
    public double? TotalGib=>Valid?TotalBytes!.Value/1073741824d:null;
    public double? FreeGib=>Valid?FreeBytes!.Value/1073741824d:null;
    public SourceState State(DateTimeOffset now)=>!Valid?SourceState.NoData:
        Freshness.IsCurrent(ObservedAt,now,30)?SourceState.Ok:SourceState.Stale;
    public object Wire(DateTimeOffset now)=>new{id=Id,state=(int)State(now),age_s=Freshness.AgeSeconds(ObservedAt,now),
        total_gib=TotalGib,free_gib=FreeGib,available_gib=Valid?AvailableBytes!.Value/1073741824d:(double?)null};
}
public static class DiskTelemetry
{
    public const int PanelLimit=2;
    public static VolumeReading[] Read()
    {
        var result=new List<VolumeReading>();
        try{
            foreach(var d in DriveInfo.GetDrives()){
                string id=d.Name.Length>=2?d.Name[..2].ToUpperInvariant():"";
                try{
                    // No network shares, optical media, file scans or disk writes.
                    if(d.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))continue;
                    if(!d.IsReady){result.Add(new(id,Status:"Volume not ready"));continue;}
                    long total=d.TotalSize,free=d.TotalFreeSpace,available=d.AvailableFreeSpace;
                    var value=new VolumeReading(id,total,free,available,DateTimeOffset.UtcNow,"Windows DriveInfo; volume capacity");
                    result.Add(value.Valid?value:new(id,Status:"Inconsistent capacity sample; retry at next poll"));
                }catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException){
                    result.Add(new(id,Status:"Capacity unavailable; no zero substitution"));
                }
            }
        }catch(Exception e)when(e is IOException or UnauthorizedAccessException){return [];}
        string system=Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\').ToUpperInvariant()??"C:";
        return result.OrderBy(v=>v.Id==system?0:1).ThenBy(v=>v.Id,StringComparer.Ordinal).ToArray();
    }
}
