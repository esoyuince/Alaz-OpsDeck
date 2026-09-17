using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
namespace OpsDeck.Core;

public sealed class PcSampler : IDisposable
{
    private readonly HostConfig config;
    private string? intelLuid,nvidiaLuid; private readonly WindowsGpuSampler windows=new(); private readonly IntelSensors sensors; private readonly OmenSensors omen=new(); private ThermalReading thermal=new(); private OmenReading omenThermal=new(); private long sensorStamp,nvidiaStamp; private double? nvidiaTemp;
    private VolumeReading[] volumes=[];private long volumeStamp;
    private ulong idle, kernel, user;
    private bool initialized;
    private long prevStamp, rx, tx;
    private string? nicId;
    public PcSampler(HostConfig config){this.config=config;sensors=new(windows.Adapters.Any(a=>a.Vendor==0x8086)?1:0);}
    public PcSample Sample()
    {
        double? cpu=null,ram=null,totalRam=null,down=null,up=null;
        if(GetSystemTimes(out var i,out var k,out var u)) {
            if(initialized && k>=kernel && u>=user && i>=idle) {
                double total=(double)(k-kernel)+(u-user); double dIdle=i-idle;
                if(total>0&&dIdle<=total)cpu=100*(total-dIdle)/total;
            }
            idle=i;kernel=k;user=u;initialized=true;
        }
        MemoryStatus m=new(){Length=(uint)Marshal.SizeOf<MemoryStatus>()};
        if(GlobalMemoryStatusEx(ref m)&&m.Available<=m.Total){ram=(m.Total-m.Available)/1073741824d;totalRam=m.Total/1073741824d;}
        long stamp=Stopwatch.GetTimestamp();
        try {
            var nic=NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n=>(n.Name==config.NetworkInterface||n.Id==config.NetworkInterface)&&n.OperationalStatus==OperationalStatus.Up);
            if(nic!=null){
                var counters=nic.GetIPStatistics();
                double elapsed=Stopwatch.GetElapsedTime(prevStamp,stamp).TotalSeconds;
                if(prevStamp!=0&&nicId==nic.Id&&elapsed>0&&elapsed<10&&counters.BytesReceived>=rx&&counters.BytesSent>=tx){
                    down=8*(counters.BytesReceived-rx)/elapsed/1e6;up=8*(counters.BytesSent-tx)/elapsed/1e6;
                }
                rx=counters.BytesReceived;tx=counters.BytesSent;nicId=nic.Id;prevStamp=stamp;
            } else {nicId=null;prevStamp=0;}
        } catch(NetworkInformationException){nicId=null;prevStamp=0;}
        var gpus=windows.Read();var intel=gpus.Where(g=>g.Identity.Vendor==0x8086).ToArray();var nv=gpus.Where(g=>g.Identity.Vendor==0x10DE).ToArray();
        GpuReading? ig=SelectMeasured(intel,ref intelLuid),ng=SelectMeasured(nv,ref nvidiaLuid);
        if(sensorStamp==0||Stopwatch.GetElapsedTime(sensorStamp,stamp).TotalSeconds>=5){thermal=sensors.Read();sensorStamp=stamp;}
        omenThermal=omen.Read();
        bool pollNv=ng!=null&&(config.DetailedNvidiaSensors||ng.Usage>0.5);string nvStatus="NVML not polled while idle; not a sleep-state claim";
        if(pollNv){if(nvidiaStamp==0||Stopwatch.GetElapsedTime(nvidiaStamp,stamp).TotalSeconds>=5){using var nvml=new Nvml(config.GpuIndex);nvidiaTemp=nvml.Read().Temp;nvidiaStamp=stamp;}nvStatus=nvidiaTemp.HasValue?"NVML temperature (up to 5s old)":"NVML temperature unavailable";}
        else{nvidiaTemp=null;nvidiaStamp=0;}
        if(volumeStamp==0||Stopwatch.GetElapsedTime(volumeStamp,stamp).TotalSeconds>=10){volumes=DiskTelemetry.Read();volumeStamp=Stopwatch.GetTimestamp();}
        double? fan1=omenThermal.Fan1Rpm??thermal.Fan1,fan2=omenThermal.Fan2Rpm??thermal.Fan2;
        string fanStatus=omenThermal.Fan1Rpm.HasValue&&omenThermal.Fan2Rpm.HasValue?omenThermal.Status:thermal.FanStatus+"; "+omenThermal.Status;
        return new(Cpu:cpu,Gpu:ng?.Usage,GpuTemp:nvidiaTemp,RamUsedGib:ram,RamTotalGib:totalRam,VramUsedGib:ng?.DedicatedGib,VramTotalGib:ng?.Identity.DedicatedGib,RxMbps:down,TxMbps:up,IntelGpu:ig?.Usage,IntelTemp:thermal.IntelTemp,IntelSharedGib:ig?.SharedGib,IntelSharedLimitGib:ig?.Identity.SharedLimitGib,Fan1Rpm:fan1,Fan2Rpm:fan2,IntelSensorStatus:thermal.IntelStatus,FanSensorStatus:fanStatus,NvidiaSensorStatus:nvStatus,Gpus:gpus,CpuTemp:omenThermal.CpuTemp,CpuSensorStatus:omenThermal.Status,Volumes:volumes,ChassisTemp:omenThermal.ChassisTemp,ChassisSensorStatus:omenThermal.Status);
    }
    public static GpuReading? SelectMeasured(GpuReading[] candidates,ref string? selectedLuid)
    {
        // USB/indirect display drivers can publish render-only aliases with the same Intel name.
        string? id=selectedLuid;var saved=candidates.FirstOrDefault(g=>g.Identity.Luid==id);
        if(saved!=null)return saved;
        var measured=candidates.Where(g=>g.Usage.HasValue||g.DedicatedGib.HasValue||g.SharedGib.HasValue).ToArray();
        var chosen=measured.Length==1?measured[0]:candidates.Length==1?candidates[0]:null;
        if(chosen!=null)selectedLuid=chosen.Identity.Luid;return chosen;
    }
    public void ResetBaseline()
    {
        initialized=false;prevStamp=0;nicId=null;sensorStamp=0;nvidiaStamp=0;volumeStamp=0;volumes=[];
        nvidiaTemp=null;thermal=new();omenThermal=new();omen.ResetBaseline();intelLuid=null;nvidiaLuid=null;windows.ResetBaseline();
    }
    public void Dispose(){omen.Dispose();sensors.Dispose();windows.Dispose();}
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)]private struct MemoryStatus {
        public uint Length,Load;public ulong Total,Available,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,Extended;
    }
}
internal sealed class Nvml : IDisposable
{
    private IntPtr lib,device;
    private Shutdown? shutdown;
    private Util? util;private Temp? temp;private Mem? mem;
    private bool initialized;
    public Nvml(uint index)
    {
        string[] paths=[Path.Combine(Environment.SystemDirectory,"nvml.dll"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"NVIDIA Corporation","NVSMI","nvml.dll")];
        foreach(var path in paths)if(File.Exists(path)&&NativeLibrary.TryLoad(path,out lib))break;
        if(lib==IntPtr.Zero)return;
        try {
            var init=Get<Init>("nvmlInit_v2");shutdown=Get<Shutdown>("nvmlShutdown");
            initialized=init()==0;
            if(!initialized||Get<Handle>("nvmlDeviceGetHandleByIndex_v2")(index,out device)!=0){device=IntPtr.Zero;return;}
            util=Get<Util>("nvmlDeviceGetUtilizationRates");temp=Get<Temp>("nvmlDeviceGetTemperature");mem=Get<Mem>("nvmlDeviceGetMemoryInfo");
        } catch(EntryPointNotFoundException){Dispose();}
    }
    private T Get<T>(string name)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib,name));
    public (double? Load,double? Temp,double? Used,double? Total) Read()
    {
        double? load=null,temperature=null,used=null,total=null;
        if(device==IntPtr.Zero)return(load,temperature,used,total);
        if(util!=null&&util(device,out var u)==0&&u.Gpu<=100)load=u.Gpu;
        if(temp!=null&&temp(device,0,out var t)==0&&t<=150)temperature=t;
        if(mem!=null&&mem(device,out var m)==0&&m.Total>0&&m.Used<=m.Total&&m.Total<1099511627776UL){used=m.Used/1073741824d;total=m.Total/1073741824d;}
        return(load,temperature,used,total);
    }
    public void Dispose(){device=IntPtr.Zero;if(initialized){shutdown?.Invoke();initialized=false;}if(lib!=IntPtr.Zero){NativeLibrary.Free(lib);lib=IntPtr.Zero;}}
    [StructLayout(LayoutKind.Sequential)]private struct Utilization{public uint Gpu,Memory;}
    [StructLayout(LayoutKind.Sequential)]private struct Memory{public ulong Total,Free,Used;}
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Handle(uint index,out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Util(IntPtr device,out Utilization util);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Temp(IntPtr device,uint sensor,out uint temp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Mem(IntPtr device,out Memory memory);
}
