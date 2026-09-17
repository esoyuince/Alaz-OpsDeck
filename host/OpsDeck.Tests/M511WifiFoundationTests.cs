using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using OpsDeck.Core;
namespace OpsDeck.Tests;

public static class M511WifiFoundationTests
{
    public static void Run(Action<string,bool> check)
    {
        void C(string n,bool ok)=>check("m511-wifi-"+n,ok);
        C("ports",WifiDiscoveryBeacon.DiscoveryPort==47230&&WifiDiscoveryBeacon.FutureTransportPort==47231&&WifiPairing.Port==47231);
        C("default-name",WifiDiscoveryBeacon.SafeHostName(null)=="OPSDECK-PC");
        C("sanitize",WifiDiscoveryBeacon.SafeHostName("PC name/with:*bad") == "PCnamewithbad");
        string longName=new string('A',80);C("name-bound",WifiDiscoveryBeacon.SafeHostName(longName).Length==48);
        string wire=WifiDiscoveryBeacon.Wire("DESKTOP_TEST");
        C("wire-shape",wire=="OPSDECK_HOST_V1|DESKTOP_TEST|47231");
        C("wire-ascii",Encoding.ASCII.GetByteCount(wire)==wire.Length&&wire.All(c=>c is >= ' ' and <= '~'));
        C("wire-budget",Encoding.ASCII.GetByteCount(wire)<128);
        C("wire-no-secret",!wire.Contains("token",StringComparison.OrdinalIgnoreCase)&&!wire.Contains("password",StringComparison.OrdinalIgnoreCase));
        C("bind-interface-name",WifiNetworkBinding.InterfaceMatches("Wi-Fi","Wi-Fi","abc")&&!WifiNetworkBinding.InterfaceMatches("Wi-Fi","Ethernet","abc"));
        C("bind-interface-id",WifiNetworkBinding.InterfaceMatches("abc","Wi-Fi","abc"));
        C("bind-ipv4",WifiNetworkBinding.IsBindableIPv4(System.Net.IPAddress.Parse("192.168.1.77"))&&!WifiNetworkBinding.IsBindableIPv4(System.Net.IPAddress.Any)&&!WifiNetworkBinding.IsBindableIPv4(System.Net.IPAddress.Loopback)&&!WifiNetworkBinding.IsBindableIPv4(System.Net.IPAddress.Parse("169.254.1.2")));
        const string key="00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF",nonce="0123456789ABCDEFFEDCBA9876543210",device="A1B2C3D4E5F6";
        string id=WifiPairing.Id(key);C("pair-id",id.Length==12&&Regex.IsMatch(id,"^[A-F0-9]{12}$")&&id==WifiPairing.Id(key));
        byte[] dummyCert=Enumerable.Range(0,256).Select(i=>(byte)i).ToArray();string dummyFp=WifiTlsIdentity.FingerprintOf(dummyCert);string pairFrame=WifiPairing.PairFrame(key,dummyCert);
        C("pair-frame-v2",pairFrame==$"OPSDECK_PAIR_V2|{id}|{key}|{Convert.ToBase64String(dummyCert)}"&&!pairFrame.Contains("PAIR_V1",StringComparison.Ordinal));
        string clientMac=WifiPairing.AuthMac(key,nonce,device),serverMac=WifiPairing.ServerMac(key,nonce,device);
        C("mutual-domains",clientMac.Length==64&&serverMac.Length==64&&!string.Equals(clientMac,serverMac,StringComparison.Ordinal));
        C("auth-valid",WifiPairing.VerifyAuth(key,nonce,$"OPSDECK_AUTH_V1|{device}|{clientMac}",out string parsed)&&parsed==device);
        C("auth-wrong-nonce",!WifiPairing.VerifyAuth(key,new string('A',32),$"OPSDECK_AUTH_V1|{device}|{clientMac}",out _));
        C("auth-wrong-mac",!WifiPairing.VerifyAuth(key,nonce,$"OPSDECK_AUTH_V1|{device}|{new string('0',64)}",out _));
        string f1=WifiPairing.FrameMac(key,nonce,1,"{\"type\":\"opsdeck.pc.v1\",\"cpu\":42}");
        string f2=WifiPairing.FrameMac(key,nonce,2,"{\"type\":\"opsdeck.pc.v1\",\"cpu\":42}");
        string f3=WifiPairing.FrameMac(key,nonce,1,"{\"type\":\"opsdeck.pc.v1\",\"cpu\":43}");
        C("frame-sequence-bound",f1!=f2);C("frame-content-bound",f1!=f3);
        string s1=WifiPairing.StandbyMac(key,nonce,3,device),s2=WifiPairing.StandbyMac(key,nonce,4,device),s3=WifiPairing.StandbyMac(key,nonce,3,"001122334455");
        C("standby-sequence-bound",s1!=s2);C("standby-device-bound",s1!=s3);
        C("ack-valid",WifiPairing.TryAck($"OPSDECK_PAIR_ACK_V2|{id}|{device}|{dummyFp}",id,dummyFp,out string ackDevice)&&ackDevice==device);
        C("ack-wrong-id",!WifiPairing.TryAck($"OPSDECK_PAIR_ACK_V2|AAAAAAAAAAAA|{device}|{dummyFp}",id,dummyFp,out _));
        C("ack-wrong-pin",!WifiPairing.TryAck($"OPSDECK_PAIR_ACK_V2|{id}|{device}|{new string('0',64)}",id,dummyFp,out _));
        string temp=Path.Combine(Path.GetTempPath(),"opsdeck-wifi-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var settings=new LocalSettings(temp);string saved=settings.GetOrCreateWifiPairingKeyHex(),again=settings.GetOrCreateWifiPairingKeyHex();
            C("dpapi-key-shape",Regex.IsMatch(saved,"^[A-F0-9]{64}$")&&saved==again);
            byte[] raw=File.ReadAllBytes(Path.Combine(temp,"wifi-pairing.dpapi"));C("dpapi-not-plaintext",!Encoding.ASCII.GetString(raw).Contains(saved,StringComparison.Ordinal));
            string fp1,fp2;using(var tls=WifiTlsIdentity.LoadOrCreate(temp)){fp1=tls.Fingerprint;C("tls-cert-ecdsa",tls.Certificate.HasPrivateKey&&tls.Certificate.GetECDsaPrivateKey()!=null);C("tls-cert-size",tls.CertificateDer.Length is >=200 and <=1536);C("tls-pin-shape",Regex.IsMatch(fp1,"^[A-F0-9]{64}$"));C("tls-wide-validity",tls.Certificate.NotBefore.Year<=1970&&tls.Certificate.NotAfter.Year>=2099);}
            using(var tls=WifiTlsIdentity.LoadOrCreate(temp)){fp2=tls.Fingerprint;}C("tls-identity-stable",fp1==fp2);
            byte[] tlsRaw=File.ReadAllBytes(Path.Combine(temp,"wifi-tls-identity.dpapi"));C("tls-private-key-dpapi",tlsRaw.Length>64&&!Encoding.ASCII.GetString(tlsRaw).Contains("ALAZ-OPSDECK",StringComparison.Ordinal));
        }
        finally{Directory.Delete(temp,true);}
    }
}

