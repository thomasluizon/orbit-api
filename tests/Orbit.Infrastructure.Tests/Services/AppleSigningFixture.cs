using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

internal sealed class AppleSigningFixture : IDisposable
{
    private readonly ECDsa _rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 _intermediate;
    private readonly X509Certificate2 _leaf;
    internal X509Certificate2 Root { get; }

    internal AppleSigningFixture()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        var end = start.AddYears(1);
        var rootRequest = Request("CN=Test root", _rootKey, true);
        Root = rootRequest.CreateSelfSigned(start, end);
        var intermediateRequest = Request("CN=Test intermediate", _intermediateKey, true);
        intermediateRequest.CertificateExtensions.Add(new X509Extension("1.2.840.113635.100.6.2.1", [5, 0], false));
        using var intermediate = intermediateRequest.Create(Root, start, end, RandomNumberGenerator.GetBytes(16));
        _intermediate = intermediate.CopyWithPrivateKey(_intermediateKey);
        var leafRequest = Request("CN=Test signer", _leafKey, false);
        leafRequest.CertificateExtensions.Add(new X509Extension("1.2.840.113635.100.6.11.1", [5, 0], false));
        _leaf = leafRequest.Create(_intermediate, start, end, RandomNumberGenerator.GetBytes(16));
    }

    internal AppStoreJwsVerifier Verifier => new(Root.RawData, false, TimeProvider.System);

    internal string Sign(object payload)
    {
        var header = new { alg = "ES256", x5c = new[] { Convert.ToBase64String(_leaf.RawData), Convert.ToBase64String(_intermediate.RawData), Convert.ToBase64String(Root.RawData) } };
        var content = $"{AppleJwt.Encode(JsonSerializer.SerializeToUtf8Bytes(header))}.{AppleJwt.Encode(JsonSerializer.SerializeToUtf8Bytes(payload))}";
        return $"{content}.{AppleJwt.Encode(_leafKey.SignData(Encoding.ASCII.GetBytes(content), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}";
    }

    private static CertificateRequest Request(string subject, ECDsa key, bool ca)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(ca ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
        return request;
    }

    public void Dispose()
    {
        _leaf.Dispose();
        _intermediate.Dispose();
        Root.Dispose();
        _rootKey.Dispose();
        _intermediateKey.Dispose();
        _leafKey.Dispose();
    }
}
