using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Orbit.Infrastructure.Services;

internal static class AppAttestReceiptVerifier
{
    internal static bool Verify(byte[] receipt, X509Certificate2 attestedCertificate, string appId,
        DateTimeOffset now, byte[] rootCertificate)
    {
        var cms = new SignedCms();
        cms.Decode(receipt);
        if (cms.SignerInfos.Count != 1 || cms.SignerInfos[0].Certificate is not { } signer)
            return false;
        cms.CheckSignature(verifySignatureOnly: true);
        using var root = X509CertificateLoader.LoadCertificate(rootCertificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ExtraStore.AddRange(cms.Certificates);
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        if (!chain.Build(signer) || !chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(root.RawData))
            return false;
        var fields = ParseFields(cms.ContentInfo.Content);
        if (!fields.TryGetValue(2, out var receiptAppId) || Encoding.UTF8.GetString(receiptAppId) != appId
            || !fields.TryGetValue(3, out var certificate) || !certificate.AsSpan().SequenceEqual(attestedCertificate.RawData)
            || !fields.TryGetValue(6, out var type) || Encoding.UTF8.GetString(type) != "ATTEST"
            || !fields.TryGetValue(12, out var created)
            || !DateTimeOffset.TryParse(Encoding.UTF8.GetString(created), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var createdAtUtc))
            return false;
        var age = now - createdAtUtc;
        return age >= TimeSpan.Zero && age <= TimeSpan.FromMinutes(5);
    }

    private static Dictionary<int, byte[]> ParseFields(byte[] content)
    {
        var reader = new AsnReader(content, AsnEncodingRules.BER);
        var set = reader.ReadSetOf(skipSortOrderValidation: true);
        var fields = new Dictionary<int, byte[]>();
        while (set.HasData)
        {
            var field = set.ReadSequence();
            var type = checked((int)field.ReadInteger());
            field.ReadInteger();
            fields.Add(type, field.ReadOctetString());
            field.ThrowIfNotEmpty();
        }
        reader.ThrowIfNotEmpty();
        return fields;
    }
}
