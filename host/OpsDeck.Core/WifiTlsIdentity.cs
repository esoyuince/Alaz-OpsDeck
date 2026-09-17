using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpsDeck.Core;

public sealed class WifiTlsIdentity : IDisposable
{
    private const string FileName="wifi-tls-identity.dpapi";
    public X509Certificate2 Certificate{get;}
    public byte[] CertificateDer{get;}
    public string Fingerprint{get;}
    public string CertificateBase64=>Convert.ToBase64String(CertificateDer);

    private WifiTlsIdentity(X509Certificate2 certificate)
    {
        Certificate=certificate;
        CertificateDer=certificate.Export(X509ContentType.Cert);
        Fingerprint=FingerprintOf(CertificateDer);
    }

    public static string FingerprintOf(ReadOnlySpan<byte> certificateDer)
        =>Convert.ToHexString(SHA256.HashData(certificateDer));

    public static WifiTlsIdentity LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,FileName);
        if(File.Exists(path))return Load(path);
        return Create(path);
    }
    private static WifiTlsIdentity Load(string path)
    {
        byte[] protectedBytes=File.ReadAllBytes(path);
        byte[] pfx=ProtectedData.Unprotect(protectedBytes,null,DataProtectionScope.CurrentUser);
        try
        {
            var certificate=X509CertificateLoader.LoadPkcs12(pfx,ReadOnlySpan<char>.Empty,X509KeyStorageFlags.UserKeySet|X509KeyStorageFlags.Exportable);
            if(!certificate.HasPrivateKey||certificate.GetECDsaPrivateKey()==null)throw new CryptographicException("Saved Wi-Fi TLS identity is invalid.");
            return new WifiTlsIdentity(certificate);
        }
        finally{CryptographicOperations.ZeroMemory(pfx);}
    }

    private static WifiTlsIdentity Create(string path)
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request=new CertificateRequest("CN=ALAZ-OPSDECK",key,HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyCertSign,true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey,false));
        var eku=new OidCollection{new("1.3.6.1.5.5.7.3.1")};
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku,true));
        using var created=request.CreateSelfSigned(new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero),new DateTimeOffset(2099,12,31,23,59,59,TimeSpan.Zero));
        byte[] pfx=created.Export(X509ContentType.Pkcs12,"");
        try
        {
            byte[] protectedBytes=ProtectedData.Protect(pfx,null,DataProtectionScope.CurrentUser);
            Atomic(path,protectedBytes);
        }
        finally{CryptographicOperations.ZeroMemory(pfx);}
        return Load(path);
    }
    private static void Atomic(string path,byte[] bytes)
    {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }

    public void Dispose()
    {
        Certificate.Dispose();
        CryptographicOperations.ZeroMemory(CertificateDer);
    }
}

