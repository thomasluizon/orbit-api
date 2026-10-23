using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Services;

public sealed class AppStoreJwsVerifier
{
    private readonly byte[] _rootCertificate;
    private readonly bool _checkRevocation;
    private readonly TimeProvider _time;

    public AppStoreJwsVerifier() : this(ReadAppleRoot(), true, TimeProvider.System) { }

    internal AppStoreJwsVerifier(byte[] rootCertificate, bool checkRevocation, TimeProvider time)
    {
        _rootCertificate = rootCertificate;
        _checkRevocation = checkRevocation;
        _time = time;
    }

    public JsonDocument Verify(string signedPayload)
    {
        try
        {
            var parts = signedPayload.Split('.');
            if (parts.Length != 3 || signedPayload.Length > 100_000)
                throw new AppleVerificationException();
            using var header = JsonDocument.Parse(AppleJwt.Decode(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "ES256")
                throw new AppleVerificationException();
            var certificates = header.RootElement.GetProperty("x5c").EnumerateArray().ToArray();
            if (certificates.Length != 3)
                throw new AppleVerificationException();
            using var root = X509CertificateLoader.LoadCertificate(_rootCertificate);
            using var leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificates[0].GetString()!));
            using var intermediate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificates[1].GetString()!));
            using var suppliedRoot = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificates[2].GetString()!));
            if (!root.RawData.AsSpan().SequenceEqual(suppliedRoot.RawData)
                || !leaf.Extensions.Any(extension => extension.Oid?.Value == "1.2.840.113635.100.6.11.1")
                || !intermediate.Extensions.Any(extension => extension.Oid?.Value == "1.2.840.113635.100.6.2.1"))
                throw new AppleVerificationException();
            using var payload = JsonDocument.Parse(AppleJwt.Decode(parts[1]));
            var signedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(payload.RootElement.GetProperty("signedDate").GetInt64());
            if (signedAtUtc > _time.GetUtcNow().AddMinutes(5))
                throw new AppleVerificationException();
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.ExtraStore.Add(intermediate);
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.RevocationMode = _checkRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.VerificationTime = signedAtUtc.UtcDateTime;
            if (!chain.Build(leaf) || chain.ChainElements.Count != 3
                || !chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(root.RawData))
                throw new AppleVerificationException();
            using var publicKey = leaf.GetECDsaPublicKey();
            if (publicKey is null || !publicKey.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                    AppleJwt.Decode(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new AppleVerificationException();
            return JsonDocument.Parse(payload.RootElement.GetRawText());
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException
            or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new AppleVerificationException();
        }
    }

    private static byte[] ReadAppleRoot()
    {
        using var stream = typeof(AppStoreJwsVerifier).Assembly.GetManifestResourceStream("Orbit.Infrastructure.Certificates.AppleRootCA-G3.cer")
            ?? throw new InvalidOperationException("Apple root certificate is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
