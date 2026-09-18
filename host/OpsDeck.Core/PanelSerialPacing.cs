using System.Text;

namespace OpsDeck.Core;

public static class PanelSerialPacing
{
    public const int Baud=115200;
    public const int LoopDelayMs=5;
    public const int RequestResponseFloorMs=0;
    public const int MinGapMs=8;
    public const int MaxGapMs=300;

    public static int GapMs(string frame,int baud=Baud)
    {
        if(baud<=0)throw new ArgumentOutOfRangeException(nameof(baud));
        int bytes=Encoding.ASCII.GetByteCount(frame??string.Empty)+1;
        long wireMs=(bytes*10_000L+baud-1)/baud;
        return (int)Math.Clamp(wireMs+3,MinGapMs,MaxGapMs);
    }
}
