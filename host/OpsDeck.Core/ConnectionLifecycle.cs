using System.Diagnostics;
namespace OpsDeck.Core;

public sealed partial class AppEngine
{
    private int serialPaused, powerSuspended, disposed, started;
    private long resetEpoch;
    public bool SerialPaused
    {
        get => Volatile.Read(ref serialPaused) != 0;
        set => Volatile.Write(ref serialPaused, value ? 1 : 0);
    }
    public bool PowerSuspended => Volatile.Read(ref powerSuspended) != 0;
    public bool PcIsFresh => Interlocked.Read(ref pcStamp) != 0 &&
        !PowerSuspended && Stopwatch.GetElapsedTime(Interlocked.Read(ref pcStamp)).TotalSeconds < 3;
    public double? PcAgeSeconds => Interlocked.Read(ref pcStamp) == 0 ? null :
        Stopwatch.GetElapsedTime(Interlocked.Read(ref pcStamp)).TotalSeconds;
    public string PcState => PowerSuspended ? "Askıda" : PcIsFresh ? "Canlı" : "Güncel veri yok";

    public void SuspendObservation()
    {
        Volatile.Write(ref powerSuspended, 1);
        Interlocked.Exchange(ref pcStamp, 0);
        Interlocked.Increment(ref resetEpoch);
        InvalidateProviders();
        log.Event("power_suspend_observed");
    }
    public void ResumeObservation()
    {
        Interlocked.Exchange(ref pcStamp, 0);
        InvalidateProviders();
        Interlocked.Increment(ref resetEpoch);
        Volatile.Write(ref powerSuspended, 0);
        log.Event("power_resume_rebaseline_requested");
    }

    private void InvalidateProviders()
    {
        static Metric Old(Metric m) => m.Value.HasValue ? m with { State=SourceState.Stale } : m;
        lock (gate)
        {
            fleet=fleet with {
                Agents=fleet.Agents with {CodexState=SourceState.Stale,BridgeState=SourceState.Stale},
                Workers=Old(fleet.Workers), D1=Old(fleet.D1), R2=Old(fleet.R2),
                Hosting=Old(fleet.Hosting), Cost=Old(fleet.Cost)
            };
            accounts=accounts.Select(a=>a with {Workers=Old(a.Workers),D1=Old(a.D1),
                R2=Old(a.R2),Hosting=Old(a.Hosting),Cost=Old(a.Cost)}).ToArray();
        }
    }
}
