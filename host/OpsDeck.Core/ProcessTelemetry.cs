using System.Diagnostics;
using System.Text.Json;

namespace OpsDeck.Core;

public sealed record ProcessRow(int Pid,string Name,double CpuPercent,double RamMiB);
public sealed record ProcessSnapshot(DateTimeOffset? CollectedAt,int TotalCount,ProcessRow[] Rows)
{
    public const int PanelLimit=12;
    public static ProcessSnapshot Empty=>new(null,0,[]);
    public string Wire()=>JsonSerializer.Serialize(new{
        type="opsdeck.processes.v1",
        total=TotalCount,
        rows=Rows.Take(PanelLimit).Select(x=>new{pid=x.Pid,name=x.Name,cpu=Math.Round(x.CpuPercent,1),ram_mib=Math.Round(x.RamMiB,1)})
    },Json.Options);
}

public sealed class ProcessSampler
{
    private sealed record Baseline(TimeSpan Cpu,DateTimeOffset At);
    private readonly Dictionary<int,Baseline> baselines=[];
    private readonly int sessionId;
    public ProcessSampler(){using var self=Process.GetCurrentProcess();sessionId=self.SessionId;}

    public ProcessSnapshot Sample()
    {
        var now=DateTimeOffset.UtcNow;var rows=new List<ProcessRow>();var seen=new HashSet<int>();int total=0;
        foreach(var p in Process.GetProcesses())
        {
            using(p)
            {
                try
                {
                    if(p.SessionId!=sessionId)continue;string name=SafeName(p.ProcessName);if(name.Length==0)continue;
                    p.Refresh();int pid=p.Id;seen.Add(pid);total++;
                    TimeSpan cpu=p.TotalProcessorTime;double pct=0;
                    if(baselines.TryGetValue(pid,out var old))
                    {
                        double elapsed=(now-old.At).TotalMilliseconds;
                        if(elapsed>=250)pct=Math.Clamp((cpu-old.Cpu).TotalMilliseconds/elapsed/Math.Max(1,Environment.ProcessorCount)*100.0,0,100);
                    }
                    baselines[pid]=new(cpu,now);double ram=Math.Max(0,p.WorkingSet64)/1048576.0;
                    rows.Add(new(pid,name,pct,ram));
                }
                catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException){}
            }
        }
        foreach(int pid in baselines.Keys.Where(x=>!seen.Contains(x)).ToArray())baselines.Remove(pid);
        var top=rows.OrderByDescending(x=>x.CpuPercent).ThenByDescending(x=>x.RamMiB).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).Take(ProcessSnapshot.PanelLimit).ToArray();
        return new(now,total,top);
    }

    private static string SafeName(string value)
    {
        if(string.IsNullOrWhiteSpace(value))return "";Span<char> b=stackalloc char[Math.Min(31,value.Length)];int n=0;
        foreach(char c in value){if(n>=b.Length)break;b[n++]=c is >= ' ' and <= '~'?c:'?';}
        return new string(b[..n]);
    }
}
