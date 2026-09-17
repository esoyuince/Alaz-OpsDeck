using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;

public static class WifiPairing
{
    public const int Port=47231;
    private static readonly Regex DeviceRx=new("^[A-F0-9]{12}$",RegexOptions.CultureInvariant);
    public static string Id(string keyHex)
    {
        byte[] key=Convert.FromHexString(keyHex);
        try{return Convert.ToHexString(SHA256.HashData(key))[..12];}
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    public static string PairFrame(string keyHex,byte[] certificateDer)
    {
        if(certificateDer.Length is <200 or >1536)throw new ArgumentException("TLS certificate size is outside pairing budget.",nameof(certificateDer));
        return $"OPSDECK_PAIR_V2|{Id(keyHex)}|{keyHex}|{Convert.ToBase64String(certificateDer)}";
    }
    public static bool TryAck(string line,string expectedId,string expectedFingerprint,out string device)
    {
        device="";var p=line.Split('|');
        if(p.Length!=4||p[0]!="OPSDECK_PAIR_ACK_V2"||!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p[1]),Encoding.ASCII.GetBytes(expectedId))||!DeviceRx.IsMatch(p[2])||p[3].Length!=64)return false;
        if(!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p[3]),Encoding.ASCII.GetBytes(expectedFingerprint)))return false;
        device=p[2];return true;
    }
    private static string Mac(string keyHex,string message)
    {
        byte[] key=Convert.FromHexString(keyHex),msg=Encoding.UTF8.GetBytes(message);
        try{return Convert.ToHexString(HMACSHA256.HashData(key,msg));}
        finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(msg);}
    }
    public static string AuthMac(string keyHex,string nonce,string device)=>Mac(keyHex,$"OPSDECK_AUTH_V1|{nonce}|{device}");
    public static string ServerMac(string keyHex,string nonce,string device)=>Mac(keyHex,$"OPSDECK_SERVER_V1|{nonce}|{device}");
    public static string FrameMac(string keyHex,string nonce,long sequence,string frame)=>Mac(keyHex,$"OPSDECK_FRAME_V1|{nonce}|{sequence}|{frame}");
    public static string StandbyMac(string keyHex,string nonce,long sequence,string device)=>Mac(keyHex,$"OPSDECK_STANDBY_V2|{nonce}|{sequence}|{device}");
    public static bool VerifyAuth(string keyHex,string nonce,string line,out string device)
    {
        device="";var p=line.Split('|');
        if(p.Length!=3||p[0]!="OPSDECK_AUTH_V1"||!DeviceRx.IsMatch(p[1])||p[2].Length!=64)return false;
        string expected=AuthMac(keyHex,nonce,p[1]);
        try
        {
            byte[] a=Convert.FromHexString(expected),b=Convert.FromHexString(p[2]);
            try{if(a.Length!=b.Length||!CryptographicOperations.FixedTimeEquals(a,b))return false;}
            finally{CryptographicOperations.ZeroMemory(a);CryptographicOperations.ZeroMemory(b);}
        }
        catch(FormatException){return false;}
        device=p[1];return true;
    }
}

public sealed class WifiTelemetryServer(IPAddress bindAddress,string keyHex,X509Certificate2 certificate,Func<bool> usbPrimary,Func<string[]> frames,SafeLog log)
{
    private const int MaxConcurrentSessions=8;
    private static readonly TimeSpan AuthTimeout=TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WriteTimeout=TimeSpan.FromSeconds(2);
    public async Task Run(CancellationToken ct)
    {
        if(!WifiNetworkBinding.IsBindableIPv4(bindAddress))throw new ArgumentException("Telemetry bind address must be a usable IPv4 address.",nameof(bindAddress));
        var listener=new TcpListener(bindAddress,WifiPairing.Port);listener.Start(MaxConcurrentSessions);log.Event("wifi_telemetry_listen",new{bind_address=bindAddress.ToString(),port=WifiPairing.Port,mode="read_only",max_sessions=MaxConcurrentSessions,chat_over_wifi=false});
        var sessions=new List<Task>();
        try
        {
            while(!ct.IsCancellationRequested)
            {
                sessions.RemoveAll(t=>t.IsCompleted);
                TcpClient client=await listener.AcceptTcpClientAsync(ct);client.NoDelay=true;
                if(sessions.Count>=MaxConcurrentSessions){log.Event("wifi_telemetry_busy",new{remote=client.Client.RemoteEndPoint?.ToString()??"unknown"});client.Dispose();continue;}
                sessions.Add(HandleClient(client,ct));
            }
        }
        finally
        {
            listener.Stop();
            if(sessions.Count>0)try{await Task.WhenAll(sessions);}catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        }
    }
    private async Task HandleClient(TcpClient client,CancellationToken ct)
    {
        using(client)
        try{await Handle(client,ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch(OperationCanceledException){log.Event("wifi_telemetry_auth_timeout",new{remote=client.Client.RemoteEndPoint?.ToString()??"unknown"});}
        catch(AuthenticationException e){log.Event("wifi_tls_failed",new{detail=SafeTlsDetail(e.Message)});}
        catch(Exception e)when(e is IOException or SocketException or InvalidDataException){log.Event("wifi_telemetry_disconnect",new{kind=e.GetType().Name});}
    }
    private static string SafeTlsDetail(string value)=>new(value.Where(c=>c is >= ' ' and <= '~').Take(160).ToArray());
    private static async Task<string?> ReadLineBounded(StreamReader reader,int max,CancellationToken ct)
    {
        var b=new StringBuilder(Math.Min(max,128));char[] one=new char[1];
        while(b.Length<max){int n=await reader.ReadAsync(one.AsMemory(0,1),ct);if(n==0)return b.Length==0?null:b.ToString();char c=one[0];if(c=='\n')return b.ToString();if(c!='\r'&&c>=' '&&c<='~')b.Append(c);else if(c!='\r')throw new InvalidDataException("Invalid auth byte");}
        throw new InvalidDataException("Auth line exceeds protocol budget");
    }
    private static async Task WriteLineBounded(StreamWriter writer,string value,CancellationToken ct)
    {
        using var writeCts=CancellationTokenSource.CreateLinkedTokenSource(ct);writeCts.CancelAfter(WriteTimeout);
        await writer.WriteLineAsync(value.AsMemory(),writeCts.Token);
    }
    private async Task Handle(TcpClient client,CancellationToken ct)
    {
        string remote=client.Client.RemoteEndPoint?.ToString()??"unknown";
        long tlsStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        using NetworkStream network=client.GetStream();using var stream=new SslStream(network,false);
        using(var tlsCts=CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            tlsCts.CancelAfter(TimeSpan.FromSeconds(5));
            var tlsOptions=new SslServerAuthenticationOptions{ServerCertificate=certificate,ClientCertificateRequired=false,EnabledSslProtocols=SslProtocols.Tls12,CertificateRevocationCheckMode=X509RevocationMode.NoCheck};
            await stream.AuthenticateAsServerAsync(tlsOptions,tlsCts.Token);
        }
        long tlsElapsedMs=(long)System.Diagnostics.Stopwatch.GetElapsedTime(tlsStarted).TotalMilliseconds;
        log.Event("wifi_tls_established",new{remote,protocol=stream.SslProtocol.ToString(),cipher=stream.NegotiatedCipherSuite.ToString(),elapsed_ms=tlsElapsedMs});
        long authStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        var utf8=new UTF8Encoding(false);using var reader=new StreamReader(stream,utf8,false,512,true);using var writer=new StreamWriter(stream,utf8,512,true){AutoFlush=true,NewLine="\n"};
        string nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(16));await WriteLineBounded(writer,$"OPSDECK_CHALLENGE_V1|{nonce}",ct);
        using var authCts=CancellationTokenSource.CreateLinkedTokenSource(ct);authCts.CancelAfter(AuthTimeout);
        string? auth=await ReadLineBounded(reader,256,authCts.Token);
        string device="";bool authenticated=auth!=null&&WifiPairing.VerifyAuth(keyHex,nonce,auth,out device);
        if(!authenticated){log.Event("wifi_telemetry_auth_failed",new{remote});return;}
        string serverProof=WifiPairing.ServerMac(keyHex,nonce,device);
        await WriteLineBounded(writer,$"OPSDECK_AUTH_OK_V1|{serverProof}",ct);
        long authElapsedMs=(long)System.Diagnostics.Stopwatch.GetElapsedTime(authStarted).TotalMilliseconds;
        log.Event("wifi_telemetry_auth_ok",new{remote,device,auth_elapsed_ms=authElapsedMs});bool? lastStandby=null;long frameSequence=0;
        while(!ct.IsCancellationRequested&&client.Connected)
        {
            bool standby=usbPrimary();
            if(lastStandby!=standby){lastStandby=standby;log.Event("wifi_telemetry_mode",new{device,mode=standby?"standby_usb_primary":"active_wifi_read_only"});}
            if(standby)
            {
                long seq=++frameSequence;string mac=WifiPairing.StandbyMac(keyHex,nonce,seq,device);
                await WriteLineBounded(writer,$"OPSDECK_STANDBY_V2|{seq}|{mac}",ct);
            }
            else foreach(string frame in frames())
            {
                if(Encoding.UTF8.GetByteCount(frame)>3000)throw new InvalidDataException("Wi-Fi telemetry frame exceeds protocol budget");
                long seq=++frameSequence;string frameMac=WifiPairing.FrameMac(keyHex,nonce,seq,frame);
                await WriteLineBounded(writer,$"OPSDECK_FRAME_V1|{seq}|{frameMac}",ct);await WriteLineBounded(writer,frame,ct);
            }
            await Task.Delay(TimeSpan.FromSeconds(1),ct);
        }
    }
}
