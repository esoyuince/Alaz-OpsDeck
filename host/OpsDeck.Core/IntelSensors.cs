using System.Runtime.InteropServices;
namespace OpsDeck.Core;
public sealed record ThermalReading(double? IntelTemp=null,double? Fan1=null,double? Fan2=null,string IntelStatus="unavailable",string FanStatus="unavailable");
public sealed class IntelSensors : IDisposable
{
    private IntPtr lib,api;private IntPtr[] temperatures=[],fans=[];
    private Close? close;private ReadTemp? readTemp;private ReadFan? readFan;
    public string IntelStatus{get;private set;}="IGCL unavailable";
    public string FanStatus{get;private set;}="IGCL unavailable; OEM sensors need separate access";
    public IntelSensors(int intelAdapterCount)
    {
        if(intelAdapterCount!=1){IntelStatus="Intel adapter mapping ambiguous";return;}
        string path=Path.Combine(Environment.SystemDirectory,"ControlLib.dll");if(!File.Exists(path)||!NativeLibrary.TryLoad(path,out lib))return;
        try {
            close=Get<Close>("ctlClose");Args args=new(){Size=(uint)Marshal.SizeOf<Args>(),AppVersion=0x10000,Flags=1};
            int code=Get<Init>("ctlInit")(ref args,out api);if(code!=0){api=IntPtr.Zero;IntelStatus=$"IGCL init 0x{code:X8}";return;}
            var devices=Enumerate(Get<Enum>("ctlEnumerateDevices"),api);if(devices.Length!=1){IntelStatus="IGCL mapping ambiguous";return;}
            readTemp=Get<ReadTemp>("ctlTemperatureGetState");readFan=Get<ReadFan>("ctlFanGetState");var props=Get<Props>("ctlTemperatureGetProperties");
            temperatures=Enumerate(Get<Enum>("ctlEnumTemperatureSensors"),devices[0]).Where(h=>{TempProps p=new(){Size=(uint)Marshal.SizeOf<TempProps>()};return props(h,ref p)==0&&p.Type==1;}).ToArray();
            fans=Enumerate(Get<Enum>("ctlEnumFans"),devices[0]);
            IntelStatus=temperatures.Length>0?"IGCL GPU temperature":"IGCL exposes no GPU temperature sensor";
            FanStatus=fans.Length>0?"IGCL actual RPM":"IGCL exposes no fans; OEM access not enabled";
        }catch(Exception e)when(e is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException){IntelStatus="IGCL API unsupported";Dispose();}
    }
    private static IntPtr[] Enumerate(Enum fn,IntPtr parent)
    {uint count=0;if(fn(parent,ref count,null)!=0||count==0||count>16)return [];var handles=new IntPtr[count];if(fn(parent,ref count,handles)!=0||count>handles.Length)return [];return handles.Take((int)count).ToArray();}
    public ThermalReading Read()
    {
        var ts=new List<double>();double?[] fs=new double?[2];
        if(api!=IntPtr.Zero){foreach(var h in temperatures)if(readTemp!=null&&readTemp(h,out var t)==0&&double.IsFinite(t)&&t>=0&&t<=150)ts.Add(t);
            for(int i=0;i<Math.Min(fans.Length,2);i++)if(readFan!=null&&readFan(fans[i],0,out var rpm)==0&&rpm>=0&&rpm<=30000)fs[i]=rpm;}
        return new(ts.Count>0?ts.Max():null,fs[0],fs[1],temperatures.Length>0&&ts.Count==0?"IGCL reading unavailable":IntelStatus,fans.Length>0&&fs.All(x=>!x.HasValue)?"IGCL RPM unavailable":FanStatus);
    }
    private T Get<T>(string name)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib,name));
    public void Dispose(){if(api!=IntPtr.Zero){close?.Invoke(api);api=IntPtr.Zero;}if(lib!=IntPtr.Zero){NativeLibrary.Free(lib);lib=IntPtr.Zero;}}
    [StructLayout(LayoutKind.Sequential)]private struct Args{public uint Size;public byte Version;public uint AppVersion,Flags,SupportedVersion;public Guid Id;}
    [StructLayout(LayoutKind.Sequential)]private struct TempProps{public uint Size;public byte Version;public uint Type;public double Max;}
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Init(ref Args args,out IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Close(IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Enum(IntPtr p,ref uint count,[Out]IntPtr[]? handles);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Props(IntPtr h,ref TempProps p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int ReadTemp(IntPtr h,out double temp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int ReadFan(IntPtr h,int units,out int rpm);
}