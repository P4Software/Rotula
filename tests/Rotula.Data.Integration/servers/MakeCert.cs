// Creates a throw-away self-signed TLS certificate and key (PEM) for the test servers.
// Run by Start-TestServers.ps1 as:  dotnet run MakeCert.cs -- <output folder>
// Why a C# file instead of PowerShell: New-SelfSignedCertificate writes into the Windows certificate store (the registry), and
// Windows PowerShell 5.1 cannot export a PEM key. This writes two files and touches nothing else.
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

if (args.Length != 1) { Console.Error.WriteLine("usage: MakeCert <output folder>"); return 2; }
string dir = args[0];
Directory.CreateDirectory(dir);

using var rsa = RSA.Create(2048);
var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

// Names the client checks with VerifyFull: both the loopback address and "localhost", so a refusal in a test is about TRUST,
// never about a host-name mismatch.
var san = new SubjectAlternativeNameBuilder();
san.AddIpAddress(IPAddress.Loopback);
san.AddDnsName("localhost");
request.CertificateExtensions.Add(san.Build());
// CA:true so the same file can be handed to a client as its "certificate authority file" (the server certificate is its own root).
request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
request.CertificateExtensions.Add(new X509KeyUsageExtension(
    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, true));
request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
File.WriteAllText(Path.Combine(dir, "server.crt"), cert.ExportCertificatePem());
File.WriteAllText(Path.Combine(dir, "server.key"), rsa.ExportPkcs8PrivateKeyPem());
Console.WriteLine("certificate written to " + dir);
return 0;
