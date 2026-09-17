using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;
public sealed record GpuIdentity(string Luid,string Name,uint Vendor,uint Device,double DedicatedGib,double SharedLimitGib);
public sealed record CounterPoint(string Name,double Value);
public sealed record GpuReading(GpuIdentity Identity,double? Usage,double? DedicatedGib,double? SharedGib);
public static class GpuMath
{
    private static readonly Regex Engine=new(@"luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)_eng_(\d+)_",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    public static double? BusyEngine(string luid,IEnumerable<CounterPoint> points)
    {
        var perEngine=new Dictionary<string,double>();
        foreach(var p in points.GroupBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).Select(g=>g.Last())) {
            if(!double.IsFinite(p.Value)||p.Value<0)continue;
            var m=Engine.Match(p.Name);
            if(!m.Success||!string.Equals(m.Groups[1].Value,luid,StringComparison.OrdinalIgnoreCase))continue;
            string key=m.Groups[2].Value+":"+m.Groups[3].Value;
            perEngine[key]=perEngine.GetValueOrDefault(key)+p.Value;
        }
        return perEngine.Count==0?null:Math.Clamp(perEngine.Values.Max(),0,100);
    }
    public static double? Memory(string luid,IEnumerable<CounterPoint> points)
    {
        var valid=points.Where(p=>p.Name.StartsWith("luid_"+luid+"_",StringComparison.OrdinalIgnoreCase)&&double.IsFinite(p.Value)&&p.Value>=0).ToArray();
        return valid.Length==1?valid[0].Value/1073741824d:null;
    }
}
public sealed class WindowsGpuSampler : IDisposable
{
    private IntPtr query,engine,dedicated,shared;private long stamp;
    public GpuIdentity[] Adapters {get;private set;}=[];
    public string Diagnostic {get;private set;}="Not started";
    public WindowsGpuSampler()=>Open();
    public void ResetBaseline()=>Open();
    private void Open()
    {
        Dispose();
        try {
            Adapters=Enumerate();
            if(PdhOpenQueryW(null,UIntPtr.Zero,out query)!=0){Diagnostic="PDH unavailable";return;}
            uint a=PdhAddEnglishCounterW(query,@"\GPU Engine(*)\Utilization Percentage",UIntPtr.Zero,out engine);
            uint b=PdhAddEnglishCounterW(query,@"\GPU Adapter Memory(*)\Dedicated Usage",UIntPtr.Zero,out dedicated);
            uint c=PdhAddEnglishCounterW(query,@"\GPU Adapter Memory(*)\Shared Usage",UIntPtr.Zero,out shared);
            Diagnostic=$"WDDM engine=0x{a:X8}, dedicated=0x{b:X8}, shared=0x{c:X8}";
            PdhCollectQueryData(query);stamp=System.Diagnostics.Stopwatch.GetTimestamp();
        }catch(Exception e)when(e is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException){Diagnostic="WDDM init: "+e.GetType().Name;Dispose();}
    }
    public GpuReading[] Read()
    {
        if(query==IntPtr.Zero)return Adapters.Select(a=>new GpuReading(a,null,null,null)).ToArray();
        var now=System.Diagnostics.Stopwatch.GetTimestamp();
        if(stamp!=0&&System.Diagnostics.Stopwatch.GetElapsedTime(stamp,now).TotalSeconds>10){Open();return Adapters.Select(a=>new GpuReading(a,null,null,null)).ToArray();}
        if(stamp!=0&&System.Diagnostics.Stopwatch.GetElapsedTime(stamp,now).TotalMilliseconds<400)return Adapters.Select(a=>new GpuReading(a,null,null,null)).ToArray();
        stamp=now;
        if(PdhCollectQueryData(query)!=0)return Adapters.Select(a=>new GpuReading(a,null,null,null)).ToArray();
        var e=Values(engine);var d=Values(dedicated);var s=Values(shared);
        return Adapters.Select(a=>new GpuReading(a,GpuMath.BusyEngine(a.Luid,e),GpuMath.Memory(a.Luid,d),GpuMath.Memory(a.Luid,s))).ToArray();
    }
    private static CounterPoint[] Values(IntPtr counter)
    {
        if(counter==IntPtr.Zero)return [];
        const uint format=0x200|0x8000;
        for(int attempt=0;attempt<3;attempt++) {
            uint bytes=0,count=0;
            uint code=PdhGetFormattedCounterArrayW(counter,format,ref bytes,out count,IntPtr.Zero);
            if(code!=0x800007D2||bytes==0||bytes>4*1024*1024)return [];
            IntPtr buffer=Marshal.AllocHGlobal((int)bytes);
            try {
                uint capacity=bytes;code=PdhGetFormattedCounterArrayW(counter,format,ref capacity,out count,buffer);
                if(code==0x800007D2)continue;
                int stride=Marshal.SizeOf<CounterItem>();
                if(code!=0||count>50000||(ulong)count*(uint)stride>bytes)return [];
                var list=new List<CounterPoint>((int)count);
                for(int i=0;i<count;i++) {
                    var item=Marshal.PtrToStructure<CounterItem>(buffer+i*stride);
                    if(item.Value.Status>1||!double.IsFinite(item.Value.Number))continue;
                    var name=Marshal.PtrToStringUni(item.Name);if(name!=null)list.Add(new(name,item.Value.Number));
                }
                return list.ToArray();
            }finally{Marshal.FreeHGlobal(buffer);}
        }
        return [];
    }
    private static GpuIdentity[] Enumerate()
    {
        Guid iid=new("770aae78-f26f-4dba-a829-253c83d1b387");
        int hr=CreateDXGIFactory1(ref iid,out IntPtr factory);Marshal.ThrowExceptionForHR(hr);var found=new List<GpuIdentity>();
        try {
            var enumerate=VTable<EnumAdapters>(factory,12);
            for(uint i=0;i<16;i++) {
                hr=enumerate(factory,i,out IntPtr adapter);if(unchecked((uint)hr)==0x887A0002)break;Marshal.ThrowExceptionForHR(hr);
                try {
                    hr=VTable<GetDesc>(adapter,10)(adapter,out var d);Marshal.ThrowExceptionForHR(hr);if((d.Flags&2)!=0)continue;
                    found.Add(new($"0x{d.High:X8}_0x{d.Low:X8}",d.Description,d.Vendor,d.Device,d.Dedicated.ToUInt64()/1073741824d,d.Shared.ToUInt64()/1073741824d));
                }finally{Marshal.Release(adapter);}
            }
        }finally{Marshal.Release(factory);}
        return found.ToArray();
    }
    private static T VTable<T>(IntPtr o,int slot)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(o),slot*IntPtr.Size));
    public void Dispose(){if(query!=IntPtr.Zero)PdhCloseQuery(query);query=engine=dedicated=shared=IntPtr.Zero;stamp=0;}
    [StructLayout(LayoutKind.Sequential)]private struct CounterValue{public uint Status;public double Number;}
    [StructLayout(LayoutKind.Sequential)]private struct CounterItem{public IntPtr Name;public CounterValue Value;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct AdapterDesc {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Description;
        public uint Vendor,Device,Subsys,Revision;public UIntPtr Dedicated,DedicatedSystem,Shared;public uint Low,High,Flags;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int EnumAdapters(IntPtr self,uint index,out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int GetDesc(IntPtr self,out AdapterDesc desc);
    [DllImport("dxgi.dll")]private static extern int CreateDXGIFactory1(ref Guid iid,out IntPtr factory);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode)]private static extern uint PdhOpenQueryW(string? source,UIntPtr user,out IntPtr query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode)]private static extern uint PdhAddEnglishCounterW(IntPtr query,string path,UIntPtr user,out IntPtr counter);
    [DllImport("pdh.dll")]private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode)]private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter,uint format,ref uint size,out uint count,IntPtr items);
}