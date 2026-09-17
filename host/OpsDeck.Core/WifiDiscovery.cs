using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
namespace OpsDeck.Core;

public static class WifiNetworkBinding
{
    public static bool InterfaceMatches(string configured,string name,string id)
        =>!string.IsNullOrWhiteSpace(configured)&&(string.Equals(configured,name,StringComparison.OrdinalIgnoreCase)||string.Equals(configured,id,StringComparison.OrdinalIgnoreCase));
    public static bool IsBindableIPv4(IPAddress address)
    {
        if(address.AddressFamily!=AddressFamily.InterNetwork||IPAddress.IsLoopback(address)||address.Equals(IPAddress.Any)||address.Equals(IPAddress.Broadcast))return false;
        byte[] b=address.GetAddressBytes();
        return !(b[0]==169&&b[1]==254)&&b[0]<224;
    }
    public static IPAddress? ResolveIPv4(string configured)
    {
        if(string.IsNullOrWhiteSpace(configured))return null;
        try
        {
            foreach(var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if(nic.OperationalStatus!=OperationalStatus.Up||!InterfaceMatches(configured,nic.Name,nic.Id))continue;
                foreach(var u in nic.GetIPProperties().UnicastAddresses)
                    if(IsBindableIPv4(u.Address))return u.Address;
            }
        }
        catch(NetworkInformationException){}
        return null;
    }
}

public static class WifiDiscoveryBeacon
{
    public const int DiscoveryPort=47230;
    public const int FutureTransportPort=47231;
    public static string SafeHostName(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return "OPSDECK-PC";
        var chars=value.Where(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.').Take(48).ToArray();
        return chars.Length==0?"OPSDECK-PC":new string(chars);
    }
    public static string Wire(string? machineName)
        =>$"OPSDECK_HOST_V1|{SafeHostName(machineName)}|{FutureTransportPort}";
    public static async Task Run(CancellationToken ct)
    {
        using var udp=new UdpClient(AddressFamily.InterNetwork){EnableBroadcast=true};
        var endpoint=new IPEndPoint(IPAddress.Broadcast,DiscoveryPort);
        byte[] bytes=Encoding.ASCII.GetBytes(Wire(Environment.MachineName));
        while(!ct.IsCancellationRequested)
        {
            try{await udp.SendAsync(bytes,bytes.Length,endpoint);}
            catch(SocketException){}
            await Task.Delay(TimeSpan.FromSeconds(2),ct);
        }
    }
}
