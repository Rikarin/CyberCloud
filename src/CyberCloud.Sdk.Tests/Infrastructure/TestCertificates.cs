using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CyberCloud.Sdk.Tests;

/// <summary>Self-signed certificates, made in memory. Nothing here touches a certificate store.</summary>
public static class TestCertificates {
    public static X509Certificate2 CreateRsa() {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest("CN=cybercloud-sdk-tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
