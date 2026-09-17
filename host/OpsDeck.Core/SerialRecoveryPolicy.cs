namespace OpsDeck.Core;

public enum SerialRecoveryAction { None, ReopenPort, RomProbeReset }
public enum SerialRecoveryReason { None, DeviceSilent, ForwardStalled }

public readonly record struct SerialRecoveryDecision(SerialRecoveryAction Action,SerialRecoveryReason Reason,int Attempt);

public sealed class SerialRecoveryPolicy
{
    public const long GraceMs=15_000;
    public const long DeviceSilenceMs=20_000;
    public const long ForwardProgressTimeoutMs=15_000;
    public const long RomProbeCooldownMs=300_000; // Legacy/manual tooling only; automatic hardware reset is disabled.
    public const bool AutomaticHardwareResetEnabled=false;
    public const int MinimumFramesSent=6;
    private int unresolvedConnections;
    public int UnresolvedConnections=>unresolvedConnections;
    public bool Recovering=>unresolvedConnections>0;

    public void ObserveForwardProgress()=>unresolvedConnections=0;

    public SerialRecoveryDecision Evaluate(long hostNowMs,long connectionAgeMs,long lastDeviceRxAgeMs,long lastForwardProgressAgeMs,int framesSent)
    {
        if(connectionAgeMs<GraceMs||framesSent<MinimumFramesSent)return new(SerialRecoveryAction.None,SerialRecoveryReason.None,unresolvedConnections);
        if(lastDeviceRxAgeMs<0&&connectionAgeMs<DeviceSilenceMs)return new(SerialRecoveryAction.None,SerialRecoveryReason.None,unresolvedConnections);
        if(lastForwardProgressAgeMs>=0&&lastForwardProgressAgeMs<ForwardProgressTimeoutMs){ObserveForwardProgress();return new(SerialRecoveryAction.None,SerialRecoveryReason.None,0);}
        var reason=lastDeviceRxAgeMs<0||lastDeviceRxAgeMs>DeviceSilenceMs?SerialRecoveryReason.DeviceSilent:SerialRecoveryReason.ForwardStalled;
        int attempt=++unresolvedConnections;
        // Never toggle RTS/DTR or invoke esptool automatically. Reopening COM is non-destructive;
        // hardware resets are an explicit, user-initiated recovery action only.
        return new(SerialRecoveryAction.ReopenPort,reason,attempt);
    }
}
