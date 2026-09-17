namespace OpsDeck.Core;
/// <summary>Only sanitized panel log acknowledgements, scoped to one COM connection.</summary>
public sealed record PanelReceipt(int Pc=0,int Status=0,int Cloud=0,int Inventory=0,
    string LastPc="",string LastStatus="",string LastCloud="",string LastInventory="",int Details=0,string LastDetails="")
{
    public PanelReceipt Observe(string text)
    {
        if(text.Length>255||text.Any(c=>c<32||c>126))return this;
        if(text.StartsWith("DETAILS_RX ",StringComparison.Ordinal))return this with{Details=Details+1,LastDetails=text};
        if(text.StartsWith("PC_RX ",StringComparison.Ordinal))return this with{Pc=Pc+1,LastPc=text};
        if(text.StartsWith("STATUS_RX ",StringComparison.Ordinal))return this with{Status=Status+1,LastStatus=text};
        if(text.StartsWith("CLOUD_RX ",StringComparison.Ordinal))return this with{Cloud=Cloud+1,LastCloud=text};
        if(text.StartsWith("INVENTORY_RX ",StringComparison.Ordinal))return this with{Inventory=Inventory+1,LastInventory=text};
        return this;
    }
}
